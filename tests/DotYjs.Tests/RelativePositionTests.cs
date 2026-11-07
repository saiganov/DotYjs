namespace DotYjs.Tests;

public class RelativePositionTests
{
    private static void CheckRelativePositions(YText ytext)
    {
        // test if all positions are encoded and restored correctly
        for (long i = 0; i < ytext.Length; i++)
        {
            for (long assoc = -1; assoc < 2; assoc++)
            {
                var rpos = RelativePosition.FromTypeIndex(ytext, i, assoc);
                var decoded = RelativePosition.Decode(rpos.Encode());
                var abs = decoded.ToAbsolutePosition(ytext.Doc!);
                Assert.NotNull(abs);
                Assert.Equal(i, abs!.Index);
                Assert.Equal(assoc, abs.Assoc);
                Assert.Same(ytext, abs.Type);
            }
        }
    }

    [Fact]
    public void Case1()
    {
        var ydoc = new Doc();
        var ytext = ydoc.GetText();
        ytext.Insert(0, "1");
        ytext.Insert(0, "abc");
        ytext.Insert(0, "z");
        ytext.Insert(0, "y");
        ytext.Insert(0, "x");
        CheckRelativePositions(ytext);
    }

    [Fact]
    public void Case3()
    {
        var ydoc = new Doc();
        var ytext = ydoc.GetText();
        ytext.Insert(0, "abc");
        ytext.Insert(0, "1");
        ytext.Insert(0, "xyz");
        CheckRelativePositions(ytext);
    }

    [Fact]
    public void Empty_Text()
    {
        var ydoc = new Doc();
        var ytext = ydoc.GetText();
        CheckRelativePositions(ytext);
        var end = RelativePosition.FromTypeIndex(ytext, 0);
        Assert.Equal("", end.TName);
        Assert.Null(end.Item);
        Assert.Equal(0, end.ToAbsolutePosition(ydoc)!.Index);
    }

    [Fact]
    public void Association_Difference()
    {
        var ydoc = new Doc();
        var ytext = ydoc.GetText();
        ytext.Insert(0, "2");
        ytext.Insert(0, "1");
        var rposRight = RelativePosition.FromTypeIndex(ytext, 1, 0);
        var rposLeft = RelativePosition.FromTypeIndex(ytext, 1, -1);
        ytext.Insert(1, "x");
        Assert.Equal(2, rposRight.ToAbsolutePosition(ydoc)!.Index);
        Assert.Equal(1, rposLeft.ToAbsolutePosition(ydoc)!.Index);
    }

    [Fact]
    public void With_Undo()
    {
        var ydoc = new Doc();
        var ytext = ydoc.GetText();
        ytext.Insert(0, "hello world");
        var rpos = RelativePosition.FromTypeIndex(ytext, 1);
        var um = new UndoManager(ytext);
        ytext.Delete(0, 6);
        Assert.Equal(0, rpos.ToAbsolutePosition(ydoc)!.Index);
        um.Undo();
        Assert.Equal(1, rpos.ToAbsolutePosition(ydoc)!.Index);
        Assert.Equal(6, rpos.ToAbsolutePosition(ydoc, followUndoneDeletions: false)!.Index);
        var clone = new Doc();
        UpdateCodec.ApplyUpdate(clone, UpdateCodec.EncodeStateAsUpdate(ydoc));
        Assert.Equal(6, rpos.ToAbsolutePosition(clone)!.Index);
        Assert.Equal(6, rpos.ToAbsolutePosition(clone, false)!.Index);
    }

    [Fact]
    public void Nested_Type_And_Json_Round_Trip()
    {
        var ydoc = new Doc();
        var arr = ydoc.GetArray("a");
        var nested = new YText();
        arr.Insert(0, new object?[] { nested });
        nested.Insert(0, "abc");
        var end = RelativePosition.FromTypeIndex(nested, 3);
        Assert.NotNull(end.Type);
        Assert.Null(end.TName);
        var json = end.ToJSON();
        var back = RelativePosition.FromJSON(json);
        Assert.Equal(end, back);
        Assert.Equal(end, RelativePosition.Decode(end.Encode()));
        var abs = end.ToAbsolutePosition(ydoc)!;
        Assert.Same(nested, abs.Type);
        Assert.Equal(3, abs.Index);
        // unknown position → null
        Assert.Null(new RelativePosition(null, null, new ID(999, 0)).ToAbsolutePosition(ydoc));
    }

    [Fact]
    public void Wire_Format_Matches_Yjs()
    {
        // yjs: writeVarUint(0) + writeID(client, clock) + writeVarInt(assoc)
        var rp = new RelativePosition(null, null, new ID(5, 7), -1);
        Assert.Equal(new byte[] { 0, 5, 7, 0x41 }, rp.Encode());
        // tname case: uint8 1 + varstring + assoc 0
        var rn = new RelativePosition(null, "t", null, 0);
        Assert.Equal(new byte[] { 1, 1, (byte)'t', 0 }, rn.Encode());
    }
}
