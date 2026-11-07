using System.Net.WebSockets;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http;

namespace DotYjs.AspNetCore;

/// <summary>One WebSocket client attached to a <see cref="YjsDocument"/>.</summary>
public sealed class YjsConnection
{
    private readonly Channel<byte[]> _outbox = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly HashSet<long> _controlledIds = new();

    internal YjsConnection(WebSocket socket, YjsDocument document, HttpContext? httpContext)
    {
        Socket = socket;
        Document = document;
        HttpContext = httpContext;
    }

    /// <summary>Unique id of this connection (used in logs).</summary>
    public string Id { get; } = Guid.NewGuid().ToString("N");
    /// <summary>The WebSocket of the client.</summary>
    public WebSocket Socket { get; }
    /// <summary>The document this connection is attached to.</summary>
    public YjsDocument Document { get; }

    /// <summary>The HTTP context of the upgrade request (user, headers, query), if any.</summary>
    public HttpContext? HttpContext { get; }

    /// <summary>Arbitrary per-connection data for the host application.</summary>
    public IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();

    /// <summary>Awareness client ids that were announced through this connection; removed when it closes.</summary>
    public IReadOnlyCollection<long> ControlledIds
    {
        get { lock (_controlledIds) return _controlledIds.ToArray(); }
    }

    internal void TrackAwareness(IEnumerable<long> added, IEnumerable<long> removed)
    {
        lock (_controlledIds)
        {
            foreach (var id in added) _controlledIds.Add(id);
            foreach (var id in removed) _controlledIds.Remove(id);
        }
    }

    /// <summary>Queues a binary message for this client. Returns false when the connection is closing.</summary>
    public bool Send(byte[] message) => _outbox.Writer.TryWrite(message);

    internal void Complete() => _outbox.Writer.TryComplete();

    internal async Task RunSendLoopAsync(CancellationToken cancellationToken)
    {
        await foreach (var message in _outbox.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (Socket.State != WebSocketState.Open) break;
            await Socket.SendAsync(message, WebSocketMessageType.Binary, true, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public override string ToString() => $"YjsConnection({Id}, {Document.Name})";
}
