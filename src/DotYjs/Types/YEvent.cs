using DotYjs.Lib0;

namespace DotYjs;

public enum ChangeAction
{
    Add,
    Update,
    Delete,
}

/// <summary>Describes how a key of a map-like type changed.</summary>
public sealed class EntryChange
{
    public EntryChange(ChangeAction action, object? oldValue)
    {
        Action = action;
        OldValue = oldValue;
    }

    public ChangeAction Action { get; }

    /// <summary>The previous value (<see cref="Undefined.Value"/> for <see cref="ChangeAction.Add"/>).</summary>
    public object? OldValue { get; }
}

/// <summary>
/// A delta operation (Quill delta format): exactly one of <see cref="Insert"/>, <see cref="Retain"/> or
/// <see cref="Delete"/> is set.
/// </summary>
public sealed class DeltaOp
{
    /// <summary>Inserted content: a string (text), a List&lt;object?&gt; (arrays) or an embed / shared type (text).</summary>
    public object? Insert { get; set; }
    public long? Retain { get; set; }
    public long? Delete { get; set; }
    public Dictionary<string, object?>? Attributes { get; set; }

    public bool IsInsert => Retain == null && Delete == null;

    public static DeltaOp Inserting(object? insert, Dictionary<string, object?>? attributes = null) => new() { Insert = insert, Attributes = attributes };
    public static DeltaOp Retaining(long n, Dictionary<string, object?>? attributes = null) => new() { Retain = n, Attributes = attributes };
    public static DeltaOp Deleting(long n) => new() { Delete = n };

    /// <summary>Converts the op to a plain dictionary (handy for JSON serialization / comparisons).</summary>
    public Dictionary<string, object?> ToDictionary()
    {
        var d = new Dictionary<string, object?>();
        if (Retain != null) d["retain"] = Retain.Value;
        else if (Delete != null) d["delete"] = Delete.Value;
        else d["insert"] = Insert is AbstractType t ? t : Insert;
        if (Attributes != null) d["attributes"] = Attributes;
        return d;
    }

    public override string ToString() => JsonValues.Stringify(ToDictionary());
}

public sealed class YChanges
{
    public required HashSet<Item> Added { get; init; }
    public required HashSet<Item> Deleted { get; init; }
    public required List<DeltaOp> Delta { get; init; }
    public required Dictionary<string, EntryChange> Keys { get; init; }
}

/// <summary>YEvent describes the changes on a shared type.</summary>
public class YEvent
{
    private const string ErrorComputeChanges = "You must not compute changes after the event-handler fired.";

    private YChanges? _changes;
    private Dictionary<string, EntryChange>? _keys;
    private List<object>? _path;
    private protected List<DeltaOp>? DeltaCache;

    public YEvent(AbstractType target, Transaction transaction)
    {
        Target = target;
        CurrentTarget = target;
        Transaction = transaction;
    }

    /// <summary>The type on which this event was created.</summary>
    public AbstractType Target { get; }

    /// <summary>The current target on which the observe callback is called.</summary>
    public AbstractType CurrentTarget { get; internal set; }

    /// <summary>The transaction that triggered this event.</summary>
    public Transaction Transaction { get; }

    internal void ResetPath() => _path = null;

    /// <summary>
    /// Computes the path from <c>y</c> to the changed type: a list of string keys (maps) and long indices (arrays).
    /// </summary>
    public IReadOnlyList<object> Path => _path ??= GetPathTo(CurrentTarget, Target);

    /// <summary>Check if a struct is deleted by this event.</summary>
    public bool Deletes(AbstractStruct s) => Transaction.DeleteSet.IsDeleted(s.Id);

    /// <summary>Check if a struct is added by this event.</summary>
    public bool Adds(AbstractStruct s) => s.Id.Clock >= Transaction.BeforeState.GetValueOrDefault(s.Id.Client);

    public Dictionary<string, EntryChange> Keys
    {
        get
        {
            if (_keys == null)
            {
                if (Transaction.Doc.TransactionCleanups.Count == 0) throw new InvalidOperationException(ErrorComputeChanges);
                var keys = new Dictionary<string, EntryChange>();
                var target = Target;
                var changed = Transaction.Changed[target];
                foreach (var key in changed)
                {
                    if (key == null) continue;
                    var item = target.Map[key];
                    ChangeAction action;
                    object? oldValue;
                    if (Adds(item))
                    {
                        var prev = item.Left;
                        while (prev != null && Adds(prev)) prev = prev.Left;
                        if (Deletes(item))
                        {
                            if (prev != null && Deletes(prev))
                            {
                                action = ChangeAction.Delete;
                                oldValue = LastOrNull(prev.Content.GetContent());
                            }
                            else
                            {
                                continue;
                            }
                        }
                        else
                        {
                            if (prev != null && Deletes(prev))
                            {
                                action = ChangeAction.Update;
                                oldValue = LastOrNull(prev.Content.GetContent());
                            }
                            else
                            {
                                action = ChangeAction.Add;
                                oldValue = Undefined.Value;
                            }
                        }
                    }
                    else
                    {
                        if (Deletes(item))
                        {
                            action = ChangeAction.Delete;
                            oldValue = LastOrNull(item.Content.GetContent());
                        }
                        else
                        {
                            continue; // nop
                        }
                    }
                    keys[key] = new EntryChange(action, oldValue);
                }
                _keys = keys;
            }
            return _keys;
        }
    }

    /// <summary>The changes in delta format (lists only; YText overrides this with rich-text deltas).</summary>
    public virtual List<DeltaOp> Delta => Changes.Delta;

    /// <summary>Computes the added/deleted items, the delta and the key changes.</summary>
    public virtual YChanges Changes
    {
        get
        {
            if (_changes == null)
            {
                if (Transaction.Doc.TransactionCleanups.Count == 0) throw new InvalidOperationException(ErrorComputeChanges);
                var target = Target;
                var added = new HashSet<Item>();
                var deleted = new HashSet<Item>();
                var delta = new List<DeltaOp>();
                var changes = new YChanges { Added = added, Deleted = deleted, Delta = delta, Keys = Keys };
                var changed = Transaction.Changed[target];
                if (changed.Contains(null))
                {
                    DeltaOp? lastOp = null;
                    void PackOp()
                    {
                        if (lastOp != null) delta.Add(lastOp);
                    }
                    for (var item = target.Start; item != null; item = item.Right)
                    {
                        if (item.Deleted)
                        {
                            if (Deletes(item) && !Adds(item))
                            {
                                if (lastOp == null || lastOp.Delete == null)
                                {
                                    PackOp();
                                    lastOp = DeltaOp.Deleting(0);
                                }
                                lastOp.Delete += item.Length;
                                deleted.Add(item);
                            }
                        }
                        else
                        {
                            if (Adds(item))
                            {
                                if (lastOp == null || !lastOp.IsInsert)
                                {
                                    PackOp();
                                    lastOp = DeltaOp.Inserting(new List<object?>());
                                }
                                ((List<object?>)lastOp.Insert!).AddRange(item.Content.GetContent());
                                added.Add(item);
                            }
                            else
                            {
                                if (lastOp == null || lastOp.Retain == null)
                                {
                                    PackOp();
                                    lastOp = DeltaOp.Retaining(0);
                                }
                                lastOp.Retain += item.Length;
                            }
                        }
                    }
                    if (lastOp != null && lastOp.Retain == null) PackOp();
                }
                _changes = changes;
            }
            return _changes;
        }
    }

    private protected void SetChanges(YChanges changes) => _changes = changes;
    private protected bool HasChanges => _changes != null;

    private static object? LastOrNull(IReadOnlyList<object?> content) => content.Count > 0 ? content[content.Count - 1] : null;

    private static List<object> GetPathTo(AbstractType parent, AbstractType child)
    {
        var path = new List<object>();
        while (child.Item != null && child != parent)
        {
            if (child.Item.ParentSub != null)
            {
                // parent is map-ish
                path.Insert(0, child.Item.ParentSub);
            }
            else
            {
                // parent is array-ish
                long i = 0;
                var c = ((AbstractType)child.Item.Parent!).Start;
                while (c != child.Item && c != null)
                {
                    if (!c.Deleted && c.Countable) i += c.Length;
                    c = c.Right;
                }
                path.Insert(0, i);
            }
            child = (AbstractType)child.Item.Parent!;
        }
        return path;
    }
}
