using DotYjs.AspNetCore;
using DotYjs.AspNetCore.Redis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DotYjs.Tests;

/// <summary>Several server nodes hosting the same document through an <see cref="IYjsBackplane"/>.</summary>
public class BackplaneTests
{
    private static YjsDocumentManager Node(IYjsBackplane backplane, IYjsPersistence? persistence = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton(backplane);
        if (persistence != null) services.AddSingleton(persistence);
        services.AddYjs(o => o.UnloadWhenIdle = false);
        return services.BuildServiceProvider().GetRequiredService<YjsDocumentManager>();
    }

    private static async Task WaitUntil(Func<bool> cond, string what, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!cond())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Timed out waiting for: " + what);
            await Task.Delay(10);
        }
    }

    private static string Text(YjsDocument d) => d.Read(x => x.GetText("t").ToString());

    private static async Task RunScenario(IYjsBackplane backplane)
    {
        var room = "room-" + Guid.NewGuid().ToString("N");
        var node1 = Node(backplane);
        var node2 = Node(backplane);
        Assert.NotEqual(node1.NodeId, node2.NodeId);
        var d1 = await node1.GetOrCreateAsync(room);
        var d2 = await node2.GetOrCreateAsync(room);

        // document updates flow both ways
        d1.Transact(d => d.GetText("t").Insert(0, "from node 1"));
        await WaitUntil(() => Text(d2) == "from node 1", "node 2 receives update");
        d2.Transact(d => d.GetText("t").Insert(0, ">> "));
        await WaitUntil(() => Text(d1) == ">> from node 1", "node 1 receives update");
        Assert.Equal(UpdateCodec.EncodeStateVector(d1.Doc), UpdateCodec.EncodeStateVector(d2.Doc));

        // awareness states of clients on node 1 become visible on node 2 (and are removed again)
        var clientDoc = new Doc { ClientID = 777 };
        using var clientAwareness = new DotYjs.Protocols.Awareness(clientDoc, false);
        clientAwareness.SetLocalState(new Dictionary<string, object?> { ["user"] = "alice" });
        d1.Awareness.ApplyUpdate(clientAwareness.EncodeUpdate(new[] { 777L }), "conn");
        await WaitUntil(() => d2.Awareness.GetStates().ContainsKey(777), "awareness relayed to node 2");
        Assert.Equal("alice", ((Dictionary<string, object?>)d2.Awareness.GetStates()[777]!)["user"]);
        d1.Awareness.RemoveStates(new[] { 777L }, null);
        await WaitUntil(() => !d2.Awareness.GetStates().ContainsKey(777), "awareness removal relayed");

        // a node that loads the document later catches up with the in-memory state of the others
        clientAwareness.SetLocalState(new Dictionary<string, object?> { ["user"] = "alice", ["back"] = true }); // new clock, otherwise the re-announce is stale
        d1.Awareness.ApplyUpdate(clientAwareness.EncodeUpdate(new[] { 777L }), "conn");
        var node3 = Node(backplane);
        var d3 = await node3.GetOrCreateAsync(room);
        await WaitUntil(() => Text(d3) == ">> from node 1", "late node catches up");
        await WaitUntil(() => d3.Awareness.GetStates().ContainsKey(777), "late node receives awareness");
        d3.Transact(d => d.GetText("t").Insert(0, "3:"));
        await WaitUntil(() => Text(d1) == "3:>> from node 1" && Text(d2) == "3:>> from node 1", "all nodes converge");
    }

    [Fact]
    public async Task InMemory_Backplane_Syncs_Nodes() => await RunScenario(new InMemoryYjsBackplane());

    [Fact]
    public async Task Concurrent_Edits_On_Two_Nodes_Do_Not_Deadlock_And_Converge()
    {
        var backplane = new InMemoryYjsBackplane();
        var node1 = Node(backplane);
        var node2 = Node(backplane);
        var d1 = await node1.GetOrCreateAsync("busy");
        var d2 = await node2.GetOrCreateAsync("busy");
        const int n = 300;
        var t1 = Task.Run(() => { for (int i = 0; i < n; i++) d1.Transact(d => d.GetArray("a").Push(1L)); });
        var t2 = Task.Run(() => { for (int i = 0; i < n; i++) d2.Transact(d => d.GetArray("a").Push(2L)); });
        await Task.WhenAll(t1, t2).WaitAsync(TimeSpan.FromSeconds(20));
        await WaitUntil(() => d1.Read(d => d.GetArray("a").Length) == 2 * n && d2.Read(d => d.GetArray("a").Length) == 2 * n, "both nodes see all edits", 10000);
        Assert.Equal(UpdateCodec.EncodeStateVector(d1.Doc), UpdateCodec.EncodeStateVector(d2.Doc));
    }

    [Fact]
    public async Task Joining_Node_With_Preloaded_State_Syncs_Both_Ways()
    {
        var backplane = new InMemoryYjsBackplane();
        var node1 = Node(backplane);
        var d1 = await node1.GetOrCreateAsync("pre");
        d1.Transact(d => d.GetText("t").Insert(0, "live"));

        // node 2 loads the document from a persistence that holds edits node 1 never saw
        var other = new Doc();
        other.GetText("t").Insert(0, "persisted ");
        var persistence = new PreloadPersistence(UpdateCodec.EncodeStateAsUpdate(other));
        var node2 = Node(backplane, persistence);
        var d2 = await node2.GetOrCreateAsync("pre");

        await WaitUntil(() => Text(d1).Contains("persisted") && Text(d1).Contains("live"), "node 1 receives preloaded state");
        await WaitUntil(() => Text(d2).Contains("persisted") && Text(d2).Contains("live"), "node 2 receives live state");
        await WaitUntil(() => UpdateCodec.EncodeStateVector(d1.Doc).SequenceEqual(UpdateCodec.EncodeStateVector(d2.Doc)), "state vectors equal");
        Assert.Equal(Text(d1), Text(d2));
    }

    private sealed class PreloadPersistence : IYjsPersistence
    {
        private readonly byte[] _state;
        public PreloadPersistence(byte[] state) { _state = state; }
        public Task BindStateAsync(string docName, Doc doc, CancellationToken ct) { UpdateCodec.ApplyUpdate(doc, _state); return Task.CompletedTask; }
        public Task WriteStateAsync(string docName, Doc doc, CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public async Task Backplane_Message_Round_Trips()
    {
        var m = new YjsBackplaneMessage(YjsBackplaneMessageKind.AwarenessUpdate, new byte[] { 1, 2, 3 }, "node-a");
        var back = YjsBackplaneMessage.Decode(m.Encode());
        Assert.Equal(m.Kind, back.Kind);
        Assert.Equal(m.Payload, back.Payload);
        Assert.Equal(m.NodeId, back.NodeId);
    }

    /// <summary>Runs against a real Redis when the DOTYJS_REDIS environment variable holds a connection string (CI starts one).</summary>
    [Fact]
    public async Task Redis_Backplane_Syncs_Nodes()
    {
        var config = Environment.GetEnvironmentVariable("DOTYJS_REDIS");
        if (string.IsNullOrEmpty(config)) return; // no Redis available: nothing to test here
        await using var backplane = new RedisYjsBackplane(new RedisYjsBackplaneOptions { Configuration = config, ChannelPrefix = "dotyjs-test:" });
        await RunScenario(backplane);
    }
}
