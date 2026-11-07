using System.Net;
using System.Net.WebSockets;
using DotYjs.AspNetCore;
using DotYjs.Lib0;
using DotYjs.Protocols;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DotYjs.Tests;

/// <summary>End-to-end tests of the ASP.NET Core WebSocket server with in-process y-websocket style clients.</summary>
public class AspNetCoreTests : IAsyncLifetime
{
    private IHost _host = null!;
    private TestServer _server = null!;
    private readonly MemoryPersistence _persistence = new();

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
                    services.AddSingleton<IYjsPersistence>(_persistence);
                    services.AddYjs(o => o.Authorize = (ctx, doc) => ValueTask.FromResult(doc != "forbidden"));
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

    /// <summary>A minimal y-websocket client: a Doc + Awareness kept in sync over a WebSocket.</summary>
    private sealed class Client : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly TaskCompletionSource _synced = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task _loop = Task.CompletedTask;

        public Doc Doc { get; } = new();
        public Awareness Awareness { get; }
        public WebSocket Socket { get; private set; } = null!;
        public List<byte[]> Received { get; } = new();
        public Task Synced => _synced.Task;

        public Client()
        {
            Awareness = new Awareness(Doc, runOutdatedCheck: false);
            Doc.Update += (update, origin, _, _) =>
            {
                if (origin is not string) _ = SendAsync(YWebSocketProtocol.EncodeUpdate(update));
            };
            Awareness.Update += (change, origin) =>
            {
                if (origin is "local") _ = SendAsync(YWebSocketProtocol.EncodeAwareness(Awareness.EncodeUpdate(change.All.ToList())));
            };
        }

        public static async Task<Client> ConnectAsync(TestServer server, string room)
        {
            var c = new Client();
            var wsClient = server.CreateWebSocketClient();
            // nosemgrep: javascript.lang.security.detect-insecure-websocket -- in-process test server, no TLS
            c.Socket = await wsClient.ConnectAsync(new Uri(server.BaseAddress, $"/collab/{room}").ToString().Replace("http://", "ws://").Let(u => new Uri(u)), CancellationToken.None);
            c._loop = Task.Run(c.ReceiveLoopAsync);
            // like WebsocketProvider: send step1 and our awareness on open
            await c.SendAsync(YWebSocketProtocol.EncodeSyncStep1(c.Doc));
            if (c.Awareness.GetLocalState() != null) await c.SendAsync(YWebSocketProtocol.EncodeAwareness(c.Awareness.EncodeUpdate(new[] { c.Doc.ClientID })));
            return c;
        }

        private async Task ReceiveLoopAsync()
        {
            var buffer = new byte[64 * 1024];
            try
            {
                while (Socket.State == WebSocketState.Open)
                {
                    using var ms = new MemoryStream();
                    WebSocketReceiveResult r;
                    do
                    {
                        r = await Socket.ReceiveAsync(buffer, _cts.Token);
                        if (r.MessageType == WebSocketMessageType.Close) return;
                        ms.Write(buffer, 0, r.Count);
                    } while (!r.EndOfMessage);
                    var msg = ms.ToArray();
                    lock (Received) Received.Add(msg);
                    var decoder = new Decoder(msg);
                    var type = (int)decoder.ReadVarUint();
                    switch (type)
                    {
                        case YWebSocketProtocol.MessageSync:
                        {
                            var encoder = new Encoder();
                            encoder.WriteVarUint(YWebSocketProtocol.MessageSync);
                            int syncType;
                            lock (Doc) syncType = SyncProtocol.ReadSyncMessage(decoder, encoder, Doc, "remote");
                            if (encoder.Length > 1) await SendAsync(encoder.ToArray());
                            if (syncType == SyncProtocol.MessageYjsSyncStep2) _synced.TrySetResult();
                            break;
                        }
                        case YWebSocketProtocol.MessageAwareness:
                            Awareness.ApplyUpdate(decoder.ReadVarUint8Array(), "remote");
                            break;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
        }

        public async Task SendAsync(byte[] data)
        {
            await _sendLock.WaitAsync();
            try
            {
                if (Socket.State == WebSocketState.Open) await Socket.SendAsync(data, WebSocketMessageType.Binary, true, _cts.Token);
            }
            finally { _sendLock.Release(); }
        }

        public void Transact(Action<Doc> f)
        {
            lock (Doc) Doc.Transact(_ => f(Doc));
        }

        public string Text(string name = "t")
        {
            lock (Doc) return Doc.GetText(name).ToString();
        }

        public async Task CloseAsync()
        {
            if (Socket.State == WebSocketState.Open)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, cts.Token); } catch { }
            }
            _cts.Cancel();
            try { await _loop; } catch { }
        }

        public async ValueTask DisposeAsync()
        {
            await CloseAsync();
            Awareness.Destroy();
            Socket.Dispose();
        }
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

    [Fact]
    public async Task Two_Clients_Sync_Through_Server()
    {
        await using var a = await Client.ConnectAsync(_server, "room1");
        await a.Synced.WaitAsync(TimeSpan.FromSeconds(5));
        a.Transact(d => d.GetText("t").Insert(0, "hello"));

        await using var b = await Client.ConnectAsync(_server, "room1");
        await b.Synced.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntil(() => b.Text() == "hello", "b receives initial state");

        b.Transact(d => d.GetText("t").Insert(5, " world"));
        await WaitUntil(() => a.Text() == "hello world", "a receives b's update");

        // the server holds the same state and can edit it
        var serverDoc = Manager.TryGet("room1")!;
        Assert.Equal("hello world", serverDoc.Read(d => d.GetText("t").ToString()));
        Assert.Equal(2, serverDoc.ConnectionCount);
        serverDoc.Transact(d => d.GetText("t").Insert(0, ">> "));
        await WaitUntil(() => a.Text() == ">> hello world" && b.Text() == ">> hello world", "server edit broadcast");
        Assert.Equal(UpdateCodec.EncodeStateVector(a.Doc), UpdateCodec.EncodeStateVector(b.Doc));
    }

    [Fact]
    public async Task Awareness_Is_Relayed_And_Removed_On_Disconnect()
    {
        await using var a = await Client.ConnectAsync(_server, "aw");
        await using var b = await Client.ConnectAsync(_server, "aw");
        await Task.WhenAll(a.Synced, b.Synced).WaitAsync(TimeSpan.FromSeconds(5));
        a.Awareness.SetLocalState(new Dictionary<string, object?> { ["user"] = "alice" });
        await WaitUntil(() => b.Awareness.GetStates().TryGetValue(a.Doc.ClientID, out var s) && s is Dictionary<string, object?> d && (string?)d["user"] == "alice", "b sees alice");
        var server = Manager.TryGet("aw")!;
        Assert.True(server.Awareness.GetStates().ContainsKey(a.Doc.ClientID));
        Assert.Contains(a.Doc.ClientID, server.Connections.Single(c => c.ControlledIds.Contains(a.Doc.ClientID)).ControlledIds);

        // a late joiner gets the existing awareness states right away
        await using var c = await Client.ConnectAsync(_server, "aw");
        await WaitUntil(() => c.Awareness.GetStates().ContainsKey(a.Doc.ClientID), "late joiner receives awareness");

        await a.CloseAsync();
        await WaitUntil(() => !b.Awareness.GetStates().ContainsKey(a.Doc.ClientID), "alice removed after disconnect");
        Assert.False(server.Awareness.GetStates().ContainsKey(a.Doc.ClientID));
    }

    [Fact]
    public async Task Document_Is_Persisted_And_Unloaded_When_Last_Client_Leaves()
    {
        _persistence.Stored["persist"] = Update(d => d.GetText("t").Insert(0, "from db"));
        await using (var a = await Client.ConnectAsync(_server, "persist"))
        {
            await a.Synced.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitUntil(() => a.Text() == "from db", "state loaded from persistence");
            a.Transact(d => d.GetText("t").Insert(7, "!"));
            await WaitUntil(() => Manager.TryGet("persist")?.Read(d => d.GetText("t").ToString()) == "from db!", "server applied update");
        }
        await WaitUntil(() => Manager.TryGet("persist") == null, "document unloaded");
        Assert.Equal(1, _persistence.Writes);
        var reloaded = new Doc();
        UpdateCodec.ApplyUpdate(reloaded, _persistence.Stored["persist"]);
        Assert.Equal("from db!", reloaded.GetText("t").ToString());
    }

    [Fact]
    public async Task Authorize_Rejects_Connection()
    {
        var wsClient = _server.CreateWebSocketClient();
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => wsClient.ConnectAsync(new Uri("ws://localhost/collab/forbidden"), CancellationToken.None));
        Assert.Contains("403", ex.Message);
        var http = _server.CreateClient();
        var resp = await http.GetAsync("/collab/plain-http");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    private static byte[] Update(Action<Doc> f)
    {
        var d = new Doc();
        f(d);
        return UpdateCodec.EncodeStateAsUpdate(d);
    }

    [Fact]
    public async Task Client_Connecting_During_Persist_Waits_And_Gets_Fresh_Instance()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _persistence.BeforeWrite = name => name == "slow" ? gate.Task : Task.CompletedTask;
        YjsDocument? first;
        await using (var a = await Client.ConnectAsync(_server, "slow"))
        {
            await a.Synced.WaitAsync(TimeSpan.FromSeconds(5));
            a.Transact(d => d.GetText("t").Insert(0, "persist me"));
            await WaitUntil(() => Manager.TryGet("slow")?.Read(d => d.GetText("t").ToString()) == "persist me", "server applied update");
            first = Manager.TryGet("slow");
        }
        await WaitUntil(() => first!.IsUnloading, "unload started");
        Assert.False(first!.IsUnloaded); // write is blocked by the gate

        // a new client must not attach to the instance being persisted...
        await using var b = await Client.ConnectAsync(_server, "slow");
        await Task.Delay(200);
        Assert.False(b.Synced.IsCompleted);
        Assert.Equal(0, first.ConnectionCount);
        Assert.Null(Manager.TryGet("slow"));

        // ...but gets the persisted state from a fresh instance once the write finished
        gate.SetResult();
        await b.Synced.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntil(() => b.Text() == "persist me", "state reloaded from persistence");
        var second = Manager.TryGet("slow");
        Assert.NotNull(second);
        Assert.NotSame(first, second);
        Assert.True(first.IsUnloaded);
        Assert.Equal(1, second!.ConnectionCount);
    }

    [Fact]
    public async Task Failed_Persist_Keeps_Document_In_Memory_For_Retry()
    {
        _persistence.FailWrites.Add("flaky");
        await using (var a = await Client.ConnectAsync(_server, "flaky"))
        {
            await a.Synced.WaitAsync(TimeSpan.FromSeconds(5));
            a.Transact(d => d.GetText("t").Insert(0, "do not lose me"));
            await WaitUntil(() => Manager.TryGet("flaky")?.Read(d => d.GetText("t").ToString()) == "do not lose me", "server applied update");
        }
        await WaitUntil(() => _persistence.FailedWrites > 0, "write attempted and failed");
        await WaitUntil(() => Manager.TryGet("flaky") is { IsUnloading: false, ConnectionCount: 0 }, "document retained");
        Assert.False(_persistence.Stored.ContainsKey("flaky"));
        Assert.Equal("do not lose me", Manager.TryGet("flaky")!.Read(d => d.GetText("t").ToString()));

        // the retained document is served to the next client and persisted once the store works again
        _persistence.FailWrites.Remove("flaky");
        await using (var b = await Client.ConnectAsync(_server, "flaky"))
        {
            await b.Synced.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitUntil(() => b.Text() == "do not lose me", "retained state served");
        }
        await WaitUntil(() => Manager.TryGet("flaky") == null, "document unloaded after successful write");
        var reloaded = new Doc();
        UpdateCodec.ApplyUpdate(reloaded, _persistence.Stored["flaky"]);
        Assert.Equal("do not lose me", reloaded.GetText("t").ToString());
    }

    private sealed class MemoryPersistence : IYjsPersistence
    {
        public Dictionary<string, byte[]> Stored { get; } = new();
        public HashSet<string> FailWrites { get; } = new();
        public Func<string, Task>? BeforeWrite { get; set; }
        public int Writes;
        public int FailedWrites;

        public Task BindStateAsync(string docName, Doc doc, CancellationToken ct)
        {
            lock (Stored)
            {
                if (Stored.TryGetValue(docName, out var state)) UpdateCodec.ApplyUpdate(doc, state);
            }
            return Task.CompletedTask;
        }

        public async Task WriteStateAsync(string docName, Doc doc, CancellationToken ct)
        {
            if (BeforeWrite != null) await BeforeWrite(docName);
            lock (Stored)
            {
                if (FailWrites.Contains(docName))
                {
                    FailedWrites++;
                    throw new IOException("database unavailable");
                }
                Stored[docName] = UpdateCodec.EncodeStateAsUpdate(doc);
                Writes++;
            }
        }
    }
}

internal static class FunctionalExtensions
{
    public static TResult Let<T, TResult>(this T value, Func<T, TResult> f) => f(value);
}
