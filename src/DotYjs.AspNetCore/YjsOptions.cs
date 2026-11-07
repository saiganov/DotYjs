using Microsoft.AspNetCore.Http;

namespace DotYjs.AspNetCore;

/// <summary>Options for the DotYjs WebSocket server.</summary>
public sealed class YjsOptions
{
    /// <summary>
    /// Interval of WebSocket keep-alive pings sent to clients (y-websocket uses 30 s). Set to
    /// <see cref="TimeSpan.Zero"/> to disable.
    /// </summary>
    public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Maximum size of a single incoming WebSocket message in bytes (default 100 MB, like the ws library used by y-websocket).</summary>
    public int MaxMessageSize { get; set; } = 100 * 1024 * 1024;

    /// <summary>Enable Yjs garbage collection on server-side documents (y-websocket: GC env var, default true).</summary>
    public bool Gc { get; set; } = true;

    /// <summary>
    /// Called before a WebSocket is accepted. Return false to reject the connection with 403.
    /// Receives the HTTP context (headers, query string, user) and the document name.
    /// </summary>
    public Func<HttpContext, string, ValueTask<bool>>? Authorize { get; set; }

    /// <summary>
    /// Maps the request to a document name. By default the <c>docName</c> route value is used, falling back to the
    /// request path without the leading slash (y-websocket uses the URL path as room name).
    /// </summary>
    public Func<HttpContext, string>? DocumentNameSelector { get; set; }

    /// <summary>
    /// If true (default), a document is unloaded from memory (after <see cref="IYjsPersistence.WriteStateAsync"/>)
    /// when its last connection closes, like y-websocket does.
    /// </summary>
    public bool UnloadWhenIdle { get; set; } = true;
}
