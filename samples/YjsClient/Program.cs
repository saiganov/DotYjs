using DotYjs;
using DotYjs.Lib0;
using DotYjs.Protocols;

// Connects to any y-websocket compatible server, appends text to the shared YText "t", sets an awareness state and
// prints the synced document as JSON. Used by tests/interop/check-dotnet-client.mjs against the JS y-websocket server.
//   dotnet run --project samples/YjsClient -- ws://localhost:1234 my-room "text to append"

var serverUrl = args.Length > 0 ? args[0] : "ws://localhost:1234";
var room = args.Length > 1 ? args[1] : "dotnet-room";
var text = args.Length > 2 ? args[2] : "hello from .NET";

var doc = new Doc();
await using var provider = new YjsWebSocketProvider(serverUrl, room, doc);
provider.StatusChanged += s => Console.Error.WriteLine($"status: {s}");
provider.ConnectionError += e => Console.Error.WriteLine($"error: {e.Message}");

await provider.WhenSynced.WaitAsync(TimeSpan.FromSeconds(10));
Console.Error.WriteLine("synced");
provider.Awareness.SetLocalState(new Dictionary<string, object?> { ["user"] = new Dictionary<string, object?> { ["name"] = "dotnet" } });
doc.Transact(_ =>
{
    var t = doc.GetText("t");
    t.Insert((int)t.Length, text);
});
// give the server a moment to broadcast and other peers to answer
await Task.Delay(500);
Console.WriteLine(JsonValues.Stringify(new Dictionary<string, object?>
{
    ["clientID"] = doc.ClientID,
    ["text"] = doc.GetText("t").ToString(),
    ["awareness"] = provider.Awareness.GetStates().ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
}));
