using BenchmarkDotNet.Attributes;

namespace DotYjs.Benchmarks;

/// <summary>Local edits of a <see cref="YText"/>, one transaction per edit (typing in an editor).</summary>
[MemoryDiagnoser]
public class TextEditingBenchmarks
{
    private int[] _positions = [];

    [Params(1_000, 10_000)]
    public int Operations { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var random = new Random(42);
        _positions = new int[Operations];
        for (var i = 0; i < Operations; i++) _positions[i] = random.Next(i + 1);
    }

    [Benchmark(Description = "Append characters")]
    public int AppendCharacters()
    {
        var text = new Doc().GetText();
        for (var i = 0; i < Operations; i++) text.Insert(i, "a");
        return text.Length;
    }

    [Benchmark(Description = "Insert characters at random positions")]
    public int InsertAtRandomPositions()
    {
        var text = new Doc().GetText();
        foreach (var position in _positions) text.Insert(position, "a");
        return text.Length;
    }

    [Benchmark(Description = "Prepend characters")]
    public int PrependCharacters()
    {
        var text = new Doc().GetText();
        for (var i = 0; i < Operations; i++) text.Insert(0, "a");
        return text.Length;
    }

    [Benchmark(Description = "Type then delete every character")]
    public int TypeThenDelete()
    {
        var text = new Doc().GetText();
        for (var i = 0; i < Operations; i++) text.Insert(i, "a");
        for (var i = Operations; i > 0; i--) text.Delete(i - 1, 1);
        return text.Length;
    }

    [Benchmark(Description = "Insert formatted text")]
    public int InsertFormatted()
    {
        var bold = new Dictionary<string, object?>(StringComparer.Ordinal) { ["bold"] = true };
        var text = new Doc().GetText();
        for (var i = 0; i < Operations; i++) text.Insert(i, "a", i % 2 == 0 ? bold : null);
        return text.Length;
    }
}
