using BenchmarkDotNet.Attributes;

namespace DotYjs.Benchmarks;

/// <summary>Local edits of <see cref="YArray"/> and <see cref="YMap"/>, one transaction per edit.</summary>
[MemoryDiagnoser]
public class CollectionBenchmarks
{
    [Params(1_000, 10_000)]
    public int Operations { get; set; }

    [Benchmark(Description = "YArray: push items")]
    public int ArrayPush()
    {
        var array = new Doc().GetArray();
        for (var i = 0; i < Operations; i++) array.Push(i);
        return array.Length;
    }

    [Benchmark(Description = "YArray: insert items at the start")]
    public int ArrayInsertAtStart()
    {
        var array = new Doc().GetArray();
        for (var i = 0; i < Operations; i++) array.Insert(0, i);
        return array.Length;
    }

    [Benchmark(Description = "YArray: push items in one transaction")]
    public int ArrayPushInOneTransaction()
    {
        var doc = new Doc();
        var array = doc.GetArray();
        doc.Transact(_ =>
        {
            for (var i = 0; i < Operations; i++) array.Push(i);
        });
        return array.Length;
    }

    [Benchmark(Description = "YMap: set distinct keys")]
    public int MapSetDistinctKeys()
    {
        var map = new Doc().GetMap();
        for (var i = 0; i < Operations; i++) map.Set("key" + i, i);
        return map.Size;
    }

    [Benchmark(Description = "YMap: overwrite one key")]
    public int MapOverwriteKey()
    {
        var map = new Doc().GetMap();
        for (var i = 0; i < Operations; i++) map.Set("key", i);
        return map.Size;
    }
}
