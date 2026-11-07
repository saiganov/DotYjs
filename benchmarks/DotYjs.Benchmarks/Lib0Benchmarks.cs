using BenchmarkDotNet.Attributes;
using DotYjs.Lib0;

namespace DotYjs.Benchmarks;

/// <summary>The lib0 binary encoding primitives that every update is built from.</summary>
[MemoryDiagnoser]
public class Lib0Benchmarks
{
    private byte[] _varUints = [];
    private byte[] _varStrings = [];
    private byte[] _anys = [];
    private readonly Dictionary<string, object?> _anyValue = new(StringComparer.Ordinal)
    {
        ["name"] = "alice",
        ["color"] = "#ff0000",
        ["cursor"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["index"] = 42L, ["length"] = 3L },
        ["active"] = true,
    };

    [Params(10_000)]
    public int Count { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _varUints = WriteVarUints();
        _varStrings = WriteVarStrings();
        _anys = WriteAny();
    }

    [Benchmark(Description = "Write var uints")]
    public byte[] WriteVarUints()
    {
        var encoder = new Encoder();
        for (var i = 0; i < Count; i++) encoder.WriteVarUint((long)i * 31);
        return encoder.ToArray();
    }

    [Benchmark(Description = "Read var uints")]
    public long ReadVarUints()
    {
        var decoder = new Decoder(_varUints);
        long sum = 0;
        for (var i = 0; i < Count; i++) sum += decoder.ReadVarUint();
        return sum;
    }

    [Benchmark(Description = "Write var strings")]
    public byte[] WriteVarStrings()
    {
        var encoder = new Encoder();
        for (var i = 0; i < Count; i++) encoder.WriteVarString("hello, мир");
        return encoder.ToArray();
    }

    [Benchmark(Description = "Read var strings")]
    public int ReadVarStrings()
    {
        var decoder = new Decoder(_varStrings);
        var length = 0;
        for (var i = 0; i < Count; i++) length += decoder.ReadVarString().Length;
        return length;
    }

    [Benchmark(Description = "Write any (awareness-like object)")]
    public byte[] WriteAny()
    {
        var encoder = new Encoder();
        for (var i = 0; i < Count / 10; i++) encoder.WriteAny(_anyValue);
        return encoder.ToArray();
    }

    [Benchmark(Description = "Read any (awareness-like object)")]
    public int ReadAny()
    {
        var decoder = new Decoder(_anys);
        var n = 0;
        for (var i = 0; i < Count / 10; i++) n += decoder.ReadAny() is null ? 0 : 1;
        return n;
    }
}
