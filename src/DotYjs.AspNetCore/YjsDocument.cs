using System.Collections.Concurrent;
using DotYjs.Lib0;
using DotYjs.Protocols;
using Microsoft.Extensions.Logging;

namespace DotYjs.AspNetCore;

/// <summary>
/// A shared document ("room") hosted by the server: the <see cref="Doc"/>, its <see cref="Awareness"/> and the
/// connected clients. Mirrors <c>WSSharedDoc</c> of y-websocket.
/// <para>
/// The Yjs document is not thread-safe: always mutate it through <see cref="Transact"/> or inside
/// <c>lock (SyncRoot)</c>. Changes made this way are broadcast to all connected clients automatically.
/// </para>
/// </summary>
#pragma warning disable IDISP006 // owned Awareness/Doc are torn down by Unload(), driven by the YjsDocumentManager
public sealed class YjsDocument
{
    private readonly ConcurrentDictionary<YjsConnection, byte> _connections = new();
    private readonly ILogger _logger;

    internal YjsDocument(string name, bool gc, ILogger logger)
    {
        Name = name;
        _logger = logger;
        Doc = new Doc(new DocOptions { Gc = gc });
        Awareness = new Awareness(Doc);
        Awareness.SetLocalState(null); // the server has no awareness state of its own (like y-websocket)
        Doc.Update += OnDocUpdate;
        Awareness.Update += OnAwarenessUpdate;
    }

    /// <summary>The document name (the room name of y-websocket).</summary>
    public string Name { get; }
    /// <summary>The shared document. Lock on it when you change it from your own code, or use <see cref="Doc.Transact"/> under the same lock.</summary>
    public Doc Doc { get; }
    /// <summary>The awareness (cursors, user names) of the clients connected to this document.</summary>
    public Awareness Awareness { get; }

    /// <summary>Lock protecting <see cref="Doc"/>.</summary>
    public object SyncRoot { get; } = new();

    /// <summary>The number of WebSocket clients connected to this document on this node.</summary>
    public int ConnectionCount => _connections.Count;
    /// <summary>A snapshot of the WebSocket clients connected to this document on this node.</summary>
    public IReadOnlyCollection<YjsConnection> Connections => _connections.Keys.ToArray();

    /// <summary>True once the document was unloaded from the <see cref="YjsDocumentManager"/>; it must not be used anymore.</summary>
    public bool IsUnloaded { get; private set; }

    /// <summary>
    /// True while the document is being persisted before unloading (or already unloaded). No new connection may attach
    /// in this state; the manager hands out a fresh instance once the unload finished.
    /// </summary>
    public bool IsUnloading { get; internal set; }

    /// <summary>Raised when a client attached.</summary>
    public event Action<YjsConnection>? ConnectionAdded;

    /// <summary>Raised when a client detached.</summary>
    public event Action<YjsConnection>? ConnectionRemoved;

    /// <summary>Runs <paramref name="f"/> in a transaction while holding <see cref="SyncRoot"/>. Resulting updates are broadcast to clients.</summary>
    public void Transact(Action<Doc> f, object? origin = null)
    {
        lock (SyncRoot) Doc.Transact(_ => f(Doc), origin ?? this);
    }

    /// <summary>Reads from the document while holding <see cref="SyncRoot"/>.</summary>
    public T Read<T>(Func<Doc, T> f)
    {
        lock (SyncRoot) return f(Doc);
    }

    /// <summary>Applies a Yjs update (v1) received out of band, e.g. from another server node, and broadcasts it.</summary>
    public void ApplyUpdate(byte[] update, object? origin = null)
    {
        lock (SyncRoot) UpdateCodec.ApplyUpdate(Doc, update, origin ?? this);
    }

    /// <summary>Full state of the document as a v1 update.</summary>
    public byte[] EncodeStateAsUpdate()
    {
        lock (SyncRoot) return UpdateCodec.EncodeStateAsUpdate(Doc);
    }

    /// <summary>Sends a raw WebSocket frame to all clients (optionally except one).</summary>
    public void Broadcast(byte[] message, YjsConnection? except = null)
    {
        foreach (var conn in _connections.Keys)
        {
            if (!ReferenceEquals(conn, except)) conn.Send(message);
        }
    }

    private void OnDocUpdate(byte[] update, object? origin, Doc doc, Transaction transaction)
    {
        Broadcast(YWebSocketProtocol.EncodeUpdate(update));
        if (!ReferenceEquals(origin, BackplaneOrigin)) PublishToBackplane(YjsBackplaneMessageKind.DocumentUpdate, update);
    }

    private void OnAwarenessUpdate(AwarenessChange change, object? origin)
    {
        var changed = change.All.ToList();
        if (origin is YjsConnection conn) conn.TrackAwareness(change.Added, change.Removed);
        var encoded = Awareness.EncodeUpdate(changed);
        Broadcast(YWebSocketProtocol.EncodeAwareness(encoded));
        if (!ReferenceEquals(origin, BackplaneOrigin)) PublishToBackplane(YjsBackplaneMessageKind.AwarenessUpdate, encoded);
    }

    // ---- multi-node backplane ----

    /// <summary>Transaction/awareness origin used for changes that arrived from other nodes (they are not re-published).</summary>
    public object BackplaneOrigin { get; } = new();

    internal string? NodeId { get; set; }
    internal IAsyncDisposable? BackplaneSubscription { get; set; }

    // Outgoing messages are queued and published by a single background loop: publishing never happens while
    // SyncRoot (or the awareness lock) is held, which would otherwise allow two nodes editing concurrently to
    // deadlock through a synchronous backplane, and per-document ordering is preserved.
    private System.Threading.Channels.Channel<YjsBackplaneMessage>? _outgoing;

    internal void AttachBackplane(string nodeId, Func<YjsBackplaneMessage, Task> publisher)
    {
        NodeId = nodeId;
        _outgoing = System.Threading.Channels.Channel.CreateUnbounded<YjsBackplaneMessage>(new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true });
        _ = Task.Run(() => PublishLoopAsync(_outgoing.Reader, publisher), CancellationToken.None);
    }

    private async Task PublishLoopAsync(System.Threading.Channels.ChannelReader<YjsBackplaneMessage> reader, Func<YjsBackplaneMessage, Task> publisher)
    {
        await foreach (var message in reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                await publisher(message).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to publish {Kind} for document {Document} to the backplane", message.Kind, Name);
            }
        }
    }

    private void PublishToBackplane(YjsBackplaneMessageKind kind, byte[] payload)
        => _outgoing?.Writer.TryWrite(new YjsBackplaneMessage(kind, payload, NodeId!));

    /// <summary>Applies a message received from another node.</summary>
    internal void HandleBackplaneMessage(YjsBackplaneMessage message)
    {
        if (IsUnloaded || message.NodeId == NodeId) return;
        switch (message.Kind)
        {
            case YjsBackplaneMessageKind.DocumentUpdate:
                lock (SyncRoot) UpdateCodec.ApplyUpdate(Doc, message.Payload, BackplaneOrigin);
                break;
            case YjsBackplaneMessageKind.AwarenessUpdate:
                Awareness.ApplyUpdate(message.Payload, BackplaneOrigin);
                break;
            case YjsBackplaneMessageKind.SyncRequest:
            {
                // answer with what the requester is missing plus our state vector, so it can send us what we miss
                var e = new Encoder();
                lock (SyncRoot)
                {
                    e.WriteVarUint8Array(UpdateCodec.EncodeStateAsUpdate(Doc, message.Payload));
                    e.WriteVarUint8Array(UpdateCodec.EncodeStateVector(Doc));
                }
                PublishToBackplane(YjsBackplaneMessageKind.SyncResponse, e.ToArray());
                var states = Awareness.GetStates();
                if (states.Count > 0) PublishToBackplane(YjsBackplaneMessageKind.AwarenessUpdate, Awareness.EncodeUpdate(states.Keys.ToList()));
                break;
            }
            case YjsBackplaneMessageKind.SyncResponse:
            {
                var d = new Decoder(message.Payload);
                var update = d.ReadVarUint8Array();
                var responderSv = d.ReadVarUint8Array();
                byte[] missingOnResponder;
                lock (SyncRoot)
                {
                    UpdateCodec.ApplyUpdate(Doc, update, BackplaneOrigin);
                    missingOnResponder = UpdateCodec.EncodeStateAsUpdate(Doc, responderSv);
                }
                // an update with no structs and an empty delete set is 2 bytes; anything larger carries data
                if (missingOnResponder.Length > 2) PublishToBackplane(YjsBackplaneMessageKind.DocumentUpdate, missingOnResponder);
                break;
            }
        }
    }

    /// <summary>Asks the other nodes for everything this node is missing.</summary>
    internal void RequestBackplaneSync()
    {
        byte[] sv;
        lock (SyncRoot) sv = UpdateCodec.EncodeStateVector(Doc);
        PublishToBackplane(YjsBackplaneMessageKind.SyncRequest, sv);
    }

    internal bool TryAddConnection(YjsConnection connection)
    {
        if (IsUnloading || IsUnloaded) return false;
        _connections[connection] = 0;
        ConnectionAdded?.Invoke(connection);
        return true;
    }

    internal int RemoveConnection(YjsConnection connection)
    {
        if (_connections.TryRemove(connection, out _))
        {
            Awareness.RemoveStates(connection.ControlledIds.ToArray(), null);
            ConnectionRemoved?.Invoke(connection);
        }
        return _connections.Count;
    }

    /// <summary>Sends the initial handshake (sync step 1 + current awareness states) to a freshly attached client.</summary>
    internal void SendInitialMessages(YjsConnection connection)
    {
        lock (SyncRoot) connection.Send(YWebSocketProtocol.EncodeSyncStep1(Doc));
        var states = Awareness.GetStates();
        if (states.Count > 0)
        {
            connection.Send(YWebSocketProtocol.EncodeAwareness(Awareness.EncodeUpdate(states.Keys.ToList())));
        }
    }

    /// <summary>Handles one y-websocket frame from a client.</summary>
    internal void HandleMessage(YjsConnection connection, byte[] message)
    {
        var decoder = new Decoder(message);
        var messageType = (int)decoder.ReadVarUint();
        switch (messageType)
        {
            case YWebSocketProtocol.MessageSync:
            {
                var encoder = new Encoder();
                encoder.WriteVarUint(YWebSocketProtocol.MessageSync);
                lock (SyncRoot) SyncProtocol.ReadSyncMessage(decoder, encoder, Doc, connection);
                // If the encoder only contains the type of reply message and no message, there is no need to send the message.
                if (encoder.Length > 1) connection.Send(encoder.ToArray());
                break;
            }
            case YWebSocketProtocol.MessageAwareness:
                Awareness.ApplyUpdate(decoder.ReadVarUint8Array(), connection);
                break;
            case YWebSocketProtocol.MessageQueryAwareness:
            {
                var states = Awareness.GetStates();
                connection.Send(YWebSocketProtocol.EncodeAwareness(Awareness.EncodeUpdate(states.Keys.ToList())));
                break;
            }
            case YWebSocketProtocol.MessageAuth:
                // clients do not send auth messages in y-websocket; ignore
                break;
            default:
                _logger.LogDebug("Unknown y-websocket message type {MessageType} on document {Document}", messageType, Name);
                break;
        }
    }

    internal void Unload()
    {
        IsUnloading = true;
        IsUnloaded = true;
        _outgoing?.Writer.TryComplete();
        Doc.Update -= OnDocUpdate;
        Awareness.Update -= OnAwarenessUpdate;
        Awareness.Destroy();
        foreach (var conn in _connections.Keys) conn.Complete();
        _connections.Clear();
        Doc.Destroy();
    }

    /// <inheritdoc />
    public override string ToString() => $"YjsDocument({Name}, {ConnectionCount} connections)";
}
