namespace DotYjs.Tests;

/// <summary>Regression tests for edge cases around garbage-collected items and untyped root placeholders.</summary>
public class GcEdgeCaseTests
{
    private static Dictionary<string, object?> Attrs(params (string, object?)[] kv) => kv.ToDictionary(x => x.Item1, x => x.Item2);

    [Fact]
    public void Insert_Into_Nested_Type_Whose_Parent_Was_Deleted_And_Gcd_Concurrently()
    {
        var d1 = new Doc();
        var d2 = new Doc();
        var nested = new YText();
        d1.GetMap("m").Set("t", nested);
        UpdateCodec.ApplyUpdate(d2, UpdateCodec.EncodeStateAsUpdate(d1));

        // peer 1 deletes the entry (GC replaces the parent's content with ContentDeleted), peer 2 inserts into it
        d1.GetMap("m").Delete("t");
        ((YText)d2.GetMap("m").Get("t")!).Insert(0, "late");

        var u2 = UpdateCodec.EncodeStateAsUpdate(d2, UpdateCodec.EncodeStateVector(d1));
        UpdateCodec.ApplyUpdate(d1, u2); // must not throw
        var u1 = UpdateCodec.EncodeStateAsUpdate(d1, UpdateCodec.EncodeStateVector(d2));
        UpdateCodec.ApplyUpdate(d2, u1);

        Assert.False(d1.GetMap("m").Has("t"));
        Assert.False(d2.GetMap("m").Has("t"));
        Assert.Equal(UpdateCodec.EncodeStateVector(d1), UpdateCodec.EncodeStateVector(d2));
    }

    [Fact]
    public void Xml_QuerySelector_Skips_Gcd_First_Child()
    {
        var d = new Doc();
        var frag = d.GetXmlFragment("x");
        frag.Insert(0, new YXmlElement("a"), new YXmlElement("b"));
        frag.Delete(0, 1); // first item is now deleted and garbage collected

        var b = frag.QuerySelector("b");
        Assert.NotNull(b);
        Assert.Equal("b", ((YXmlElement)b!).NodeName);
        Assert.Null(frag.QuerySelector("a"));
        Assert.Single(frag.QuerySelectorAll("b"));
        Assert.Single(frag.CreateTreeWalker(_ => true));
    }

    [Fact]
    public void Formatted_Text_Received_Before_GetText_Keeps_Formatting_Consistent()
    {
        var src = new Doc();
        var t = src.GetText("t");
        t.Insert(0, "abc");
        t.Format(0, 2, Attrs(("bold", true)));

        var d = new Doc();
        UpdateCodec.ApplyUpdate(d, UpdateCodec.EncodeStateAsUpdate(src)); // root is an untyped placeholder here
        var text = d.GetText("t");
        Assert.True(text.HasFormatting);
        Assert.Null(text.SearchMarker);

        text.Delete(1, 1);
        var delta = text.ToDelta();
        Assert.Equal(2, delta.Count);
        Assert.Equal("a", delta[0].Insert);
        Assert.Equal(true, delta[0].Attributes!["bold"]);
        Assert.Equal("c", delta[1].Insert);
        Assert.Null(delta[1].Attributes);
    }
}
