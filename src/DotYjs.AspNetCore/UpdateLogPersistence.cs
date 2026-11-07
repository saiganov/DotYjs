using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DotYjs.AspNetCore;

/// <summary>Settings of <see cref="UpdateLogPersistence"/>.</summary>
public sealed class UpdateLogPersistenceOptions
{
    /// <summary>
    /// Compact the log while the document is loaded, once this many rows have accumulated since the last compaction.
    /// 0 disables compaction while the document is in memory.
    /// </summary>
    public int CompactAfterUpdates { get; set; } = 500;

    /// <summary>Compact the log into one full-state row when the document is unloaded. Default true.</summary>
    public bool CompactOnUnload { get; set; } = true;
}

/// <summary>
/// <see cref="IYjsPersistence"/> on top of an <see cref="IYjsUpdateStore"/> (the same scheme as y-leveldb): the stored
/// updates are merged and applied on load, every local or remote update is appended in order, and the rows this node
/// has seen are compacted into a single row periodically and when the document is unloaded. Several server nodes may
/// share one store: compaction only ever replaces rows this node loaded or wrote itself, so a row appended concurrently
/// by another node is never lost.
/// </summary>
public sealed class UpdateLogPersistence : IYjsPersistence
{
    private readonly IYjsUpdateStore _store;
    private readonly UpdateLogPersistenceOptions _options;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<Doc, Binding> _bindings = new();

    public UpdateLogPersistence(IYjsUpdateStore store) : this(store, (UpdateLogPersistenceOptions?)null, null) { }

    public UpdateLogPersistence(IYjsUpdateStore store, UpdateLogPersistenceOptions? options) : this(store, options, null) { }

    public UpdateLogPersistence(IYjsUpdateStore store, IOptions<UpdateLogPersistenceOptions> options, ILogger<UpdateLogPersistence> logger)
        : this(store, options?.Value, logger) { }

    public UpdateLogPersistence(IYjsUpdateStore store, UpdateLogPersistenceOptions? options, ILogger? logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = options ?? new UpdateLogPersistenceOptions();
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>The store this persistence writes to.</summary>
    public IYjsUpdateStore Store => _store;

    public async Task BindStateAsync(string docName, Doc doc, CancellationToken cancellationToken)
    {
        var stored = await _store.LoadAsync(docName, cancellationToken).ConfigureAwait(false);
        byte[]? persisted = null;
        if (stored.Count > 0)
        {
            persisted = stored.Count == 1 ? stored[0].Update : Updates.MergeUpdates(stored.Select(r => r.Update).ToList());
            UpdateCodec.ApplyUpdate(doc, persisted, this);
        }
        var binding = new Binding(this, docName, persisted, stored.Select(r => r.Id), failed: false);
        if (!_bindings.TryAdd(doc, binding)) throw new InvalidOperationException($"Document {docName} is already bound.");
        // the document may already hold state the store does not know (e.g. it was pre-filled before binding)
        var missing = UpdateCodec.EncodeStateAsUpdate(doc, persisted == null ? null : Updates.EncodeStateVectorFromUpdate(persisted));
        if (!IsEmptyUpdate(missing)) binding.Enqueue(missing);
        if (stored.Count > 1 && _options.CompactAfterUpdates > 0 && stored.Count >= _options.CompactAfterUpdates)
        {
            binding.Enqueue(Binding.CompactMarker);
        }
        binding.Attach(doc);
    }

    public async Task WriteStateAsync(string docName, Doc doc, CancellationToken cancellationToken)
    {
        if (!_bindings.TryRemove(doc, out var binding))
        {
            // never bound here (or already written): store the full state as a new row
            await _store.AppendAsync(docName, UpdateCodec.EncodeStateAsUpdate(doc), cancellationToken).ConfigureAwait(false);
            return;
        }
        binding.Detach(doc);
        var failed = await binding.CompleteAsync().ConfigureAwait(false);
        if (!failed && !_options.CompactOnUnload) return;
        // one full-state row replaces everything this node has seen; it also repairs a log that lost an append
        var state = UpdateCodec.EncodeStateAsUpdate(doc);
        var ids = binding.KnownIds;
        try
        {
            await _store.CompactAsync(docName, state, ids, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // the document manager keeps the document alive after a failed write: keep persisting its edits so
            // the next unload can retry with everything that happened in the meantime
            var restored = new Binding(this, docName, state, ids, failed: true);
            _bindings[doc] = restored;
            restored.Attach(doc);
            throw;
        }
    }

    /// <summary>Completes once every update received so far has been written (for tests and graceful shutdown).</summary>
    public Task FlushAsync(Doc doc) => _bindings.TryGetValue(doc, out var b) ? b.FlushAsync() : Task.CompletedTask;

    internal static bool IsEmptyUpdate(byte[] update) => update.Length <= 2; // 0 structs, 0 delete sets

    /// <summary>
    /// Per-document state: a queue that writes updates strictly in the order they happened. Compaction never touches
    /// the live document (which may be in use under the server's lock): it merges the base state with the updates
    /// appended since, which yields the same content as encoding the document, and replaces exactly the rows it knows.
    /// </summary>
    private sealed class Binding
    {
#pragma warning disable CA1825, MA0005 // the markers are compared by reference, so they must be distinct instances (Array.Empty would be shared)
        public static readonly byte[] CompactMarker = new byte[0];
        private static readonly byte[] FlushMarker = new byte[0];
#pragma warning restore CA1825, MA0005

        private readonly UpdateLogPersistence _owner;
        private readonly string _docName;
        private readonly Channel<byte[]> _queue = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
        private readonly Queue<TaskCompletionSource> _flushes = new();
        private readonly Task _writer;
        private byte[]? _base;
        private readonly List<byte[]> _appended = new();
        private readonly List<long> _knownIds;
        private bool _failed;

        public Binding(UpdateLogPersistence owner, string docName, byte[]? persisted, IEnumerable<long> knownIds, bool failed)
        {
            _owner = owner;
            _docName = docName;
            _base = persisted;
            _knownIds = knownIds.ToList();
            _failed = failed;
            _writer = Task.Run(WriteLoopAsync);
        }

        /// <summary>Ids of the rows this node loaded or wrote; only these may be replaced by a compaction.</summary>
        public IReadOnlyCollection<long> KnownIds => _knownIds;

        public void Attach(Doc doc)
        {
            doc.Update += OnUpdate;
            doc.Destroyed += OnDestroyed;
        }

        public void Detach(Doc doc)
        {
            doc.Update -= OnUpdate;
            doc.Destroyed -= OnDestroyed;
        }

        private void OnUpdate(byte[] update, object? origin, Doc doc, Transaction transaction)
        {
            if (ReferenceEquals(origin, _owner)) return; // the state we applied ourselves on load
            Enqueue(update);
        }

        private void OnDestroyed(Doc doc)
        {
            // unloaded without WriteStateAsync (e.g. shutdown): stop writing, nothing else to do
            _queue.Writer.TryComplete();
        }

        public void Enqueue(byte[] update) => _queue.Writer.TryWrite(update);

        /// <summary>Waits until everything queued so far is written.</summary>
        public async Task FlushAsync()
        {
            var marker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_flushes) _flushes.Enqueue(marker);
            Enqueue(FlushMarker);
            await marker.Task.ConfigureAwait(false);
        }

        /// <summary>Stops the writer; returns true when an append failed (the caller then rewrites the full state).</summary>
        public async Task<bool> CompleteAsync()
        {
            _queue.Writer.TryComplete();
            await _writer.ConfigureAwait(false);
            return _failed;
        }

        private async Task WriteLoopAsync()
        {
            await foreach (var item in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (ReferenceEquals(item, FlushMarker))
                {
                    TaskCompletionSource tcs;
                    lock (_flushes) tcs = _flushes.Dequeue();
                    tcs.TrySetResult();
                    continue;
                }
                try
                {
                    if (ReferenceEquals(item, CompactMarker))
                    {
                        await CompactAsync().ConfigureAwait(false);
                        continue;
                    }
                    _appended.Add(item);
                    var id = await _owner._store.AppendAsync(_docName, item, CancellationToken.None).ConfigureAwait(false);
                    _knownIds.Add(id);
                    var limit = _owner._options.CompactAfterUpdates;
                    if (limit > 0 && _knownIds.Count >= limit) await CompactAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _failed = true;
                    _owner._logger.LogError(ex, "Failed to persist an update of Yjs document {Document}; the full state is rewritten on unload", _docName);
                }
            }
            lock (_flushes)
            {
                while (_flushes.Count > 0) _flushes.Dequeue().TrySetResult();
            }
        }

        private async Task CompactAsync()
        {
            if (_failed) return; // the merged state would miss the lost update; the unload rewrite repairs the log
            var parts = new List<byte[]>(_appended.Count + 1);
            if (_base != null) parts.Add(_base);
            parts.AddRange(_appended);
            if (parts.Count == 0 || _knownIds.Count <= 1) return;
            var state = parts.Count == 1 ? parts[0] : Updates.MergeUpdates(parts);
            var id = await _owner._store.CompactAsync(_docName, state, _knownIds.ToArray(), CancellationToken.None).ConfigureAwait(false);
            _base = state;
            _appended.Clear();
            _knownIds.Clear();
            _knownIds.Add(id);
        }
    }
}
