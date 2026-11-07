using System.Net.WebSockets;
using DotYjs.Lib0;

namespace DotYjs.Protocols;

/// <summary>The connection state of a <see cref="YjsWebSocketProvider"/>.</summary>
public enum YjsConnectionStatus
{
    /// <summary>Not connected; the provider may be waiting to reconnect.</summary>
    Disconnected,
    /// <summary>A connection attempt is in progress.</summary>
    Connecting,
    /// <summary>Connected to the server.</summary>
    Connected
}

/// <summary>Options for <see cref="YjsWebSocketProvider"/>.</summary>
public sealed class YjsWebSocketProviderOptions
{
    /// <summary>Query string parameters appended to the URL (e.g. an auth token), like <c>params</c> in y-websocket.</summary>
    public IDictionary<string, string> Params { get; } = new Dictionary<string, string>();

    /// <summary>Connect immediately in the constructor (default true).</summary>
    public bool Connect { get; set; } = true;

    /// <summary>Periodically re-send sync step 1 to guard against lost messages. Zero disables (default).</summary>
    public TimeSpan ResyncInterval { get; set; } = TimeSpan.Zero;

    /// <summary>Upper bound of the exponential reconnect back-off (y-websocket: 2500 ms).</summary>
    public TimeSpan MaxBackoffTime { get; set; } = TimeSpan.FromMilliseconds(2500);

    /// <summary>Maximum size of an incoming message (default 100 MB).</summary>
    public int MaxMessageSize { get; set; } = 100 * 1024 * 1024;

    /// <summary>An existing <see cref="Awareness"/> to use; a new one is created when null.</summary>
    public Awareness? Awareness { get; set; }

    /// <summary>
    /// Creates the WebSocket for a URL. Defaults to <see cref="ClientWebSocket"/>; override to use a test server,
    /// custom headers or a proxy.
    /// </summary>
    public Func<Uri, CancellationToken, Task<WebSocket>>? WebSocketFactory { get; set; }

    /// <summary>Configures the default <see cref="ClientWebSocket"/> (headers, sub-protocols, keep-alive) before connecting.</summary>
    public Action<ClientWebSocketOptions>? ConfigureClientWebSocket { get; set; }
}

/// <summary>
/// .NET port of the y-websocket <c>WebsocketProvider</c>: keeps a <see cref="Doc"/> and its <see cref="Awareness"/>
/// in sync with any y-websocket compatible server (the JS <c>y-websocket</c> server, DotYjs.AspNetCore, Hocuspocus, …).
/// Reconnects automatically with exponential back-off.
/// </summary>
#pragma warning disable IDISP003, S2930 // the socket and the connection CTS are replaced per connection attempt; the previous ones are disposed by the connection loop
public sealed class YjsWebSocketProvider : IAsyncDisposable
{
    private readonly YjsWebSocketProviderOptions _options;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly object _stateLock = new();
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly Action<byte[], object?, Doc, Transaction> _onDocUpdate;
    private readonly Action<AwarenessChange, object?> _onAwarenessUpdate;
    private readonly Action<Doc> _onDocDestroyed;
    private TaskCompletionSource<bool> _whenSynced = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private WebSocket? _ws;
    private CancellationToken _connectionToken;
    private Task? _connectionLoop;
    private CancellationTokenSource? _connectionCts;
    private int _unsuccessfulReconnects;
    private bool _shouldConnect;
    private bool _synced;
    private readonly Timer? _resyncTimer;

    /// <summary>Creates a provider; it starts connecting immediately unless <see cref="YjsWebSocketProviderOptions.Connect"/> is false.</summary>
    /// <param name="serverUrl">Server URL without the room, e.g. <c>ws://localhost:1234</c> or <c>wss://host/collab</c>.</param>
    /// <param name="roomName">Document/room name appended to the URL.</param>
    /// <param name="doc">The document to sync.</param>
    /// <param name="options">Options.</param>
    public YjsWebSocketProvider(string serverUrl, string roomName, Doc doc, YjsWebSocketProviderOptions? options = null)
    {
        _options = options ?? new YjsWebSocketProviderOptions();
        Doc = doc;
        RoomName = roomName;
        ServerUrl = serverUrl.TrimEnd('/');
        Awareness = _options.Awareness ?? new Awareness(doc);
        var url = ServerUrl + "/" + Uri.EscapeDataString(roomName);
        if (_options.Params.Count > 0)
        {
            url += "?" + string.Join("&", _options.Params.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value)));
        }
        Url = new Uri(url);

        _onDocUpdate = (update, origin, _, _) =>
        {
            if (ReferenceEquals(origin, this)) return;
            _ = SendAsync(YWebSocketProtocol.EncodeUpdate(update), _connectionToken);
        };
        _onAwarenessUpdate = (change, _) =>
        {
            var changed = change.All.ToList();
            _ = SendAsync(YWebSocketProtocol.EncodeAwareness(Awareness.EncodeUpdate(changed)), _connectionToken);
        };
        _onDocDestroyed = _doc => { _ = DisposeAsync().AsTask(); };
        Doc.Update += _onDocUpdate;
        Awareness.Update += _onAwarenessUpdate;
        Doc.Destroyed += _onDocDestroyed;

        if (_options.ResyncInterval > TimeSpan.Zero)
        {
            _resyncTimer = new Timer(_ =>
            {
                if (Status != YjsConnectionStatus.Connected) return;
                byte[] step1;
                lock (Doc) step1 = YWebSocketProtocol.EncodeSyncStep1(Doc);
                _ = SendAsync(step1, _connectionToken);
            }, null, _options.ResyncInterval, _options.ResyncInterval);
        }
        if (_options.Connect) Connect();
    }

    /// <summary>The document synced with the server.</summary>
    public Doc Doc { get; }
    /// <summary>The awareness (cursors, user names) shared with the other clients.</summary>
    public Awareness Awareness { get; }
    /// <summary>The server URL passed to the constructor.</summary>
    public string ServerUrl { get; }
    /// <summary>The room (document) name.</summary>
    public string RoomName { get; }
    /// <summary>The full WebSocket URL: server URL, room name and query parameters.</summary>
    public Uri Url { get; }

    /// <summary>The current connection state.</summary>
    public YjsConnectionStatus Status { get; private set; } = YjsConnectionStatus.Disconnected;

    /// <summary>True after the server's sync step 2 was received for the current connection.</summary>
    public bool Synced
    {
        get => _synced;
        private set
        {
            if (_synced == value) return;
            _synced = value;
            if (value) _whenSynced.TrySetResult(true);
            else _whenSynced = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            SyncedChanged?.Invoke(value);
        }
    }

    /// <summary>Completes when the document is synced with the server (renewed after a reconnect).</summary>
    public Task WhenSynced => _whenSynced.Task;

    /// <summary>Raised when <see cref="Status"/> changes.</summary>
    public event Action<YjsConnectionStatus>? StatusChanged;
    /// <summary>Raised when the document becomes synced with the server (true) or loses sync (false).</summary>
    public event Action<bool>? SyncedChanged;

    /// <summary>Raised when the server denied access (y-protocols auth message) with the reason it sent.</summary>
    public event Action<string>? PermissionDenied;

    /// <summary>Raised when a connection attempt or an open connection failed.</summary>
    public event Action<Exception>? ConnectionError;

    private void SetStatus(YjsConnectionStatus status)
    {
        if (Status == status) return;
        Status = status;
        StatusChanged?.Invoke(status);
    }

    /// <summary>Starts connecting (and reconnecting) to the server.</summary>
    public void Connect()
    {
        lock (_stateLock)
        {
            _shouldConnect = true;
            if (_connectionLoop != null || _disposeCts.IsCancellationRequested) return;
            _connectionCts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
            var token = _connectionCts.Token;
            _connectionLoop = Task.Run(() => ConnectionLoopAsync(token), CancellationToken.None);
        }
    }

    /// <summary>Closes the connection and stops reconnecting.</summary>
    public async Task DisconnectAsync()
    {
        Task? loop;
        lock (_stateLock)
        {
            _shouldConnect = false;
            loop = _connectionLoop;
#pragma warning disable VSTHRD103 // synchronous Cancel is intended: nothing to await here
            _connectionCts?.Cancel();
#pragma warning restore VSTHRD103
        }
        // a send or receive stuck on an unresponsive peer does not observe the token: abort the socket as well
        try { _ws?.Abort(); } catch { /* already closed */ }
        if (loop != null)
        {
            try { await loop.ConfigureAwait(false); } catch { /* cancelled */ }
        }
        lock (_stateLock)
        {
            _connectionLoop = null;
            _connectionCts = null;
        }
    }

    private async Task ConnectionLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _shouldConnect)
        {
            SetStatus(YjsConnectionStatus.Connecting);
            WebSocket? ws = null;
            try
            {
                ws = await CreateWebSocketAsync(cancellationToken).ConfigureAwait(false);
                _connectionToken = cancellationToken;
                _ws = ws;
                _unsuccessfulReconnects = 0;
                SetStatus(YjsConnectionStatus.Connected);
                // always send sync step 1 when connected
                byte[] step1;
                lock (Doc) step1 = YWebSocketProtocol.EncodeSyncStep1(Doc);
                await SendAsync(step1, cancellationToken).ConfigureAwait(false);
                // broadcast local awareness state
                if (Awareness.GetLocalState() != null)
                {
                    await SendAsync(YWebSocketProtocol.EncodeAwareness(Awareness.EncodeUpdate(new[] { Doc.ClientID })), cancellationToken).ConfigureAwait(false);
                }
                await ReceiveLoopAsync(ws, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                ConnectionError?.Invoke(ex);
            }
            finally
            {
                _ws = null;
                if (ws != null)
                {
                    try
                    {
                        if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
                        {
                            using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, closeCts.Token).ConfigureAwait(false);
                        }
                    }
                    catch { /* best effort */ }
                    ws.Dispose();
                }
                var wasConnected = Status == YjsConnectionStatus.Connected;
                Synced = false;
                SetStatus(YjsConnectionStatus.Disconnected);
                if (wasConnected)
                {
                    // update awareness (all users except local are null)
                    var others = Awareness.GetStates().Keys.Where(c => c != Doc.ClientID).ToList();
                    if (others.Count > 0) Awareness.RemoveStates(others, this);
                }
                else
                {
                    _unsuccessfulReconnects++;
                }
            }
            if (!_shouldConnect || cancellationToken.IsCancellationRequested) break;
            // exponential back-off, like y-websocket
            var delay = Math.Min(Math.Pow(2, _unsuccessfulReconnects) * 100, _options.MaxBackoffTime.TotalMilliseconds);
            try { await Task.Delay(TimeSpan.FromMilliseconds(delay), cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task<WebSocket> CreateWebSocketAsync(CancellationToken cancellationToken)
    {
        if (_options.WebSocketFactory != null) return await _options.WebSocketFactory(Url, cancellationToken).ConfigureAwait(false);
        var client = new ClientWebSocket();
        _options.ConfigureClientWebSocket?.Invoke(client.Options);
        try
        {
            await client.ConnectAsync(Url, cancellationToken).ConfigureAwait(false);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private async Task ReceiveLoopAsync(WebSocket ws, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (ws.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var result = await ws.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return;
            if (message.Length + result.Count > _options.MaxMessageSize) throw new InvalidDataException("Incoming message exceeds MaxMessageSize");
            message.Write(buffer.AsSpan(0, result.Count));
            if (!result.EndOfMessage) continue;
            var payload = message.ToArray();
            message.SetLength(0);
            if (result.MessageType != WebSocketMessageType.Binary || payload.Length == 0) continue;
            var reply = HandleMessage(payload);
            if (reply != null) await SendAsync(reply, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Handles one frame from the server; returns the reply frame, if any.</summary>
    private byte[]? HandleMessage(byte[] data)
    {
        var decoder = new Decoder(data);
        var messageType = (int)decoder.ReadVarUint();
        switch (messageType)
        {
            case YWebSocketProtocol.MessageSync:
            {
                var encoder = new Encoder();
                encoder.WriteVarUint(YWebSocketProtocol.MessageSync);
                int syncType;
                lock (Doc) syncType = SyncProtocol.ReadSyncMessage(decoder, encoder, Doc, this);
                if (syncType == SyncProtocol.MessageYjsSyncStep2) Synced = true;
                return encoder.Length > 1 ? encoder.ToArray() : null;
            }
            case YWebSocketProtocol.MessageAwareness:
                Awareness.ApplyUpdate(decoder.ReadVarUint8Array(), this);
                return null;
            case YWebSocketProtocol.MessageQueryAwareness:
                return YWebSocketProtocol.EncodeAwareness(Awareness.EncodeUpdate(Awareness.GetStates().Keys.ToList()));
            case YWebSocketProtocol.MessageAuth:
            {
                // y-protocols/auth: varuint 0 = permission denied, varstring reason
                if (decoder.ReadVarUint() == 0)
                {
                    var reason = decoder.ReadVarString();
                    PermissionDenied?.Invoke(reason);
                }
                return null;
            }
            default:
                return null;
        }
    }

    /// <summary>Sends a frame on the current connection; a disconnect cancels it. Failures are left to the receive loop.</summary>
    private async Task SendAsync(byte[] data, CancellationToken cancellationToken)
    {
        var ws = _ws;
        if (ws == null || ws.State != WebSocketState.Open) return;
        try
        {
            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            return;
        }
        try
        {
            if (ws.State == WebSocketState.Open)
            {
                await ws.SendAsync(data, WebSocketMessageType.Binary, true, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            // the receive loop notices the broken connection and reconnects
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Stops syncing, closes the connection and detaches from the document. The document itself stays usable.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposeCts.IsCancellationRequested) return;
        if (_resyncTimer != null) await _resyncTimer.DisposeAsync().ConfigureAwait(false);
        Doc.Update -= _onDocUpdate;
        Awareness.Update -= _onAwarenessUpdate;
        Doc.Destroyed -= _onDocDestroyed;
        // tell the server (and through it the other clients) that we are gone, like y-websocket does on unload
        if (Status == YjsConnectionStatus.Connected)
        {
            try
            {
                var local = Awareness.GetLocalState();
                if (local != null)
                {
                    Awareness.SetLocalState(null);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_connectionToken);
                    timeout.CancelAfter(TimeSpan.FromSeconds(2));
                    await SendAsync(YWebSocketProtocol.EncodeAwareness(Awareness.EncodeUpdate(new[] { Doc.ClientID })), timeout.Token).ConfigureAwait(false);
                }
            }
            catch { /* best effort */ }
        }
        await DisconnectAsync().ConfigureAwait(false);
        await _disposeCts.CancelAsync().ConfigureAwait(false);
        if (_options.Awareness == null) Awareness.Destroy();
        _disposeCts.Dispose();
        _sendLock.Dispose();
    }
}
