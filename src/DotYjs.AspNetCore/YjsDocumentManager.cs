using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DotYjs.AspNetCore;

/// <summary>
/// Holds the documents currently loaded on this server (y-websocket's <c>docs</c> map). Register it with
/// <see cref="YjsServiceCollectionExtensions.AddYjs"/>; inject it to read or modify documents from application code.
/// </summary>
#pragma warning disable VSTHRD011, VSTHRD002 // Lazy<Task<T>> is awaited (never blocked on); .Result is only read after IsCompletedSuccessfully
public sealed class YjsDocumentManager
{
    private readonly ConcurrentDictionary<string, Lazy<Task<YjsDocument>>> _docs = new();
    private readonly object _gate = new();
    private readonly YjsOptions _options;
    private readonly IYjsPersistence? _persistence;
    private readonly IYjsBackplane? _backplane;
    private readonly ILogger<YjsDocumentManager> _logger;

    /// <summary>Creates the manager; <paramref name="persistence"/> and <paramref name="backplane"/> are optional services.</summary>
    public YjsDocumentManager(IOptions<YjsOptions> options, ILogger<YjsDocumentManager> logger, IYjsPersistence? persistence = null, IYjsBackplane? backplane = null)
    {
        _options = options.Value;
        _logger = logger;
        _persistence = persistence;
        _backplane = backplane;
    }

    /// <summary>Unique id of this server node, used to filter out its own backplane messages.</summary>
    public string NodeId { get; } = Guid.NewGuid().ToString("N");

    /// <summary>Raised after a document was loaded (and bound to persistence).</summary>
    public event Action<YjsDocument>? DocumentLoaded;

    /// <summary>Raised after a document was unloaded.</summary>
    public event Action<YjsDocument>? DocumentUnloaded;

    /// <summary>Names of the documents currently in memory.</summary>
    public IReadOnlyCollection<string> DocumentNames => _docs.Where(kv => kv.Value.IsValueCreated && kv.Value.Value.IsCompletedSuccessfully && !kv.Value.Value.Result.IsUnloaded).Select(kv => kv.Key).ToArray();

    /// <summary>Returns the loaded document or null if it is not in memory.</summary>
    public YjsDocument? TryGet(string name)
    {
        if (_docs.TryGetValue(name, out var lazy) && lazy.IsValueCreated && lazy.Value.IsCompletedSuccessfully)
        {
            var doc = lazy.Value.Result;
            return doc.IsUnloaded ? null : doc;
        }
        return null;
    }

    /// <summary>Gets the document with the given name, loading it (via <see cref="IYjsPersistence.BindStateAsync"/>) if necessary.</summary>
    public async Task<YjsDocument> GetOrCreateAsync(string name, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var lazy = _docs.GetOrAdd(name, n => new Lazy<Task<YjsDocument>>(() => CreateAsync(n), LazyThreadSafetyMode.ExecutionAndPublication));
            YjsDocument doc;
            try
            {
                doc = await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                _docs.TryRemove(new KeyValuePair<string, Lazy<Task<YjsDocument>>>(name, lazy));
                throw;
            }
            if (!doc.IsUnloading) return doc;
            // unloading in the meantime: drop the stale entry and try again
            _docs.TryRemove(new KeyValuePair<string, Lazy<Task<YjsDocument>>>(name, lazy));
        }
    }

    private async Task<YjsDocument> CreateAsync(string name)
    {
        var doc = new YjsDocument(name, _options.Gc, _logger);
        if (_persistence != null)
        {
            await _persistence.BindStateAsync(name, doc.Doc, CancellationToken.None).ConfigureAwait(false);
        }
        if (_backplane != null)
        {
            doc.AttachBackplane(NodeId, message => _backplane.PublishAsync(name, message, CancellationToken.None));
            doc.BackplaneSubscription = await _backplane.SubscribeAsync(name, message =>
            {
                try
                {
                    doc.HandleBackplaneMessage(message);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to apply backplane message {Kind} to document {Document}", message.Kind, name);
                }
                return Task.CompletedTask;
            }, CancellationToken.None).ConfigureAwait(false);
            // exchange state with the nodes that already have the document in memory (both directions)
            doc.RequestBackplaneSync();
        }
        _logger.LogDebug("Loaded Yjs document {Document}", name);
        DocumentLoaded?.Invoke(doc);
        return doc;
    }

    /// <summary>Attaches a connection; returns false if the document was unloaded concurrently (caller should re-fetch it).</summary>
    internal bool TryAttach(YjsDocument doc, YjsConnection connection)
    {
        lock (_gate) return doc.TryAddConnection(connection);
    }

    /// <summary>Detaches a connection and unloads the document when it has no clients left (if configured).</summary>
    internal Task DetachAsync(YjsDocument doc, YjsConnection connection)
    {
        lock (_gate)
        {
            var remaining = doc.RemoveConnection(connection);
            if (remaining > 0 || !_options.UnloadWhenIdle || doc.IsUnloading) return Task.CompletedTask;
            // From now on no connection may attach to this instance (TryAttach checks the flag under the same gate),
            // so edits cannot slip in while the state is being written.
            doc.IsUnloading = true;
            // Replace the entry with one that waits for the unload to finish before loading the document again,
            // so that a client reconnecting during WriteStateAsync never sees stale persisted state.
            var unload = UnloadAsync(doc);
            Lazy<Task<YjsDocument>>? pending = null;
            pending = new Lazy<Task<YjsDocument>>(async () =>
            {
                var unloaded = await unload.ConfigureAwait(false);
                // persistence failed: the retained instance stays the live document
                return unloaded ? await CreateAsync(doc.Name).ConfigureAwait(false) : doc;
            }, LazyThreadSafetyMode.ExecutionAndPublication);
            _docs[doc.Name] = pending;
            _ = unload.ContinueWith(t =>
            {
                lock (_gate)
                {
                    if (!t.Result)
                    {
                        // keep the document in memory so the write can be retried when the next client leaves
                        var retained = new Lazy<Task<YjsDocument>>(() => Task.FromResult(doc), LazyThreadSafetyMode.ExecutionAndPublication);
                        _ = retained.Value; // materialize so TryGet sees it
                        _docs[doc.Name] = retained;
                    }
                    else if (!pending.IsValueCreated)
                    {
                        // nobody asked for the document while it was being written: forget it entirely
                        _docs.TryRemove(new KeyValuePair<string, Lazy<Task<YjsDocument>>>(doc.Name, pending));
                    }
                }
            }, TaskScheduler.Default);
            return unload;
        }
    }

    /// <summary>Persists and unloads the document. Returns false (and keeps the document alive) when persisting failed.</summary>
    private async Task<bool> UnloadAsync(YjsDocument doc)
    {
        if (_persistence != null)
        {
            try
            {
                await _persistence.WriteStateAsync(doc.Name, doc.Doc, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to persist Yjs document {Document}; keeping it in memory so the write can be retried", doc.Name);
                lock (_gate) doc.IsUnloading = false;
                return false;
            }
        }
        if (doc.BackplaneSubscription != null)
        {
            try { await doc.BackplaneSubscription.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to unsubscribe document {Document} from the backplane", doc.Name); }
            doc.BackplaneSubscription = null;
        }
        doc.Unload();
        _logger.LogDebug("Unloaded Yjs document {Document}", doc.Name);
        DocumentUnloaded?.Invoke(doc);
        return true;
    }
}
