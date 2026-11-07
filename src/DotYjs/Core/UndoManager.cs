namespace DotYjs;

/// <summary>One entry of the undo/redo stack.</summary>
public sealed class StackItem
{
    public StackItem(DeleteSet deletions, DeleteSet insertions)
    {
        Deletions = deletions;
        Insertions = insertions;
    }

    public DeleteSet Deletions { get; internal set; }
    public DeleteSet Insertions { get; internal set; }

    /// <summary>Use this to save and restore metadata like selection range.</summary>
    public Dictionary<object, object?> Meta { get; } = new();
}

public enum UndoRedoType
{
    Undo,
    Redo
}

public sealed class StackItemEvent
{
    public StackItemEvent(StackItem stackItem, object? origin, UndoRedoType type, OrderedMap<AbstractType, List<YEvent>> changedParentTypes)
    {
        StackItem = stackItem;
        Origin = origin;
        Type = type;
        ChangedParentTypes = changedParentTypes;
    }

    public StackItem StackItem { get; }
    public object? Origin { get; }
    public UndoRedoType Type { get; }
    public OrderedMap<AbstractType, List<YEvent>> ChangedParentTypes { get; }
}

public sealed class StackClearedEvent
{
    public StackClearedEvent(bool undoStackCleared, bool redoStackCleared)
    {
        UndoStackCleared = undoStackCleared;
        RedoStackCleared = redoStackCleared;
    }

    public bool UndoStackCleared { get; }
    public bool RedoStackCleared { get; }
}

public sealed class UndoManagerOptions
{
    /// <summary>Changes within this many milliseconds are merged into one stack item.</summary>
    public int CaptureTimeout { get; set; } = 500;

    /// <summary>Do not capture changes of a transaction if this returns false.</summary>
    public Func<Transaction, bool>? CaptureTransaction { get; set; }

    /// <summary>If this returns false for an item, the item won't be deleted by undo/redo even if it is in scope.</summary>
    public Func<Item, bool>? DeleteFilter { get; set; }

    /// <summary>
    /// Only transactions whose origin (or the origin's type) is in this set are tracked. Defaults to { null }.
    /// The UndoManager itself is always added.
    /// </summary>
    public HashSet<object?>? TrackedOrigins { get; set; }

    /// <summary>Experimental. By default the UndoManager never overwrites remote changes; enable this to overwrite remote key-value changes.</summary>
    public bool IgnoreRemoteMapChanges { get; set; }

    /// <summary>The document this UndoManager operates on. Only needed if the scope is empty.</summary>
    public Doc? Doc { get; set; }
}

/// <summary>
/// Port of yjs <c>UndoManager</c>. Fires <see cref="StackItemAdded"/> when a stack item was added to either stack
/// (store additional information via <see cref="StackItem.Meta"/>), <see cref="StackItemUpdated"/> when the last item
/// was extended within the capture timeout, and <see cref="StackItemPopped"/> after an undo/redo.
/// </summary>
public sealed class UndoManager : IDisposable
{
    private readonly Action<Transaction, Doc> _afterTransactionHandler;
    private readonly Action<Doc> _destroyHandler;
    private long _lastChange;

    public UndoManager(Doc doc, UndoManagerOptions? options = null) : this(new object[] { doc }, options) { }
    public UndoManager(AbstractType type, UndoManagerOptions? options = null) : this(new object[] { type }, options) { }
    public UndoManager(IEnumerable<AbstractType> types, UndoManagerOptions? options = null) : this(types.Cast<object>().ToArray(), options) { }

    /// <summary>Creates an undo manager for the given scope.</summary>
    /// <param name="typeScope">Limits the scope: a <see cref="Doc"/> (all changes) or one or more <see cref="AbstractType"/> (changes on the type or its children).</param>
    /// <param name="options">Options.</param>
    public UndoManager(IReadOnlyList<object> typeScope, UndoManagerOptions? options = null)
    {
        options ??= new UndoManagerOptions();
        Doc = options.Doc ?? (typeScope.Count > 0 ? typeScope[0] switch
        {
            DotYjs.Doc d => d,
            AbstractType t => t.Doc ?? throw new ArgumentException("Type is not integrated into a document", nameof(typeScope)),
            _ => throw new ArgumentException("Scope must contain Doc or AbstractType instances", nameof(typeScope))
        } : throw new ArgumentException("Scope is empty and no Doc was given", nameof(typeScope)));
        AddToScope(typeScope);
        DeleteFilter = options.DeleteFilter ?? (_ => true);
        TrackedOrigins = options.TrackedOrigins ?? new HashSet<object?> { null };
        TrackedOrigins.Add(this);
        CaptureTransaction = options.CaptureTransaction ?? (_ => true);
        IgnoreRemoteMapChanges = options.IgnoreRemoteMapChanges;
        CaptureTimeout = options.CaptureTimeout;

        _afterTransactionHandler = (transaction, _) => AfterTransaction(transaction);
        _destroyHandler = _ => Destroy();
        Doc.AfterTransaction += _afterTransactionHandler;
        Doc.Destroyed += _destroyHandler;
    }

    public Doc Doc { get; }
    public List<object> Scope { get; } = new();
    public Func<Item, bool> DeleteFilter { get; }
    public HashSet<object?> TrackedOrigins { get; }
    public Func<Transaction, bool> CaptureTransaction { get; }
    public List<StackItem> UndoStack { get; private set; } = new();
    public List<StackItem> RedoStack { get; private set; } = new();

    /// <summary>Whether the client is currently undoing.</summary>
    public bool Undoing { get; private set; }
    public bool Redoing { get; private set; }

    /// <summary>The currently popped stack item while undoing or redoing.</summary>
    public StackItem? CurrStackItem { get; private set; }
    public bool IgnoreRemoteMapChanges { get; }
    public int CaptureTimeout { get; set; }

    public event Action<StackItemEvent, UndoManager>? StackItemAdded;
    public event Action<StackItemEvent, UndoManager>? StackItemUpdated;
    public event Action<StackItemEvent, UndoManager>? StackItemPopped;
    public event Action<StackClearedEvent, UndoManager>? StackCleared;

    private static long Now() => Environment.TickCount64;

    private bool InScope(Transaction transaction, Item item)
    {
        foreach (var type in Scope)
        {
            if (ReferenceEquals(type, transaction.Doc) || (type is AbstractType t && Item.IsParentOf(t, item))) return true;
        }
        return false;
    }

    private void AfterTransaction(Transaction transaction)
    {
        // Only track certain transactions
        if (!CaptureTransaction(transaction)) return;
        // A transaction that changed nothing (e.g. the one created by Clear()) must not create or merge stack items,
        // otherwise a document-wide scope would capture an empty undo item and wipe the redo stack.
        if (transaction.ChangedParentTypes.Count == 0 && transaction.DeleteSet.Clients.Count == 0) return;
        bool scopeChanged = false;
        foreach (var type in Scope)
        {
            if (ReferenceEquals(type, Doc) || (type is AbstractType t && transaction.ChangedParentTypes.ContainsKey(t))) { scopeChanged = true; break; }
        }
        if (!scopeChanged) return;
        var origin = transaction.Origin;
        if (!TrackedOrigins.Contains(origin) && (origin == null || !TrackedOrigins.Contains(origin.GetType()))) return;

        var undoing = Undoing;
        var redoing = Redoing;
        var stack = undoing ? RedoStack : UndoStack;
        if (undoing)
        {
            StopCapturing(); // next undo should not be appended to last stack item
        }
        else if (!redoing)
        {
            // neither undoing nor redoing: delete redoStack
            Clear(false, true);
        }
        var insertions = new DeleteSet();
        foreach (var (client, endClock) in transaction.AfterState)
        {
            var startClock = transaction.BeforeState.TryGetValue(client, out var s) ? s : 0;
            var len = endClock - startClock;
            if (len > 0) insertions.Add(client, startClock, len);
        }
        var now = Now();
        bool didAdd = false;
        if (_lastChange > 0 && now - _lastChange < CaptureTimeout && stack.Count > 0 && !undoing && !redoing)
        {
            // append change to last stack op
            var lastOp = stack[^1];
            lastOp.Deletions = DeleteSet.Merge(new[] { lastOp.Deletions, transaction.DeleteSet });
            lastOp.Insertions = DeleteSet.Merge(new[] { lastOp.Insertions, insertions });
        }
        else
        {
            // create a new stack op
            stack.Add(new StackItem(transaction.DeleteSet, insertions));
            didAdd = true;
        }
        if (!undoing && !redoing) _lastChange = now;
        // make sure that deleted structs are not gc'd
        DeleteSet.IterateDeletedStructs(transaction, transaction.DeleteSet, s =>
        {
            if (s is Item item && InScope(transaction, item)) Item.KeepItem(item, true);
        });
        var changeEvent = new StackItemEvent(stack[^1], transaction.Origin, undoing ? UndoRedoType.Redo : UndoRedoType.Undo, transaction.ChangedParentTypes);
        if (didAdd) StackItemAdded?.Invoke(changeEvent, this);
        else StackItemUpdated?.Invoke(changeEvent, this);
    }

    /// <summary>Extend the scope.</summary>
    public void AddToScope(IEnumerable<object> ytypes)
    {
        foreach (var ytype in ytypes)
        {
            if (ytype is not AbstractType && ytype is not DotYjs.Doc) throw new ArgumentException("Scope must contain Doc or AbstractType instances", nameof(ytypes));
            if (Scope.Any(s => ReferenceEquals(s, ytype))) continue;
            var sameDoc = ytype is AbstractType t ? ReferenceEquals(t.Doc, Doc) : ReferenceEquals(ytype, Doc);
            if (!sameDoc) System.Diagnostics.Debug.WriteLine("[yjs#509] Not same Y.Doc");
            Scope.Add(ytype);
        }
    }

    public void AddToScope(AbstractType type) => AddToScope(new object[] { type });

    public void AddTrackedOrigin(object? origin) => TrackedOrigins.Add(origin);
    public void RemoveTrackedOrigin(object? origin) => TrackedOrigins.Remove(origin);

    public void Clear(bool clearUndoStack = true, bool clearRedoStack = true)
    {
        if ((clearUndoStack && CanUndo) || (clearRedoStack && CanRedo))
        {
            Doc.Transact(tr =>
            {
                if (clearUndoStack)
                {
                    foreach (var item in UndoStack) ClearStackItem(tr, item);
                    UndoStack = new List<StackItem>();
                }
                if (clearRedoStack)
                {
                    foreach (var item in RedoStack) ClearStackItem(tr, item);
                    RedoStack = new List<StackItem>();
                }
                StackCleared?.Invoke(new StackClearedEvent(clearUndoStack, clearRedoStack), this);
            });
        }
    }

    private void ClearStackItem(Transaction tr, StackItem stackItem)
    {
        DeleteSet.IterateDeletedStructs(tr, stackItem.Deletions, s =>
        {
            if (s is Item item && InScope(tr, item)) Item.KeepItem(item, false);
        });
    }

    /// <summary>
    /// UndoManager merges stack items created within <see cref="CaptureTimeout"/>. Call this so that the next
    /// stack item won't be merged with the previous one.
    /// </summary>
    public void StopCapturing() => _lastChange = 0;

    /// <summary>Undo last changes on type. Returns the StackItem if a change was applied.</summary>
    public StackItem? Undo()
    {
        Undoing = true;
        try
        {
            return PopStackItem(UndoStack, UndoRedoType.Undo);
        }
        finally
        {
            Undoing = false;
        }
    }

    /// <summary>Redo last undo operation. Returns the StackItem if a change was applied.</summary>
    public StackItem? Redo()
    {
        Redoing = true;
        try
        {
            return PopStackItem(RedoStack, UndoRedoType.Redo);
        }
        finally
        {
            Redoing = false;
        }
    }

    public bool CanUndo => UndoStack.Count > 0;
    public bool CanRedo => RedoStack.Count > 0;

    private StackItem? PopStackItem(List<StackItem> stack, UndoRedoType eventType)
    {
        Transaction? tr = null;
        var doc = Doc;
        Transaction.Transact(doc, transaction =>
        {
            while (stack.Count > 0 && CurrStackItem == null)
            {
                var store = doc.Store;
                var stackItem = stack[^1];
                stack.RemoveAt(stack.Count - 1);
                var itemsToRedo = new HashSet<Item>();
                var itemsToDelete = new List<Item>();
                bool performedChange = false;
                DeleteSet.IterateDeletedStructs(transaction, stackItem.Insertions, s =>
                {
                    if (s is Item it)
                    {
                        if (it.Redone != null)
                        {
                            var (item, diff) = Item.FollowRedone(store, it.Id);
                            if (diff > 0) item = store.GetItemCleanStart(transaction, new ID(item.Id.Client, item.Id.Clock + diff));
                            if (item is not Item followed) return;
                            it = followed;
                        }
                        if (!it.Deleted && InScope(transaction, it)) itemsToDelete.Add(it);
                    }
                });
                DeleteSet.IterateDeletedStructs(transaction, stackItem.Deletions, s =>
                {
                    if (s is Item it && InScope(transaction, it) &&
                        // Never redo structs in stackItem.insertions because they were created and deleted in the same capture interval.
                        !stackItem.Insertions.IsDeleted(it.Id))
                    {
                        itemsToRedo.Add(it);
                    }
                });
                foreach (var s in itemsToRedo.ToList())
                {
                    performedChange = Item.RedoItem(transaction, s, itemsToRedo, stackItem.Insertions, IgnoreRemoteMapChanges, this) != null || performedChange;
                }
                // We want to delete in reverse order so that children are deleted before
                // parents, so we have more information available when items are filtered.
                for (int i = itemsToDelete.Count - 1; i >= 0; i--)
                {
                    var item = itemsToDelete[i];
                    if (DeleteFilter(item))
                    {
                        item.Delete(transaction);
                        performedChange = true;
                    }
                }
                CurrStackItem = performedChange ? stackItem : null;
            }
            foreach (var (type, subProps) in transaction.Changed)
            {
                // destroy search marker if necessary
                if (subProps.Contains(null) && type.SearchMarker != null) type.SearchMarker.Clear();
            }
            tr = transaction;
        }, this);
        var res = CurrStackItem;
        if (res != null)
        {
            StackItemPopped?.Invoke(new StackItemEvent(res, this, eventType, tr!.ChangedParentTypes), this);
            CurrStackItem = null;
        }
        return res;
    }

    /// <summary>Stops tracking the document and unregisters all event handlers.</summary>
    public void Destroy()
    {
        TrackedOrigins.Remove(this);
        Doc.AfterTransaction -= _afterTransactionHandler;
        Doc.Destroyed -= _destroyHandler;
        StackItemAdded = null;
        StackItemUpdated = null;
        StackItemPopped = null;
        StackCleared = null;
    }

    public void Dispose() => Destroy();
}
