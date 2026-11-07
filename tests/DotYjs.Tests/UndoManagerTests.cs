namespace DotYjs.Tests;

public class UndoManagerTests
{
    private static Dictionary<string, object?> Attrs(params (string, object?)[] kv) => kv.ToDictionary(x => x.Item1, x => x.Item2);

    private static Doc[] Docs(int n)
    {
        var docs = new Doc[n];
        for (int i = 0; i < n; i++) docs[i] = new Doc { ClientID = i + 1 };
        return docs;
    }

    private static void SyncAll(params Doc[] docs)
    {
        foreach (var a in docs)
        {
            foreach (var b in docs)
            {
                if (a == b) continue;
                // remote changes carry an origin that the UndoManager does not track (like y-websocket does)
                UpdateCodec.ApplyUpdate(b, UpdateCodec.EncodeStateAsUpdate(a, UpdateCodec.EncodeStateVector(b)), "remote");
            }
        }
    }

    // ports of yjs tests/undo-redo.tests.js; a large capture timeout makes merging deterministic
    private static UndoManagerOptions Opts() => new() { CaptureTimeout = 100_000 };

    [Fact]
    public void Undo_Text()
    {
        var docs = Docs(2);
        var text0 = docs[0].GetText("text");
        var text1 = docs[1].GetText("text");
        var um = new UndoManager(text0, Opts());

        // items that are added & deleted in the same transaction won't be undone
        text0.Insert(0, "test");
        text0.Delete(0, 4);
        um.Undo();
        Assert.Equal("", text0.ToString());

        // follow redone items
        text0.Insert(0, "a");
        um.StopCapturing();
        text0.Delete(0, 1);
        um.StopCapturing();
        um.Undo();
        Assert.Equal("a", text0.ToString());
        um.Undo();
        Assert.Equal("", text0.ToString());

        text0.Insert(0, "abc");
        text1.Insert(0, "xyz");
        SyncAll(docs);
        um.Undo();
        Assert.Equal("xyz", text0.ToString());
        um.Redo();
        Assert.Equal("abcxyz", text0.ToString());
        SyncAll(docs);
        text1.Delete(0, 1);
        SyncAll(docs);
        um.Undo();
        Assert.Equal("xyz", text0.ToString());
        um.Redo();
        Assert.Equal("bcxyz", text0.ToString());
        // test marks
        text0.Format(1, 3, Attrs(("bold", true)));
        var delta = text0.ToDelta();
        Assert.Equal(3, delta.Count);
        Assert.Equal("cxy", delta[1].Insert);
        Assert.Equal(true, delta[1].Attributes!["bold"]);
        um.Undo();
        delta = text0.ToDelta();
        Assert.Single(delta);
        Assert.Equal("bcxyz", delta[0].Insert);
        um.Redo();
        delta = text0.ToDelta();
        Assert.Equal(3, delta.Count);
        Assert.Equal("cxy", delta[1].Insert);
    }

    [Fact]
    public void Undo_Map()
    {
        var docs = Docs(2);
        var map0 = docs[0].GetMap("map");
        var map1 = docs[1].GetMap("map");
        map0.Set("a", 0L);
        var um = new UndoManager(map0, Opts());
        map0.Set("a", 1L);
        um.Undo();
        Assert.Equal(0L, map0.Get("a"));
        um.Redo();
        Assert.Equal(1L, map0.Get("a"));
        // sub-types: restore a whole type
        var sub = new YMap();
        map0.Set("a", sub);
        sub.Set("x", 42L);
        Assert.Equal(42L, ((YMap)map0.Get("a")!).Get("x"));
        um.Undo();
        Assert.Equal(1L, map0.Get("a"));
        um.Redo();
        Assert.Equal(42L, ((YMap)map0.Get("a")!).Get("x"));
        SyncAll(docs);
        // if content is overwritten by another user, undo operations should be skipped
        map1.Set("a", 44L);
        SyncAll(docs);
        um.Undo();
        Assert.Equal(44L, map0.Get("a"));
        um.Redo();
        Assert.Equal(44L, map0.Get("a"));

        // setting a value multiple times
        map0.Set("b", "initial");
        um.StopCapturing();
        map0.Set("b", "val1");
        map0.Set("b", "val2");
        um.StopCapturing();
        um.Undo();
        Assert.Equal("initial", map0.Get("b"));
    }

    [Fact]
    public void Undo_Array()
    {
        var docs = Docs(2);
        var array0 = docs[0].GetArray("array");
        var array1 = docs[1].GetArray("array");
        var um = new UndoManager(array0, Opts());
        array0.Insert(0, new object?[] { 1L, 2L, 3L });
        array1.Insert(0, new object?[] { 4L, 5L, 6L });
        SyncAll(docs);
        Assert.Equal(new object?[] { 1L, 2L, 3L, 4L, 5L, 6L }, array0.ToArray());
        um.Undo();
        Assert.Equal(new object?[] { 4L, 5L, 6L }, array0.ToArray());
        um.Redo();
        Assert.Equal(new object?[] { 1L, 2L, 3L, 4L, 5L, 6L }, array0.ToArray());
        SyncAll(docs);
        array1.Delete(0, 1); // user1 deletes [1]
        SyncAll(docs);
        um.Undo();
        Assert.Equal(new object?[] { 4L, 5L, 6L }, array0.ToArray());
        um.Redo();
        Assert.Equal(new object?[] { 2L, 3L, 4L, 5L, 6L }, array0.ToArray());
        array0.Delete(0, 5);
        // nested structure
        var ymap = new YMap();
        array0.Insert(0, new object?[] { ymap });
        Assert.Equal("[{}]", Json(array0));
        um.StopCapturing();
        ymap.Set("a", 1L);
        Assert.Equal("[{\"a\":1}]", Json(array0));
        um.Undo();
        Assert.Equal("[{}]", Json(array0));
        um.Undo();
        Assert.Equal("[2,3,4,5,6]", Json(array0));
        um.Redo();
        Assert.Equal("[{}]", Json(array0));
        um.Redo();
        Assert.Equal("[{\"a\":1}]", Json(array0));
        SyncAll(docs);
        ((YMap)array1.Get(0)!).Set("b", 2L);
        SyncAll(docs);
        AssertMap(array0, ("a", 1L), ("b", 2L));
        um.Undo();
        AssertMap(array0, ("b", 2L));
        um.Undo();
        Assert.Equal("[2,3,4,5,6]", Json(array0));
        um.Redo();
        AssertMap(array0, ("b", 2L));
        um.Redo();
        AssertMap(array0, ("a", 1L), ("b", 2L));
    }

    private static void AssertMap(YArray arr, params (string, object?)[] kv)
    {
        Assert.Equal(1, arr.Length);
        var m = (YMap)arr.Get(0)!;
        Assert.Equal(kv.Length, m.Size);
        foreach (var (k, v) in kv) Assert.Equal(v, m.Get(k));
    }

    private static string Json(AbstractType t) => Lib0.JsonValues.Stringify(t.ToJSON());

    [Fact]
    public void Undo_Xml()
    {
        var doc = new Doc();
        var xml0 = doc.GetXmlElement("undefined");
        var um = new UndoManager(xml0, Opts());
        var child = new YXmlElement("p");
        xml0.Insert(0, child);
        var textchild = new YXmlText("content");
        child.Insert(0, textchild);
        Assert.Equal("<undefined><p>content</p></undefined>", xml0.ToString());
        um.StopCapturing();
        textchild.Format(3, 4, Attrs(("bold", new Dictionary<string, object?>())));
        Assert.Equal("<undefined><p>con<bold>tent</bold></p></undefined>", xml0.ToString());
        um.Undo();
        Assert.Equal("<undefined><p>content</p></undefined>", xml0.ToString());
        um.Redo();
        Assert.Equal("<undefined><p>con<bold>tent</bold></p></undefined>", xml0.ToString());
        xml0.Delete(0, 1);
        Assert.Equal("<undefined></undefined>", xml0.ToString());
        um.Undo();
        Assert.Equal("<undefined><p>con<bold>tent</bold></p></undefined>", xml0.ToString());
    }

    [Fact]
    public void Undo_Events_And_Meta()
    {
        var doc = new Doc();
        var text0 = doc.GetText("text");
        var um = new UndoManager(text0, Opts());
        int counter = 0;
        long receivedMetadata = -1;
        um.StackItemAdded += (e, _) =>
        {
            Assert.True(e.ChangedParentTypes.ContainsKey(text0));
            e.StackItem.Meta["test"] = (long)counter++;
        };
        um.StackItemPopped += (e, _) =>
        {
            Assert.True(e.ChangedParentTypes.ContainsKey(text0));
            receivedMetadata = (long)e.StackItem.Meta["test"]!;
        };
        text0.Insert(0, "abc");
        um.Undo();
        Assert.Equal(0, receivedMetadata);
        um.Redo();
        Assert.Equal(1, receivedMetadata);
    }

    [Fact]
    public void Tracked_Origins_By_Class()
    {
        var doc = new Doc();
        var text0 = doc.GetText("text");
        var um = new UndoManager(text0, new UndoManagerOptions { CaptureTimeout = 100_000, TrackedOrigins = new HashSet<object?> { typeof(int) } });
        doc.Transact(_ => text0.Insert(0, "abc"), 42);
        Assert.Equal("abc", text0.ToString());
        um.Undo();
        Assert.Equal("", text0.ToString());
        // a string origin is not tracked
        doc.Transact(_ => text0.Insert(0, "xyz"), "untracked");
        Assert.False(um.CanUndo);
    }

    [Fact]
    public void Type_Scope()
    {
        var doc = new Doc();
        var array0 = doc.GetArray("array");
        var text0 = new YText();
        var text1 = new YText();
        array0.Insert(0, new object?[] { text0, text1 });
        var um = new UndoManager(text0, Opts());
        text1.Insert(0, "abc");
        um.Undo();
        Assert.Equal("abc", text1.ToString());
        text0.Insert(0, "abc");
        um.Undo();
        Assert.Equal("", text0.ToString());
    }

    [Fact]
    public void Delete_Filter()
    {
        var doc = new Doc();
        var array0 = doc.GetArray("array");
        var um = new UndoManager(array0, new UndoManagerOptions
        {
            CaptureTimeout = 100_000,
            DeleteFilter = item => item.Content is ContentType ct && ct.Type.Map.Count == 0
        });
        var map0 = new YMap();
        map0.Set("hi", 1L);
        var map1 = new YMap();
        array0.Insert(0, new object?[] { map0, map1 });
        um.Undo();
        Assert.Equal(1, array0.Length);
        Assert.Single(((YMap)array0.Get(0)!).Keys);
    }

    [Fact]
    public void Undo_Until_Change_Performed()
    {
        var doc = new Doc();
        var doc2 = new Doc();
        doc.Update += (update, origin, _, _) => UpdateCodec.ApplyUpdate(doc2, update, "remote");
        doc2.Update += (update, origin, _, _) => UpdateCodec.ApplyUpdate(doc, update, "remote");

        var yArray = doc.GetArray("array");
        var yArray2 = doc2.GetArray("array");
        var yMap = new YMap();
        yMap.Set("hello", "world");
        yArray.Push(new object?[] { yMap });
        var yMap2 = new YMap();
        yMap2.Set("key", "value");
        yArray.Push(new object?[] { yMap2 });

        var um = new UndoManager(yArray, new UndoManagerOptions { CaptureTimeout = 100_000, TrackedOrigins = new HashSet<object?> { doc.ClientID } });
        var um2 = new UndoManager(yArray2, new UndoManagerOptions { CaptureTimeout = 100_000, TrackedOrigins = new HashSet<object?> { doc2.ClientID } });

        doc.Transact(_ => yMap2.Set("key", "value modified"), doc.ClientID);
        um.StopCapturing();
        doc.Transact(_ => yMap.Set("hello", "world modified"), doc.ClientID);
        doc2.Transact(_ => yArray2.Delete(0), doc2.ClientID);
        um2.Undo();
        um.Undo();
        Assert.Equal("value", yMap2.Get("key"));
    }

    [Fact]
    public void Global_Scope_Tracks_Whole_Doc()
    {
        var doc = new Doc();
        var um = new UndoManager(doc, Opts());
        var text = doc.GetText("t");
        var map = doc.GetMap("m");
        text.Insert(0, "abc");
        um.StopCapturing();
        map.Set("k", 1L);
        um.Undo();
        Assert.False(map.Has("k"));
        Assert.Equal("abc", text.ToString());
        um.Undo();
        Assert.Equal("", text.ToString());
        Assert.True(um.CanRedo);
        um.Redo();
        um.Redo();
        Assert.Equal("abc", text.ToString());
        Assert.Equal(1L, map.Get("k"));
    }

    [Fact]
    public void Empty_Transactions_Are_Not_Captured_With_Doc_Scope()
    {
        var doc = new Doc();
        var text = doc.GetText("t");
        var um = new UndoManager(doc, Opts());
        text.Insert(0, "a");
        um.Clear();
        Assert.False(um.CanUndo); // Clear()'s own (empty) transaction must not create a stack item
        text.Insert(0, "b");
        um.Undo();
        Assert.True(um.CanRedo);
        doc.Transact(_ => { }); // a transaction that changes nothing must not wipe the redo stack
        Assert.True(um.CanRedo);
        um.Redo();
        Assert.Equal("ba", text.ToString());
    }

    [Fact]
    public void Clear_And_Destroy()
    {
        var doc = new Doc();
        var text = doc.GetText("t");
        var um = new UndoManager(text, Opts());
        bool cleared = false;
        um.StackCleared += (e, _) => cleared = e.UndoStackCleared;
        text.Insert(0, "a");
        Assert.True(um.CanUndo);
        um.Clear();
        Assert.True(cleared);
        Assert.False(um.CanUndo);
        um.Destroy();
        text.Insert(0, "b");
        Assert.False(um.CanUndo);
    }

    [Fact]
    public void Undo_Interops_With_Remote_Peer()
    {
        // The undo is just regular ops: a second peer must converge to the same state
        var docs = Docs(2);
        var t0 = docs[0].GetText("t");
        var t1 = docs[1].GetText("t");
        var um = new UndoManager(t0, Opts());
        t0.Insert(0, "hello");
        SyncAll(docs);
        t1.Insert(5, " world");
        SyncAll(docs);
        um.Undo();
        Assert.Equal(" world", t0.ToString());
        SyncAll(docs);
        Assert.Equal(" world", t1.ToString());
        um.Redo();
        SyncAll(docs);
        Assert.Equal("hello world", t1.ToString());
        Assert.Equal(UpdateCodec.EncodeStateVector(docs[0]), UpdateCodec.EncodeStateVector(docs[1]));
    }
}
