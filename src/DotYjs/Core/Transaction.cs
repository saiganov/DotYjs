namespace DotYjs;

/// <summary>
/// A transaction groups changes on a document. Observers are called and update events are emitted once the
/// outermost transaction finishes.
/// </summary>
public sealed class Transaction
{
    internal Transaction(Doc doc, object? origin, bool local)
    {
        Doc = doc;
        DeleteSet = new DeleteSet();
        BeforeState = doc.Store.GetStateVector();
        AfterState = new Dictionary<long, long>();
        Origin = origin;
        Local = local;
    }

    /// <summary>The document of this transaction.</summary>
    public Doc Doc { get; }

    /// <summary>Describes the set of deleted items by ids.</summary>
    public DeleteSet DeleteSet { get; }

    /// <summary>Holds the state before the transaction started.</summary>
    public Dictionary<long, long> BeforeState { get; }

    /// <summary>Holds the state after the transaction.</summary>
    public Dictionary<long, long> AfterState { get; internal set; }

    /// <summary>
    /// All types that were directly modified (property added or child inserted/deleted). The set contains the
    /// changed keys (<c>null</c> denotes a change in the list part).
    /// </summary>
    public OrderedMap<AbstractType, HashSet<string?>> Changed { get; } = new();

    /// <summary>Stores the events for the types that observe also child elements.</summary>
    public OrderedMap<AbstractType, List<YEvent>> ChangedParentTypes { get; } = new();

    internal List<AbstractStruct> MergeStructs { get; } = new();

    /// <summary>The origin passed to <see cref="Doc.Transact(Action{Transaction}, object?)"/> or to the function that applied an update.</summary>
    public object? Origin { get; }

    /// <summary>Stores meta information on the transaction.</summary>
    public Dictionary<object, object?> Meta { get; } = new();

    /// <summary>Whether this change originates from this doc.</summary>
    public bool Local { get; internal set; }

    /// <summary>Subdocuments added in this transaction.</summary>
    public HashSet<Doc> SubdocsAdded { get; } = new();
    /// <summary>Subdocuments removed in this transaction.</summary>
    public HashSet<Doc> SubdocsRemoved { get; } = new();
    /// <summary>Subdocuments that requested to be loaded in this transaction.</summary>
    public HashSet<Doc> SubdocsLoaded { get; } = new();

    internal bool NeedFormattingCleanup { get; set; }

    internal ID NextID() => new(Doc.ClientID, Doc.Store.GetState(Doc.ClientID));

    /// <summary>
    /// If <paramref name="type"/>.parent was added in current transaction, the type technically did not change,
    /// it was just added and we should not fire events for it.
    /// </summary>
    internal void AddChangedType(AbstractType type, string? parentSub)
    {
        var item = type.Item;
        if (item == null || (item.Id.Clock < BeforeState.GetValueOrDefault(item.Id.Client) && !item.Deleted))
        {
            if (!Changed.TryGetValue(type, out var set))
            {
                set = new HashSet<string?>();
                Changed[type] = set;
            }
            set.Add(parentSub);
        }
    }

    internal static bool WriteUpdateMessageFromTransaction(IUpdateEncoder encoder, Transaction transaction)
    {
        if (transaction.DeleteSet.Clients.Count == 0 && !transaction.AfterState.Any(kv => transaction.BeforeState.GetValueOrDefault(kv.Key, -1) != kv.Value))
        {
            return false;
        }
        transaction.DeleteSet.SortAndMerge();
        UpdateCodec.WriteClientsStructs(encoder, transaction.Doc.Store, transaction.BeforeState);
        DeleteSet.Write(encoder, transaction.DeleteSet);
        return true;
    }

    private static int TryToMergeWithLefts(List<AbstractStruct> structs, int pos)
    {
        var right = structs[pos];
        var left = structs[pos - 1];
        int i = pos;
        for (; i > 0; right = left, --i, left = i > 0 ? structs[i - 1] : left)
        {
            if (left.Deleted == right.Deleted && left.GetType() == right.GetType())
            {
                if (left.MergeWith(right))
                {
                    if (right is Item ri && ri.ParentSub != null && ri.Parent is AbstractType pt && pt.Map.TryGetValue(ri.ParentSub, out var cur) && cur == ri)
                    {
                        pt.Map[ri.ParentSub] = (Item)left;
                    }
                    continue;
                }
            }
            break;
        }
        int merged = pos - i;
        if (merged > 0)
        {
            // remove all merged structs from the array
            structs.RemoveRange(pos + 1 - merged, merged);
        }
        return merged;
    }

    private static void TryGcDeleteSet(DeleteSet ds, StructStore store, Func<Item, bool> gcFilter)
    {
        foreach (var (client, deleteItems) in ds.Clients)
        {
            var structs = store.Clients[client];
            for (int di = deleteItems.Count - 1; di >= 0; di--)
            {
                var deleteItem = deleteItems[di];
                long endDeleteItemClock = deleteItem.Clock + deleteItem.Len;
                for (int si = StructStore.FindIndexSS(structs, deleteItem.Clock); si < structs.Count && structs[si].Id.Clock < endDeleteItemClock; si++)
                {
                    var s = structs[si];
                    if (deleteItem.Clock + deleteItem.Len <= s.Id.Clock) break;
                    if (s is Item item && item.Deleted && !item.Keep && gcFilter(item))
                    {
                        item.Gc(store, false);
                    }
                }
            }
        }
    }

    private static void TryMergeDeleteSet(DeleteSet ds, StructStore store)
    {
        // try to merge deleted / gc'd items, merge from right to left (right is more specific)
        foreach (var (client, deleteItems) in ds.Clients)
        {
            var structs = store.Clients[client];
            for (int di = deleteItems.Count - 1; di >= 0; di--)
            {
                var deleteItem = deleteItems[di];
                // start with merging the item next to the last deleted item
                int mostRightIndexToCheck = Math.Min(structs.Count - 1, 1 + StructStore.FindIndexSS(structs, deleteItem.Clock + deleteItem.Len - 1));
                for (int si = mostRightIndexToCheck; si > 0 && structs[si].Id.Clock >= deleteItem.Clock;)
                {
                    si -= 1 + TryToMergeWithLefts(structs, si);
                }
            }
        }
    }

    internal static void TryGc(DeleteSet ds, StructStore store, Func<Item, bool> gcFilter)
    {
        TryGcDeleteSet(ds, store, gcFilter);
        TryMergeDeleteSet(ds, store);
    }

    private static void CleanupTransactions(List<Transaction> transactionCleanups, int i)
    {
        if (i >= transactionCleanups.Count) return;
        var transaction = transactionCleanups[i];
        var doc = transaction.Doc;
        var store = doc.Store;
        var ds = transaction.DeleteSet;
        var mergeStructs = transaction.MergeStructs;
        try
        {
            ds.SortAndMerge();
            transaction.AfterState = store.GetStateVector();
            doc.EmitBeforeObserverCalls(transaction);
            // observe events on changed types
            var fs = new List<Action>();
            foreach (var (itemtype, subs) in transaction.Changed.ToList())
            {
                fs.Add(() =>
                {
                    if (itemtype.Item == null || !itemtype.Item.Deleted)
                    {
                        itemtype.CallObserver(transaction, subs);
                    }
                });
            }
            fs.Add(() =>
            {
                // deep observe events
                foreach (var (type, evts) in transaction.ChangedParentTypes.ToList())
                {
                    // We need to think about the possibility that the user transforms the Y.Doc in the event.
                    if (type.DEH > 0 && (type.Item == null || !type.Item.Deleted))
                    {
                        var events = evts.Where(e => e.Target.Item == null || !e.Target.Item.Deleted).ToList();
                        foreach (var e in events)
                        {
                            e.CurrentTarget = type;
                            // path is relative to the current target
                            e.ResetPath();
                        }
                        // sort events by path length so that top-level events are fired first.
                        events = events.OrderBy(e => e.Path.Count).ToList();
                        // We don't need to check for events.length because we know that we at least have one event
                        type.CallDeepEventHandlers(events, transaction);
                    }
                }
            });
            fs.Add(() => doc.EmitAfterTransaction(transaction));
            CallAll(fs);
            if (transaction.NeedFormattingCleanup)
            {
                YText.CleanupYTextAfterTransaction(transaction);
            }
        }
        finally
        {
            // Replace deleted items with ItemDeleted / GC.
            // This is where content is actually remove from the Yjs Doc.
            if (doc.Gc) TryGcDeleteSet(ds, store, doc.GcFilter);
            TryMergeDeleteSet(ds, store);

            // on all affected store.clients props, try to merge
            foreach (var (client, clock) in transaction.AfterState)
            {
                long beforeClock = transaction.BeforeState.GetValueOrDefault(client);
                if (beforeClock != clock)
                {
                    var structs = store.Clients[client];
                    // we iterate from right to left so we can safely remove entries
                    int firstChangePos = Math.Max(StructStore.FindIndexSS(structs, beforeClock), 1);
                    for (int k = structs.Count - 1; k >= firstChangePos;)
                    {
                        k -= 1 + TryToMergeWithLefts(structs, k);
                    }
                }
            }
            // try to merge mergeStructs
            for (int k = mergeStructs.Count - 1; k >= 0; k--)
            {
                var id = mergeStructs[k].Id;
                var structs = store.Clients[id.Client];
                int replacedStructPos = StructStore.FindIndexSS(structs, id.Clock);
                if (replacedStructPos + 1 < structs.Count)
                {
                    if (TryToMergeWithLefts(structs, replacedStructPos + 1) > 1)
                    {
                        continue; // no need to perform next check, both are already merged
                    }
                }
                if (replacedStructPos > 0)
                {
                    TryToMergeWithLefts(structs, replacedStructPos);
                }
            }
            if (!transaction.Local && transaction.AfterState.GetValueOrDefault(doc.ClientID, -1) != transaction.BeforeState.GetValueOrDefault(doc.ClientID, -1))
            {
                // Changed the client-id because another client seems to be using it.
                doc.ClientID = Doc.GenerateNewClientId();
            }
            // @todo Merge all the transactions into one and provide send the data as a single update message
            doc.EmitAfterTransactionCleanup(transaction);
            if (doc.HasUpdateListeners)
            {
                var encoder = new UpdateEncoderV1();
                if (WriteUpdateMessageFromTransaction(encoder, transaction))
                {
                    doc.EmitUpdate(encoder.ToArray(), transaction);
                }
            }
            if (doc.HasUpdateV2Listeners)
            {
                var encoder = new UpdateEncoderV2();
                if (WriteUpdateMessageFromTransaction(encoder, transaction))
                {
                    doc.EmitUpdateV2(encoder.ToArray(), transaction);
                }
            }
            var subdocsAdded = transaction.SubdocsAdded;
            var subdocsLoaded = transaction.SubdocsLoaded;
            var subdocsRemoved = transaction.SubdocsRemoved;
            if (subdocsAdded.Count > 0 || subdocsRemoved.Count > 0 || subdocsLoaded.Count > 0)
            {
                foreach (var subdoc in subdocsAdded)
                {
                    subdoc.ClientID = doc.ClientID;
                    subdoc.CollectionId ??= doc.CollectionId;
                    doc.Subdocs.Add(subdoc);
                }
                foreach (var subdoc in subdocsRemoved) doc.Subdocs.Remove(subdoc);
                doc.EmitSubdocs(new SubdocsEventArgs(subdocsLoaded, subdocsAdded, subdocsRemoved), transaction);
                foreach (var subdoc in subdocsRemoved) subdoc.Destroy();
            }

            if (transactionCleanups.Count <= i + 1)
            {
                doc.TransactionCleanups = new List<Transaction>();
                doc.EmitAfterAllTransactions(transactionCleanups);
            }
            else
            {
                CleanupTransactions(transactionCleanups, i + 1);
            }
        }
    }

    /// <summary>Calls all functions; if one throws, the remaining ones are still called and the first error is rethrown.</summary>
    internal static void CallAll(IReadOnlyList<Action> fs, int i = 0)
    {
        try
        {
            for (; i < fs.Count; i++) fs[i]();
        }
        finally
        {
            if (i < fs.Count) CallAll(fs, i + 1);
        }
    }

    /// <summary>Implements the functionality of <c>y.transact(()=>{..})</c>.</summary>
    internal static T Transact<T>(Doc doc, Func<Transaction, T> f, object? origin = null, bool local = true)
    {
        var transactionCleanups = doc.TransactionCleanups;
        bool initialCall = false;
        T result;
        if (doc.CurrentTransaction == null)
        {
            initialCall = true;
            doc.CurrentTransaction = new Transaction(doc, origin, local);
            transactionCleanups.Add(doc.CurrentTransaction);
            if (transactionCleanups.Count == 1) doc.EmitBeforeAllTransactions();
            doc.EmitBeforeTransaction(doc.CurrentTransaction);
        }
        try
        {
            result = f(doc.CurrentTransaction);
        }
        finally
        {
            if (initialCall)
            {
                bool finishCleanup = doc.CurrentTransaction == transactionCleanups[0];
                doc.CurrentTransaction = null;
                if (finishCleanup)
                {
                    // The first transaction ended, now process observer calls.
                    // Observer call may create new transactions for which we need to call the observers and do cleanup.
                    // We don't want to nest these calls, so we execute these calls one after
                    // another.
                    // Also we need to ensure that all cleanups are called, even if the
                    // observes throw errors.
                    // This file is full of hacky try {} finally {} blocks to ensure that an
                    // event can throw errors and also that the cleanup is called.
                    CleanupTransactions(transactionCleanups, 0);
                }
            }
        }
        return result;
    }

    internal static void Transact(Doc doc, Action<Transaction> f, object? origin = null, bool local = true)
        => Transact<object?>(doc, t => { f(t); return null; }, origin, local);
}
