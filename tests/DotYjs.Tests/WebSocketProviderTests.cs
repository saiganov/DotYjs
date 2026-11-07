using DotYjs.AspNetCore;
using DotYjs.Protocols;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DotYjs.Tests;

/// <summary>The .NET y-websocket client (<see cref="YjsWebSocketProvider"/>) against the in-process ASP.NET Core server.</summary>
public class WebSocketProviderTests : IAsyncLifetime
{
    private IHost _host = null!;
    private TestServer _server = null!;

    public async Task InitializeAsync()
    {
        _host = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
                    services.AddRouting();
                    services.AddYjs(o =>
                    {
                        o.UnloadWhenIdle = false;
                        o.Authorize = (ctx, doc) => ValueTask.FromResult(doc != "forbidden");
                    });
                });
                web.Configure(app =>
                {
                    app.UseWebSockets();
                    app.UseRouting();
                    app.UseEndpoints(e => e.MapYjs("/collab/{docName}"));
                });
            })
            .StartAsync();
        _server = _host.GetTestServer();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private YjsDocumentManager Manager => _host.Services.GetRequiredService<YjsDocumentManager>();

    private YjsWebSocketProvider Provider(string room, Doc doc, Action<YjsWebSocketProviderOptions>? configure = null)
    {
        var options = new YjsWebSocketProviderOptions
        {
            MaxBackoffTime = TimeSpan.FromMilliseconds(200),
            WebSocketFactory = async (uri, ct) => await _server.CreateWebSocketClient().ConnectAsync(uri, ct),
        };
        configure?.Invoke(options);
        return new YjsWebSocketProvider("ws://localhost/collab", room, doc, options);
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

    private static Task Synced(YjsWebSocketProvider p) => p.WhenSynced.WaitAsync(TimeSpan.FromSeconds(5));

    [Fact]
    public async Task Two_Providers_Sync_Document_And_Awareness()
    {
        var doc1 = new Doc();
        await using var p1 = Provider("two", doc1);
        await Synced(p1);
        Assert.Equal(YjsConnectionStatus.Connected, p1.Status);
        Assert.Equal(new Uri("ws://localhost/collab/two"), p1.Url);
        lock (doc1) doc1.GetText("t").Insert(0, "hello");

        var doc2 = new Doc();
        await using var p2 = Provider("two", doc2);
        await Synced(p2);
        await WaitUntil(() => doc2.GetText("t").ToString() == "hello", "initial state");

        lock (doc2) doc2.GetText("t").Insert(5, " world");
        await WaitUntil(() => doc1.GetText("t").ToString() == "hello world", "update relayed");
        Assert.Equal("hello world", Manager.TryGet("two")!.Read(d => d.GetText("t").ToString()));

        p1.Awareness.SetLocalStateField("user", "alice");
        await WaitUntil(() => p2.Awareness.GetStates().ContainsKey(doc1.ClientID), "awareness relayed");
        Assert.Equal("alice", ((Dictionary<string, object?>)p2.Awareness.GetStates()[doc1.ClientID]!)["user"]);

        // disposing announces a null state, the server relays the removal
        await p1.DisposeAsync();
        await WaitUntil(() => !p2.Awareness.GetStates().ContainsKey(doc1.ClientID), "awareness removed");
    }

    [Fact]
    public async Task Query_Params_And_Escaping_Are_Applied_To_Url()
    {
        var doc = new Doc();
        await using var p = Provider("my room/x", doc, o => { o.Connect = false; o.Params["token"] = "a b"; });
        Assert.Equal("ws://localhost/collab/my%20room%2Fx?token=a%20b", p.Url.AbsoluteUri);
        Assert.Equal(YjsConnectionStatus.Disconnected, p.Status);
    }

    [Fact]
    public async Task Reconnects_After_Server_Closes_Connection_And_Catches_Up()
    {
        var doc = new Doc();
        var statuses = new List<YjsConnectionStatus>();
        await using var p = Provider("reconnect", doc);
        p.StatusChanged += s => { lock (statuses) statuses.Add(s); };
        await Synced(p);
        var serverDoc = Manager.TryGet("reconnect")!;
        await WaitUntil(() => serverDoc.Connections.Count == 1, "server sees the connection");

        // the server drops the socket
        var conn = serverDoc.Connections.Single();
        conn.Socket.Abort();
        await WaitUntil(() => { lock (statuses) return statuses.Contains(YjsConnectionStatus.Disconnected); }, "client notices the close");
        Assert.False(p.Synced);

        // meanwhile the document changes on the server and locally
        serverDoc.Transact(d => d.GetText("t").Insert(0, "server"));
        lock (doc) doc.GetText("t").Insert(0, "client");

        await WaitUntil(() => p.Synced, "client reconnects and resyncs");
        await WaitUntil(() => doc.GetText("t").ToString().Length == "serverclient".Length, "both edits merged on client");
        await WaitUntil(() => serverDoc.Read(d => d.GetText("t").ToString()) == doc.GetText("t").ToString(), "server converges");
        Assert.Single(serverDoc.Connections);
    }

    [Fact]
    public async Task Connect_Failure_Is_Reported_And_Retried()
    {
        var doc = new Doc();
        var errors = 0;
        await using var p = Provider("forbidden", doc);
        p.ConnectionError += _ => Interlocked.Increment(ref errors);
        await WaitUntil(() => Volatile.Read(ref errors) >= 2, "repeated connection attempts fail", 10000);
        Assert.False(p.Synced);
        Assert.NotEqual(YjsConnectionStatus.Connected, p.Status);
        await p.DisconnectAsync();
        Assert.Equal(YjsConnectionStatus.Disconnected, p.Status);
    }

    [Fact]
    public async Task Disconnect_And_Connect_Again_Resyncs()
    {
        var doc = new Doc();
        await using var p = Provider("again", doc);
        await Synced(p);
        await p.DisconnectAsync();
        Assert.Equal(YjsConnectionStatus.Disconnected, p.Status);
        Assert.False(p.Synced);

        Manager.TryGet("again")!.Transact(d => d.GetMap("m").Set("k", 1L));
        p.Connect();
        await WaitUntil(() => p.Synced && doc.GetMap("m").Has("k"), "state after reconnect");
    }

    [Fact]
    public async Task Offline_Edits_Are_Sent_On_First_Connect()
    {
        var doc = new Doc();
        doc.GetText("t").Insert(0, "offline");
        await using var p = Provider("offline", doc, o => o.Connect = false);
        Assert.Null(Manager.TryGet("offline"));
        p.Connect();
        await Synced(p);
        await WaitUntil(() => Manager.TryGet("offline")?.Read(d => d.GetText("t").ToString()) == "offline", "offline edits reach the server");
    }

    [Fact]
    public async Task Shared_Awareness_Instance_Is_Not_Destroyed_With_Provider()
    {
        var doc = new Doc();
        var awareness = new Awareness(doc, runOutdatedCheck: false);
        var p = Provider("shared", doc, o => o.Awareness = awareness);
        await Synced(p);
        Assert.Same(awareness, p.Awareness);
        var destroyed = false;
        awareness.Destroyed += _ => destroyed = true;
        await p.DisposeAsync();
        Assert.False(destroyed);
        Assert.Null(awareness.GetLocalState()); // the provider announced that we left, like y-websocket does on unload
        awareness.SetLocalState(new Dictionary<string, object?> { ["still"] = "alive" });
        Assert.Equal("alive", ((Dictionary<string, object?>)awareness.GetLocalState()!)["still"]);
        awareness.Destroy();
        Assert.True(destroyed);
    }
}
