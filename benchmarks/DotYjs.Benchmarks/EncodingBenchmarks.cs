using BenchmarkDotNet.Attributes;

namespace DotYjs.Benchmarks;

/// <summary>
/// Encoding and applying updates of a document edited concurrently by two clients
/// (what a server does when it loads, persists or sends a document).
/// </summary>
[MemoryDiagnoser]
public class EncodingBenchmarks
{
    private Doc _doc = null!;
    private byte[] _updateV1 = [];
    private byte[] _updateV2 = [];
    private byte[] _halfStateVector = [];
    private List<byte[]> _incrementalUpdates = [];

    [Params(1_000, 10_000)]
    public int Edits { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _incrementalUpdates = new List<byte[]>(Edits);
        _doc = DocumentFactory.CreateEditedDocument(Edits, 42, _incrementalUpdates);
        _updateV1 = UpdateCodec.EncodeStateAsUpdate(_doc);
        _updateV2 = UpdateCodec.EncodeStateAsUpdateV2(_doc);

        var half = new Doc();
        UpdateCodec.ApplyUpdate(half, Updates.MergeUpdates(_incrementalUpdates.GetRange(0, _incrementalUpdates.Count / 2)));
        _halfStateVector = UpdateCodec.EncodeStateVector(half);
    }

    [Benchmark(Description = "Encode state as update (v1)")]
    public byte[] EncodeStateAsUpdateV1() => UpdateCodec.EncodeStateAsUpdate(_doc);

    [Benchmark(Description = "Encode state as update (v2)")]
    public byte[] EncodeStateAsUpdateV2() => UpdateCodec.EncodeStateAsUpdateV2(_doc);

    [Benchmark(Description = "Encode diff against a state vector (v1)")]
    public byte[] EncodeDiff() => UpdateCodec.EncodeStateAsUpdate(_doc, _halfStateVector);

    [Benchmark(Description = "Encode state vector")]
    public byte[] EncodeStateVector() => UpdateCodec.EncodeStateVector(_doc);

    [Benchmark(Description = "Load document from update (v1)")]
    public int ApplyUpdateV1()
    {
        var doc = new Doc();
        UpdateCodec.ApplyUpdate(doc, _updateV1);
        return doc.GetText(DocumentFactory.TextName).Length;
    }

    [Benchmark(Description = "Load document from update (v2)")]
    public int ApplyUpdateV2()
    {
        var doc = new Doc();
        UpdateCodec.ApplyUpdateV2(doc, _updateV2);
        return doc.GetText(DocumentFactory.TextName).Length;
    }

    [Benchmark(Description = "Apply every incremental update")]
    public int ApplyIncrementalUpdates()
    {
        var doc = new Doc();
        foreach (var update in _incrementalUpdates) UpdateCodec.ApplyUpdate(doc, update);
        return doc.GetText(DocumentFactory.TextName).Length;
    }

    [Benchmark(Description = "Merge incremental updates without a document")]
    public byte[] MergeUpdates() => Updates.MergeUpdates(_incrementalUpdates);

    [Benchmark(Description = "Diff update against a state vector without a document")]
    public byte[] DiffUpdate() => Updates.DiffUpdate(_updateV1, _halfStateVector);

    [Benchmark(Description = "Convert update v1 to v2")]
    public byte[] ConvertV1ToV2() => Updates.ConvertUpdateFormatV1ToV2(_updateV1);
}
