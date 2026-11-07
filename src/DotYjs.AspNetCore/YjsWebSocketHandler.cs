using System.Net.WebSockets;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DotYjs.AspNetCore;

/// <summary>Accepts a y-websocket client connection and runs it until the socket closes.</summary>
public sealed class YjsWebSocketHandler
{
    private readonly YjsDocumentManager _documents;
    private readonly YjsOptions _options;
    private readonly ILogger<YjsWebSocketHandler> _logger;

    /// <summary>Creates the handler.</summary>
    public YjsWebSocketHandler(YjsDocumentManager documents, IOptions<YjsOptions> options, ILogger<YjsWebSocketHandler> logger)
    {
        _documents = documents;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Resolves the document name for a request (see <see cref="YjsOptions.DocumentNameSelector"/>).</summary>
    public string GetDocumentName(HttpContext context)
    {
        if (_options.DocumentNameSelector != null) return _options.DocumentNameSelector(context);
        if (context.GetRouteValue("docName") is string routeName && routeName.Length > 0) return routeName;
        return Uri.UnescapeDataString((context.Request.Path.Value ?? "/").TrimStart('/'));
    }

    /// <summary>Endpoint delegate: upgrades the request to a WebSocket and serves the document.</summary>
    public async Task HandleAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("Expected a WebSocket request (make sure app.UseWebSockets() is registered before routing).", context.RequestAborted).ConfigureAwait(false);
            return;
        }
        var docName = GetDocumentName(context);
        if (_options.Authorize != null && !await _options.Authorize(context, docName).ConfigureAwait(false))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        var acceptContext = new WebSocketAcceptContext();
        if (_options.KeepAliveInterval > TimeSpan.Zero) acceptContext.KeepAliveInterval = _options.KeepAliveInterval;
        using var socket = await context.WebSockets.AcceptWebSocketAsync(acceptContext).ConfigureAwait(false);
        await ServeAsync(socket, docName, context, context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>Serves an already accepted WebSocket for the given document until it closes.</summary>
    public async Task ServeAsync(WebSocket socket, string docName, HttpContext? httpContext, CancellationToken cancellationToken)
    {
        YjsDocument doc;
        YjsConnection connection;
        while (true)
        {
            doc = await _documents.GetOrCreateAsync(docName, cancellationToken).ConfigureAwait(false);
            connection = new YjsConnection(socket, doc, httpContext);
            if (_documents.TryAttach(doc, connection)) break;
        }
        _logger.LogDebug("Client {Connection} joined document {Document}", connection.Id, docName);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var sendLoop = connection.RunSendLoopAsync(cts.Token);
        try
        {
            doc.SendInitialMessages(connection);
            await ReceiveLoopAsync(socket, doc, connection, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* connection closed */ }
        catch (WebSocketException ex)
        {
            _logger.LogDebug(ex, "WebSocket error on {Connection}", connection.Id);
        }
        finally
        {
            connection.Complete();
            await cts.CancelAsync().ConfigureAwait(false);
            try { await sendLoop.ConfigureAwait(false); } catch { /* closing */ }
            await _documents.DetachAsync(doc, connection).ConfigureAwait(false);
            _logger.LogDebug("Client {Connection} left document {Document}", connection.Id, docName);
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, closeCts.Token).ConfigureAwait(false);
                }
                catch { /* best effort */ }
            }
        }
    }

    private async Task ReceiveLoopAsync(WebSocket socket, YjsDocument doc, YjsConnection connection, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return;
            if (message.Length + result.Count > _options.MaxMessageSize)
            {
                _logger.LogWarning("Closing {Connection}: message exceeds {Max} bytes", connection.Id, _options.MaxMessageSize);
                await socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "message too big", cancellationToken).ConfigureAwait(false);
                return;
            }
            message.Write(buffer.AsSpan(0, result.Count));
            if (!result.EndOfMessage) continue;
            var payload = message.ToArray();
            message.SetLength(0);
            if (result.MessageType != WebSocketMessageType.Binary || payload.Length == 0) continue;
            try
            {
                doc.HandleMessage(connection, payload);
            }
            catch (Exception ex)
            {
                // like y-websocket: log and keep the connection alive
                _logger.LogError(ex, "Error while handling a message from {Connection} on document {Document}", connection.Id, doc.Name);
            }
        }
    }
}
