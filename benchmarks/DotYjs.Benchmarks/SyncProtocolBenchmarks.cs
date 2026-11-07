using BenchmarkDotNet.Attributes;
using DotYjs.Lib0;
using DotYjs.Protocols;

namespace DotYjs.Benchmarks;

/// <summary>The y-protocols sync handshake between a server document and a new, empty client.</summary>
[MemoryDiagnoser]
public class SyncProtocolBenchmarks
{
    private Doc _server = null!;

    [Params(1_000, 10_000)]
    public int Edits { get; set; }

    [GlobalSetup]
    public void Setup() => _server = DocumentFactory.CreateEditedDocument(Edits);

    [Benchmark(Description = "Initial sync of an empty client")]
    public int InitialSync()
    {
        var client = new Doc();

        // client -> server: sync step 1 (state vector); server answers with sync step 2 (missing updates)
        var reply = new Encoder();
        SyncProtocol.ReadSyncMessage(new Decoder(SyncProtocol.EncodeSyncStep1(client)), reply, _server);

        // server -> client: apply sync step 2
        SyncProtocol.ReadSyncMessage(new Decoder(reply.ToArray()), new Encoder(), client);
        return client.GetText(DocumentFactory.TextName).Length;
    }
}
