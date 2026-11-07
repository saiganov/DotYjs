using DotYjs.Lib0;

namespace DotYjs.Tests;

public class Lib0Tests
{
    [Theory]
    [InlineData(0L, new byte[] { 0 })]
    [InlineData(127L, new byte[] { 127 })]
    [InlineData(128L, new byte[] { 0x80, 1 })]
    [InlineData(300L, new byte[] { 0xac, 2 })]
    [InlineData(4294967295L, new byte[] { 0xff, 0xff, 0xff, 0xff, 0x0f })]
    public void VarUint_RoundTrip(long value, byte[] expected)
    {
        var e = new Encoder();
        e.WriteVarUint(value);
        Assert.Equal(expected, e.ToArray());
        Assert.Equal(value, new Decoder(expected).ReadVarUint());
    }

    [Theory]
    [InlineData(0L, new byte[] { 0 })]
    [InlineData(1L, new byte[] { 1 })]
    [InlineData(-1L, new byte[] { 0x41 })]
    [InlineData(63L, new byte[] { 63 })]
    [InlineData(64L, new byte[] { 0x80, 1 })]
    [InlineData(-64L, new byte[] { 0xc0, 1 })]
    [InlineData(-1000L, new byte[] { 0xe8, 0x0f })]
    public void VarInt_RoundTrip(long value, byte[] expected)
    {
        var e = new Encoder();
        e.WriteVarInt(value);
        Assert.Equal(expected, e.ToArray());
        Assert.Equal(value, new Decoder(expected).ReadVarInt());
    }

    [Fact]
    public void VarInt_NegativeZero()
    {
        var e = new Encoder();
        e.WriteVarInt(0, negativeZero: true);
        Assert.Equal(new byte[] { 0x40 }, e.ToArray());
        var d = new Decoder(e.ToArray());
        Assert.Equal(0, d.ReadVarInt(out var neg));
        Assert.True(neg);
    }

    [Fact]
    public void Any_RoundTrip()
    {
        var value = new Dictionary<string, object?>
        {
            ["s"] = "héllo 🙂",
            ["i"] = 42L,
            ["neg"] = -7L,
            ["f"] = 1.5,
            ["big"] = 12345678901.25,
            ["b"] = true,
            ["n"] = null,
            ["arr"] = new List<object?> { 1L, "x", false, new byte[] { 1, 2, 3 } },
        };
        var e = new Encoder();
        e.WriteAny(value);
        var back = (Dictionary<string, object?>)new Decoder(e.ToArray()).ReadAny()!;
        Assert.Equal("héllo 🙂", back["s"]);
        Assert.Equal(42L, back["i"]);
        Assert.Equal(-7L, back["neg"]);
        Assert.Equal(1.5, back["f"]);
        Assert.Equal(12345678901.25, back["big"]);
        Assert.Equal(true, back["b"]);
        Assert.Null(back["n"]);
        var arr = (List<object?>)back["arr"]!;
        Assert.Equal(1L, arr[0]);
        Assert.Equal(new byte[] { 1, 2, 3 }, (byte[])arr[3]!);
    }

    [Theory]
    [InlineData(1.5, "1.5")]
    [InlineData(100.0, "100")]
    [InlineData(0.1, "0.1")]
    [InlineData(1e21, "1e+21")]
    [InlineData(1e-7, "1e-7")]
    [InlineData(123456789012345680000.0, "123456789012345680000")]
    [InlineData(-0.000001, "-0.000001")]
    public void JsNumberFormatting(double d, string expected) => Assert.Equal(expected, JsonValues.FormatNumber(d));

    [Fact]
    public void Stringify_MatchesJs()
    {
        var v = new Dictionary<string, object?> { ["a"] = new List<object?> { 1L, "x\n\"", null, true }, ["b"] = 2.5 };
        Assert.Equal("{\"a\":[1,\"x\\n\\\"\",null,true],\"b\":2.5}", JsonValues.Stringify(v));
    }
}
