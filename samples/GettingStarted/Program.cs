using DotYjs;
using DotYjs.Lib0;

// A tour of the core DotYjs API without any network: two documents play the role of two users and exchange
// binary updates directly. Exactly the same bytes would work with a JavaScript yjs client.
//   dotnet run --project samples/GettingStarted

// 1. Shared types: YText, YMap, YArray.
var alice = new Doc();
var text = alice.GetText("content");
text.Insert(0, "Hello world");
text.Format(0, 5, new Dictionary<string, object?> { ["bold"] = true });

var settings = alice.GetMap("settings");
settings.Set("title", "Shopping list");
settings.Set("public", false);

var items = alice.GetArray("items");
items.Push("milk", "bread");

Section("Alice's document");
Console.WriteLine(JsonValues.Stringify(alice.ToJSON()));
Console.WriteLine("Delta of the text: " + JsonValues.Stringify(text.ToDelta().Select(op => op.ToDictionary()).ToList()));

// 2. Sync a second document: the full state is one update.
var bob = new Doc();
UpdateCodec.ApplyUpdate(bob, UpdateCodec.EncodeStateAsUpdate(alice));
// Root types are typed by the first Get call, like in JavaScript; until then ToJSON() cannot know what they are.
bob.GetText("content");
bob.GetMap("settings");
bob.GetArray("items");
Section("Bob after the initial sync");
Console.WriteLine(JsonValues.Stringify(bob.ToJSON()));

// 3. Live sync: every transaction raises Update with the bytes to send to the other side.
//    The origin tells a remote change apart from a local one, so updates do not bounce back and forth.
var remote = new object();
alice.Update += (update, origin, _, _) => { if (origin != remote) UpdateCodec.ApplyUpdate(bob, update, remote); };
bob.Update += (update, origin, _, _) => { if (origin != remote) UpdateCodec.ApplyUpdate(alice, update, remote); };

// 4. Observe changes.
bob.GetText("content").Observe((e, _) =>
    Console.WriteLine("  Bob sees a text change: " + JsonValues.Stringify(((YTextEvent)e).Delta.Select(op => op.ToDictionary()).ToList())));
bob.GetMap("settings").Observe((e, _) =>
    Console.WriteLine("  Bob sees map keys changed: " + string.Join(", ", ((YMapEvent)e).KeysChanged)));

Section("Concurrent edits");
alice.Transact(_ =>
{
    text.Insert(text.Length, "!");
    settings.Set("public", true);
});
bob.GetArray("items").Insert(0, "eggs");
Console.WriteLine("Alice: " + text + " | items: " + JsonValues.Stringify(items.ToArray()));
Console.WriteLine("Bob:   " + bob.GetText("content") + " | items: " + JsonValues.Stringify(bob.GetArray("items").ToArray()));

// 5. Offline edits: both sides change the document while disconnected, then exchange only what the other is missing.
Section("Offline edits merged with state vectors");
var carol = new Doc();
UpdateCodec.ApplyUpdate(carol, UpdateCodec.EncodeStateAsUpdate(alice));
carol.GetText("content").Insert(0, ">> ");        // Carol is offline
text.Insert(text.Length, " (edited by Alice)");   // Alice keeps working
var missingForCarol = UpdateCodec.EncodeStateAsUpdate(alice, UpdateCodec.EncodeStateVector(carol));
var missingForAlice = UpdateCodec.EncodeStateAsUpdate(carol, UpdateCodec.EncodeStateVector(alice));
UpdateCodec.ApplyUpdate(carol, missingForCarol);
UpdateCodec.ApplyUpdate(alice, missingForAlice, "carol"); // not `remote`, so Alice forwards Carol's edit to Bob
Console.WriteLine($"Alice: {text}");
Console.WriteLine($"Carol: {carol.GetText("content")}");
Console.WriteLine($"Bob:   {bob.GetText("content")}");
Console.WriteLine($"Diff sizes: {missingForCarol.Length} and {missingForAlice.Length} bytes; full state: {UpdateCodec.EncodeStateAsUpdate(alice).Length} bytes (v1), {UpdateCodec.EncodeStateAsUpdateV2(alice).Length} bytes (v2)");

// 6. Relative positions: a cursor that stays on the same character while others edit.
Section("Relative positions");
var cursor = RelativePosition.FromTypeIndex(text, text.ToString().IndexOf("world", StringComparison.Ordinal));
text.Insert(0, "Well, ");
var index = cursor.ToAbsolutePosition(alice)!.Index;
Console.WriteLine($"The cursor still points at \"{text.ToString().Substring((int)index, 5)}\" (index {index})");

// 7. Undo/redo of local changes only: Bob's edits are not undone by Alice's UndoManager.
Section("Undo manager");
var undo = new UndoManager(text);
text.Insert(text.Length, " [typo]");
undo.StopCapturing();
bob.GetText("content").Insert(0, "* ");
Console.WriteLine("Before undo: " + text);
undo.Undo();
Console.WriteLine("After undo:  " + text);
undo.Redo();
Console.WriteLine("After redo:  " + text);

static void Section(string title) => Console.WriteLine($"{Environment.NewLine}== {title}");
