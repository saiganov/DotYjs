using DotYjs.Lib0;

namespace DotYjs.Protocols;

/// <summary>Which clients were added, updated or removed by an awareness update.</summary>
public sealed class AwarenessChange
{
    /// <summary>Creates the change description.</summary>
    public AwarenessChange(IReadOnlyList<long> added, IReadOnlyList<long> updated, IReadOnlyList<long> removed)
    {
        Added = added;
        Updated = updated;
        Removed = removed;
    }

    /// <summary>Clients whose state was added.</summary>
    public IReadOnlyList<long> Added { get; }
    /// <summary>Clients whose state was updated.</summary>
    public IReadOnlyList<long> Updated { get; }
    /// <summary>Clients whose state was removed.</summary>
    public IReadOnlyList<long> Removed { get; }

    /// <summary>All client ids touched by this change (added + updated + removed).</summary>
    public IEnumerable<long> All => Added.Concat(Updated).Concat(Removed);
}

/// <summary>Bookkeeping for the awareness state of one client.</summary>
public sealed class MetaClientState
{
    /// <summary>Creates the bookkeeping entry.</summary>
    public MetaClientState(long clock, long lastUpdated)
    {
        Clock = clock;
        LastUpdated = lastUpdated;
    }

    /// <summary>The clock of the client's state; it increases with every change, so older updates are ignored.</summary>
    public long Clock { get; }

    /// <summary>Unix time in milliseconds.</summary>
    public long LastUpdated { get; }
}

/// <summary>
/// Port of <c>y-protocols/awareness</c>: ephemeral per-client state (cursor, user name, …) that is not stored in the
/// document. Wire-compatible with JS <c>y-protocols</c> 1.x. States are JSON-like values
/// (usually <c>Dictionary&lt;string, object?&gt;</c>, see <see cref="JsonValues"/>).
/// <para>
/// All members are thread-safe; events are raised while the internal lock is held, so handlers must not block on
/// other work that needs this instance.
/// </para>
/// </summary>
public sealed class Awareness : IDisposable
{
    /// <summary>Remote clients are removed if they did not update their state for this many milliseconds.</summary>
    public const int OutdatedTimeout = 30000;

#if NET9_0_OR_GREATER
    private readonly Lock _lock = new();
#else
    private readonly object _lock = new();
#endif
    private readonly Dictionary<long, object?> _states = new();
    private readonly Dictionary<long, MetaClientState> _meta = new();
    private readonly Timer? _checkTimer;
    private readonly Action<Doc> _docDestroyed;
    private bool _destroyed;

    /// <summary>Creates an awareness instance for <paramref name="doc"/> with an empty local state.</summary>
    /// <param name="doc">The document this awareness instance belongs to.</param>
    /// <param name="runOutdatedCheck">
    /// When true (default, like JS), a timer runs every <see cref="OutdatedTimeout"/>/10 ms that renews the local
    /// state and removes outdated remote states. Set to false and call <see cref="CheckOutdated"/> yourself if you want
    /// to control the timing.
    /// </param>
    public Awareness(Doc doc, bool runOutdatedCheck = true)
    {
        Doc = doc;
        ClientID = doc.ClientID;
        _docDestroyed = _ => Destroy();
        doc.Destroyed += _docDestroyed;
        if (runOutdatedCheck)
        {
            _checkTimer = new Timer(_ => CheckOutdated(), null, OutdatedTimeout / 10, OutdatedTimeout / 10);
        }
        SetLocalState(new Dictionary<string, object?>());
    }

    /// <summary>The document this awareness belongs to.</summary>
    public Doc Doc { get; }
    /// <summary>The client id of the local client (the client id of the document).</summary>
    public long ClientID { get; }

    /// <summary>Emitted when a state was added, removed or its content actually changed. Origin is "local" for local changes.</summary>
    public event Action<AwarenessChange, object?>? Change;

    /// <summary>Emitted for every applied update, even if the content did not change (used to propagate clock renewals).</summary>
    public event Action<AwarenessChange, object?>? Update;

    /// <summary>Emitted when the awareness is destroyed.</summary>
    public event Action<Awareness>? Destroyed;

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>Renews the local state if it is older than half the timeout and removes outdated remote states.</summary>
    public void CheckOutdated()
    {
        lock (_lock)
        {
            if (_destroyed) return;
            var now = Now();
            if (GetLocalState() != null && OutdatedTimeout / 2 <= now - _meta[ClientID].LastUpdated)
            {
                // renew local clock
                SetLocalState(GetLocalState());
            }
            var remove = new List<long>();
            foreach (var (clientId, meta) in _meta)
            {
                if (clientId != ClientID && OutdatedTimeout <= now - meta.LastUpdated && _states.ContainsKey(clientId)) remove.Add(clientId);
            }
            if (remove.Count > 0) RemoveStates(remove, "timeout");
        }
    }

    /// <summary>Returns the state of the local client, or null if it is offline (after <see cref="SetLocalState"/> with null).</summary>
    public object? GetLocalState()
    {
        lock (_lock) return _states.TryGetValue(ClientID, out var s) ? s : null;
    }

    /// <summary>Snapshot of all known states by client id.</summary>
    public Dictionary<long, object?> GetStates()
    {
        lock (_lock) return new Dictionary<long, object?>(_states);
    }

    /// <summary>Snapshot of the clock/lastUpdated meta information by client id.</summary>
    public Dictionary<long, MetaClientState> GetMeta()
    {
        lock (_lock) return new Dictionary<long, MetaClientState>(_meta);
    }

    /// <summary>Sets the local state (null removes it). Emits <see cref="Change"/> / <see cref="Update"/> with origin "local".</summary>
    public void SetLocalState(object? state)
    {
        lock (_lock)
        {
            var clientID = ClientID;
            var clock = _meta.TryGetValue(clientID, out var currLocalMeta) ? currLocalMeta.Clock + 1 : 0;
            _states.TryGetValue(clientID, out var prevState);
            bool hadPrev = _states.ContainsKey(clientID);
            if (state == null) _states.Remove(clientID);
            else _states[clientID] = state;
            _meta[clientID] = new MetaClientState(clock, Now());
            var added = new List<long>();
            var updated = new List<long>();
            var filteredUpdated = new List<long>();
            var removed = new List<long>();
            if (state == null)
            {
                removed.Add(clientID);
            }
            else if (!hadPrev || prevState == null)
            {
                added.Add(clientID);
            }
            else
            {
                updated.Add(clientID);
                if (!JsonValues.DeepEquals(prevState, state)) filteredUpdated.Add(clientID);
            }
            if (added.Count > 0 || filteredUpdated.Count > 0 || removed.Count > 0)
            {
                Change?.Invoke(new AwarenessChange(added, filteredUpdated, removed), "local");
            }
            Update?.Invoke(new AwarenessChange(added, updated, removed), "local");
        }
    }

    /// <summary>Sets one field of the local state (no-op if there is no local state).</summary>
    public void SetLocalStateField(string field, object? value)
    {
        lock (_lock)
        {
            if (GetLocalState() is IDictionary<string, object?> state)
            {
                var copy = new Dictionary<string, object?>(state) { [field] = value };
                SetLocalState(copy);
            }
        }
    }

    /// <summary>Removes the states of the given clients (yjs removeAwarenessStates).</summary>
    public void RemoveStates(IReadOnlyList<long> clients, object? origin)
    {
        lock (_lock)
        {
            var removed = new List<long>();
            foreach (var clientID in clients)
            {
                if (_states.Remove(clientID))
                {
                    if (clientID == ClientID)
                    {
                        var curMeta = _meta[clientID];
                        _meta[clientID] = new MetaClientState(curMeta.Clock + 1, Now());
                    }
                    removed.Add(clientID);
                }
            }
            if (removed.Count > 0)
            {
                var change = new AwarenessChange(Array.Empty<long>(), Array.Empty<long>(), removed);
                Change?.Invoke(change, origin);
                Update?.Invoke(change, origin);
            }
        }
    }

    /// <summary>Encodes the states of the given clients (yjs encodeAwarenessUpdate). Unknown clients are encoded as removed (null).</summary>
    public byte[] EncodeUpdate(IReadOnlyList<long> clients, IReadOnlyDictionary<long, object?>? states = null)
    {
        lock (_lock)
        {
            var encoder = new Encoder();
            encoder.WriteVarUint(clients.Count);
            foreach (var clientID in clients)
            {
                object? state = null;
                if (states != null) states.TryGetValue(clientID, out state);
                else _states.TryGetValue(clientID, out state);
                var clock = _meta[clientID].Clock;
                encoder.WriteVarUint(clientID);
                encoder.WriteVarUint(clock);
                encoder.WriteVarString(JsonValues.Stringify(state));
            }
            return encoder.ToArray();
        }
    }

    /// <summary>Encodes the states of all known clients.</summary>
    public byte[] EncodeUpdate()
    {
        lock (_lock) return EncodeUpdate(_states.Keys.ToList());
    }

    /// <summary>Modify the content of an awareness update before re-encoding it (yjs modifyAwarenessUpdate).</summary>
    public static byte[] ModifyUpdate(byte[] update, Func<object?, object?> modify)
    {
        var decoder = new Decoder(update);
        var encoder = new Encoder();
        var len = decoder.ReadVarUint();
        encoder.WriteVarUint(len);
        for (long i = 0; i < len; i++)
        {
            var clientID = decoder.ReadVarUint();
            var clock = decoder.ReadVarUint();
            var state = JsonValues.ParseEncoded(decoder.ReadVarString());
            var modified = modify(state);
            encoder.WriteVarUint(clientID);
            encoder.WriteVarUint(clock);
            encoder.WriteVarString(JsonValues.Stringify(modified));
        }
        return encoder.ToArray();
    }

    /// <summary>Applies a received awareness update (yjs applyAwarenessUpdate).</summary>
    public void ApplyUpdate(byte[] update, object? origin)
    {
        var decoder = new Decoder(update);
        lock (_lock)
        {
            var timestamp = Now();
            var added = new List<long>();
            var updated = new List<long>();
            var filteredUpdated = new List<long>();
            var removed = new List<long>();
            var len = decoder.ReadVarUint();
            for (long i = 0; i < len; i++)
            {
                var clientID = decoder.ReadVarUint();
                var clock = decoder.ReadVarUint();
                var state = JsonValues.ParseEncoded(decoder.ReadVarString());
                var hasMeta = _meta.TryGetValue(clientID, out var clientMeta);
                _states.TryGetValue(clientID, out var prevState);
                var currClock = hasMeta ? clientMeta!.Clock : 0;
                if (currClock < clock || (currClock == clock && state == null && _states.ContainsKey(clientID)))
                {
                    if (state == null)
                    {
                        // never let a remote client remove this local state
                        if (clientID == ClientID && GetLocalState() != null)
                        {
                            // remote client removed the local state. Do not remove state. Broadcast a message indicating
                            // that this client still exists by increasing the clock
                            clock++;
                        }
                        else
                        {
                            _states.Remove(clientID);
                        }
                    }
                    else
                    {
                        _states[clientID] = state;
                    }
                    _meta[clientID] = new MetaClientState(clock, timestamp);
                    if (!hasMeta && state != null)
                    {
                        added.Add(clientID);
                    }
                    else if (hasMeta && state == null)
                    {
                        removed.Add(clientID);
                    }
                    else if (state != null)
                    {
                        if (!JsonValues.DeepEquals(state, prevState)) filteredUpdated.Add(clientID);
                        updated.Add(clientID);
                    }
                }
            }
            if (added.Count > 0 || filteredUpdated.Count > 0 || removed.Count > 0)
            {
                Change?.Invoke(new AwarenessChange(added, filteredUpdated, removed), origin);
            }
            if (added.Count > 0 || updated.Count > 0 || removed.Count > 0)
            {
                Update?.Invoke(new AwarenessChange(added, updated, removed), origin);
            }
        }
    }

    /// <summary>Marks the local client as offline, stops the outdated-state timer and emits <see cref="Destroyed"/>.</summary>
    public void Destroy()
    {
        lock (_lock)
        {
            if (_destroyed) return;
            Destroyed?.Invoke(this);
            SetLocalState(null);
            _destroyed = true;
            _checkTimer?.Dispose();
            Doc.Destroyed -= _docDestroyed;
            Change = null;
            Update = null;
            Destroyed = null;
        }
    }

    /// <summary>Same as <see cref="Destroy"/>.</summary>
    public void Dispose() => Destroy();
}
