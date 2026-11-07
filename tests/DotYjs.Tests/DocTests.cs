namespace DotYjs.Tests;

public class DocTests
{
    private static void Sync(Doc a, Doc b)
    {
        UpdateCodec.ApplyUpdate(b, UpdateCodec.EncodeStateAsUpdate(a, UpdateCodec.EncodeStateVector(b)));
        UpdateCodec.ApplyUpdate(a, UpdateCodec.EncodeStateAsUpdate(b, UpdateCodec.EncodeStateVector(a)));
    }

    [Fact]
    public void Text_InsertDelete_And_Sync()
    {
        var d1 = new Doc();
        var d2 = new Doc();
        var t1 = d1.GetText("t");
        t1.Insert(0, "hello world");
        t1.Delete(5, 6);
        t1.Insert(5, ", yjs");
        Sync(d1, d2);
        Assert.Equal("hello, yjs", d2.GetText("t").ToString());
    }

    [Fact]
    public void Concurrent_Inserts_Converge()
    {
        var d1 = new Doc();
        var d2 = new Doc();
        d1.GetText("t").Insert(0, "abc");
        Sync(d1, d2);
        d1.GetText("t").Insert(1, "X");
        d2.GetText("t").Insert(1, "Y");
        Sync(d1, d2);
        Assert.Equal(d1.GetText("t").ToString(), d2.GetText("t").ToString());
        Assert.Equal(5, d1.GetText("t").Length);
    }

    [Fact]
    public void Map_And_Array_Nested()
    {
        var d1 = new Doc();
        var map = d1.GetMap("m");
        map.Set("num", 1L);
        map.Set("str", "s");
        var arr = new YArray();
        map.Set("arr", arr);
        arr.Push(1L, 2L, "three");
        arr.Insert(1, new Dictionary<string, object?> { ["k"] = "v" });
        arr.Delete(0);

        var d2 = new Doc();
        Sync(d1, d2);
        var m2 = d2.GetMap("m");
        Assert.Equal(1L, m2.Get("num"));
        Assert.Equal("s", m2.Get("str"));
        var a2 = (YArray)m2.Get("arr")!;
        Assert.Equal(3, a2.Length);
        Assert.Equal("three", a2.Get(2));
        Assert.Equal("v", ((Dictionary<string, object?>)a2.Get(0)!)["k"]);
    }

    [Fact]
    public void Update_Event_Emitted_And_Applies()
    {
        var d1 = new Doc();
        var d2 = new Doc();
        d1.Update += (update, _, _, _) => UpdateCodec.ApplyUpdate(d2, update);
        d1.GetArray("a").Push(1L, 2L, 3L);
        d1.GetArray("a").Delete(1);
        Assert.Equal(new object?[] { 1L, 3L }, d2.GetArray("a").ToArray());
    }

    [Fact]
    public void V2_Encoding_RoundTrip()
    {
        var d1 = new Doc();
        var t = d1.GetText("t");
        t.Insert(0, "hello");
        t.Format(0, 2, new Dictionary<string, object?> { ["bold"] = true });
        d1.GetMap("m").Set("k", new List<object?> { 1L, 2L });
        var u2 = UpdateCodec.EncodeStateAsUpdateV2(d1);
        var d2 = new Doc();
        UpdateCodec.ApplyUpdateV2(d2, u2);
        Assert.Equal("hello", d2.GetText("t").ToString());
        var delta = d2.GetText("t").ToDelta();
        Assert.Equal(2, delta.Count);
        Assert.Equal("he", delta[0].Insert);
        Assert.Equal(true, delta[0].Attributes!["bold"]);
        // v1 -> v2 -> v1 conversion round trip
        var u1 = UpdateCodec.EncodeStateAsUpdate(d1);
        Assert.Equal(u1, Updates.ConvertUpdateFormatV2ToV1(Updates.ConvertUpdateFormatV1ToV2(u1)));
    }

    [Fact]
    public void MergeUpdates_Equals_FullState()
    {
        var d1 = new Doc();
        var updates = new List<byte[]>();
        d1.Update += (u, _, _, _) => updates.Add(u);
        d1.GetText("t").Insert(0, "abc");
        d1.GetText("t").Insert(3, "def");
        d1.GetText("t").Delete(1, 2);
        var merged = Updates.MergeUpdates(updates);
        var d2 = new Doc();
        UpdateCodec.ApplyUpdate(d2, merged);
        Assert.Equal("adef", d2.GetText("t").ToString());
        var sv = Updates.EncodeStateVectorFromUpdate(merged);
        Assert.Equal(UpdateCodec.EncodeStateVector(d1), sv);
    }

    [Fact]
    public void Observe_Delta()
    {
        var d = new Doc();
        var t = d.GetText("t");
        t.Insert(0, "hello");
        List<DeltaOp>? delta = null;
        t.Observe((e, _) => delta = e.Delta);
        t.Insert(5, " world", new Dictionary<string, object?> { ["italic"] = true });
        Assert.NotNull(delta);
        Assert.Equal(5, delta![0].Retain);
        Assert.Equal(" world", delta[1].Insert);
    }

    [Fact]
    public void Xml_ToString()
    {
        var d = new Doc();
        var frag = d.GetXmlFragment("x");
        var p = new YXmlElement("p");
        frag.Insert(0, p);
        p.SetAttribute("class", "a");
        p.Insert(0, new YXmlText("hi"));
        Assert.Equal("<p class=\"a\">hi</p>", frag.ToString());
    }

    [Fact]
    public void Pending_Structs_Applied_When_Dependency_Arrives()
    {
        var d1 = new Doc();
        var updates = new List<byte[]>();
        d1.Update += (u, _, _, _) => updates.Add(u);
        d1.GetText("t").Insert(0, "a");
        d1.GetText("t").Insert(1, "b");
        var d2 = new Doc();
        UpdateCodec.ApplyUpdate(d2, updates[1]); // out of order
        Assert.Equal("", d2.GetText("t").ToString());
        UpdateCodec.ApplyUpdate(d2, updates[0]);
        Assert.Equal("ab", d2.GetText("t").ToString());
    }
}
