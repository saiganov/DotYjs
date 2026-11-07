using System.Text;
using DotYjs.Lib0;

namespace DotYjs;

internal static class JsEquality
{
    private static bool IsNumber(object o) => o is int or long or short or sbyte or byte or ushort or uint or ulong or float or double or decimal;

    /// <summary>Approximates JavaScript's strict equality (===) for the DotYjs value model.</summary>
    public static bool StrictEquals(object? a, object? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        if (a is string sa && b is string sb) return sa == sb;
        if (a is bool ba && b is bool bb) return ba == bb;
        if (IsNumber(a) && IsNumber(b)) return Convert.ToDouble(a, System.Globalization.CultureInfo.InvariantCulture) == Convert.ToDouble(b, System.Globalization.CultureInfo.InvariantCulture);
        return false;
    }

    /// <summary>yjs equalAttrs: strict equality or flat equality of two objects.</summary>
    public static bool EqualAttrs(object? a, object? b)
    {
        if (StrictEquals(a, b)) return true;
        if (a is IDictionary<string, object?> da && b is IDictionary<string, object?> db)
        {
            if (da.Count != db.Count) return false;
            foreach (var (key, val) in da)
            {
                if (!db.TryGetValue(key, out var bv)) return false;
                if (!StrictEquals(bv, val)) return false;
            }
            return true;
        }
        return false;
    }
}

internal sealed class ItemTextListPosition
{
    public ItemTextListPosition(Item? left, Item? right, long index, OrderedMap<string, object?> currentAttributes)
    {
        Left = left;
        Right = right;
        Index = index;
        CurrentAttributes = currentAttributes;
    }

    public Item? Left;
    public Item? Right;
    public long Index;
    public OrderedMap<string, object?> CurrentAttributes;

    /// <summary>Only call this if you know that this.right is defined.</summary>
    public void Forward()
    {
        if (Right == null) throw new InvalidOperationException("Unexpected case");
        switch (Right.Content)
        {
            case ContentFormat cf:
                if (!Right.Deleted) YText.UpdateCurrentAttributes(CurrentAttributes, cf);
                break;
            default:
                if (!Right.Deleted) Index += Right.Length;
                break;
        }
        Left = Right;
        Right = Right.Right;
    }
}

public sealed class YTextEvent : YEvent
{
    public YTextEvent(YText ytext, Transaction transaction, HashSet<string?> subs) : base(ytext, transaction)
    {
        foreach (var sub in subs)
        {
            if (sub == null) ChildListChanged = true;
            else KeysChanged.Add(sub);
        }
    }

    /// <summary>Whether the children changed.</summary>
    public bool ChildListChanged { get; }

    /// <summary>Set of all changed attributes.</summary>
    public HashSet<string> KeysChanged { get; } = new();

    public override YChanges Changes
    {
        get
        {
            if (!HasChanges)
            {
                SetChanges(new YChanges { Keys = Keys, Delta = Delta, Added = new HashSet<Item>(), Deleted = new HashSet<Item>() });
            }
            return base.Changes;
        }
    }

    /// <summary>Compute the changes in the delta format (Quill rich-text delta).</summary>
    public override List<DeltaOp> Delta
    {
        get
        {
            if (DeltaCache == null)
            {
                var y = Target.Doc!;
                var delta = new List<DeltaOp>();
                Transaction.Transact(y, transaction =>
                {
                    var currentAttributes = new OrderedMap<string, object?>(); // saves all current attributes for insert
                    var oldAttributes = new OrderedMap<string, object?>();
                    var item = Target.Start;
                    string? action = null;
                    var attributes = new OrderedMap<string, object?>(); // counts added or removed new attributes for retain
                    object insert = "";
                    long retain = 0;
                    long deleteLen = 0;
                    void AddOp()
                    {
                        if (action != null)
                        {
                            DeltaOp? op = null;
                            switch (action)
                            {
                                case "delete":
                                    if (deleteLen > 0) op = DeltaOp.Deleting(deleteLen);
                                    deleteLen = 0;
                                    break;
                                case "insert":
                                    if (insert is not string s || s.Length > 0)
                                    {
                                        op = DeltaOp.Inserting(insert);
                                        if (currentAttributes.Count > 0)
                                        {
                                            op.Attributes = new Dictionary<string, object?>();
                                            foreach (var (key, value) in currentAttributes)
                                            {
                                                if (value != null) op.Attributes[key] = value;
                                            }
                                        }
                                    }
                                    insert = "";
                                    break;
                                case "retain":
                                    if (retain > 0)
                                    {
                                        op = DeltaOp.Retaining(retain);
                                        if (attributes.Count > 0)
                                        {
                                            op.Attributes = attributes.ToDictionary(kv => kv.Key, kv => kv.Value);
                                        }
                                    }
                                    retain = 0;
                                    break;
                            }
                            if (op != null) delta.Add(op);
                            action = null;
                        }
                    }
                    while (item != null)
                    {
                        switch (item.Content)
                        {
                            case ContentType:
                            case ContentEmbed:
                                if (Adds(item))
                                {
                                    if (!Deletes(item))
                                    {
                                        AddOp();
                                        action = "insert";
                                        insert = item.Content.GetContent()[0]!;
                                        AddOp();
                                    }
                                }
                                else if (Deletes(item))
                                {
                                    if (action != "delete")
                                    {
                                        AddOp();
                                        action = "delete";
                                    }
                                    deleteLen += 1;
                                }
                                else if (!item.Deleted)
                                {
                                    if (action != "retain")
                                    {
                                        AddOp();
                                        action = "retain";
                                    }
                                    retain += 1;
                                }
                                break;
                            case ContentString cs:
                                if (Adds(item))
                                {
                                    if (!Deletes(item))
                                    {
                                        if (action != "insert")
                                        {
                                            AddOp();
                                            action = "insert";
                                        }
                                        insert = (insert as string ?? "") + cs.Str;
                                    }
                                }
                                else if (Deletes(item))
                                {
                                    if (action != "delete")
                                    {
                                        AddOp();
                                        action = "delete";
                                    }
                                    deleteLen += item.Length;
                                }
                                else if (!item.Deleted)
                                {
                                    if (action != "retain")
                                    {
                                        AddOp();
                                        action = "retain";
                                    }
                                    retain += item.Length;
                                }
                                break;
                            case ContentFormat cf:
                                {
                                    var key = cf.Key;
                                    var value = cf.Value;
                                    if (Adds(item))
                                    {
                                        if (!Deletes(item))
                                        {
                                            var curVal = currentAttributes.TryGetValue(key, out var cv) ? cv : null;
                                            if (!JsEquality.EqualAttrs(curVal, value))
                                            {
                                                if (action == "retain") AddOp();
                                                var oldVal = oldAttributes.TryGetValue(key, out var ov) ? ov : null;
                                                if (JsEquality.EqualAttrs(value, oldVal)) attributes.Remove(key);
                                                else attributes[key] = value;
                                            }
                                            else if (value != null)
                                            {
                                                item.Delete(transaction);
                                            }
                                        }
                                    }
                                    else if (Deletes(item))
                                    {
                                        oldAttributes[key] = value;
                                        var curVal = currentAttributes.TryGetValue(key, out var cv) ? cv : null;
                                        if (!JsEquality.EqualAttrs(curVal, value))
                                        {
                                            if (action == "retain") AddOp();
                                            attributes[key] = curVal;
                                        }
                                    }
                                    else if (!item.Deleted)
                                    {
                                        oldAttributes[key] = value;
                                        if (attributes.TryGetValue(key, out var attr))
                                        {
                                            if (!JsEquality.EqualAttrs(attr, value))
                                            {
                                                if (action == "retain") AddOp();
                                                if (value == null) attributes.Remove(key);
                                                else attributes[key] = value;
                                            }
                                            else if (attr != null)
                                            {
                                                // this will be cleaned up automatically by the contextless cleanup function
                                                item.Delete(transaction);
                                            }
                                        }
                                    }
                                    if (!item.Deleted)
                                    {
                                        if (action == "insert") AddOp();
                                        YText.UpdateCurrentAttributes(currentAttributes, cf);
                                    }
                                    break;
                                }
                        }
                        item = item.Right;
                    }
                    AddOp();
                    while (delta.Count > 0)
                    {
                        var lastOp = delta[^1];
                        if (lastOp.Retain != null && lastOp.Attributes == null) delta.RemoveAt(delta.Count - 1);
                        else break;
                    }
                });
                DeltaCache = delta;
            }
            return DeltaCache;
        }
    }
}

/// <summary>Type that represents text with formatting information (rich text).</summary>
public class YText : AbstractType
{
    private List<Action>? _pending;

    public YText() : this(null) { }

    public YText(string? str)
    {
        _pending = str != null ? new List<Action> { () => Insert(0, str) } : new List<Action>();
        SearchMarker = new List<ArraySearchMarker>();
    }

    internal bool HasFormatting { get; set; }

    /// <summary>Number of characters (UTF-16 code units) of this text type.</summary>
    public int Length
    {
        get
        {
            EnsureIntegrated();
            return (int)LengthInternal;
        }
    }

    internal override void Integrate(Doc y, Item? item)
    {
        base.Integrate(y, item);
        try
        {
            foreach (var f in _pending!) f();
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e);
        }
        _pending = null;
    }

    internal override AbstractType InternalCopy() => new YText();

    public override AbstractType Clone()
    {
        var text = new YText();
        text.ApplyDelta(ToDelta());
        return text;
    }

    internal override void CallObserver(Transaction transaction, HashSet<string?> parentSubs)
    {
        base.CallObserver(transaction, parentSubs);
        var evt = new YTextEvent(this, transaction, parentSubs);
        CallTypeObservers(transaction, evt);
        // If a remote change happened, we try to cleanup potential formatting duplicates.
        if (!transaction.Local && HasFormatting) transaction.NeedFormattingCleanup = true;
    }

    /// <summary>Returns the unformatted string representation of this YText type.</summary>
    public override string ToString()
    {
        EnsureIntegrated();
        var sb = new StringBuilder();
        var n = Start;
        while (n != null)
        {
            if (!n.Deleted && n.Countable && n.Content is ContentString cs) sb.Append(cs.Str);
            n = n.Right;
        }
        return sb.ToString();
    }

    public override object? ToJSON() => ToString();

    /// <summary>Apply a Quill delta on this shared YText type.</summary>
    public void ApplyDelta(IReadOnlyList<DeltaOp> delta, bool sanitize = true)
    {
        if (Doc != null)
        {
            Transaction.Transact(Doc, transaction =>
            {
                var currPos = new ItemTextListPosition(null, Start, 0, new OrderedMap<string, object?>());
                for (int i = 0; i < delta.Count; i++)
                {
                    var op = delta[i];
                    if (op.Retain == null && op.Delete == null)
                    {
                        // Quill assumes that the content starts with an empty paragraph.
                        // Yjs/Y.Text assumes that it starts empty. We always hide that
                        // there is a newline at the end of the content.
                        // If we omit this step, clients will see a different number of
                        // paragraphs, but nothing bad will happen.
                        var ins = (!sanitize && op.Insert is string s && i == delta.Count - 1 && currPos.Right == null && s.EndsWith('\n')) ? s[..^1] : op.Insert;
                        if (ins is not string str || str.Length > 0)
                        {
                            InsertText(transaction, this, currPos, ins, ToOrdered(op.Attributes));
                        }
                    }
                    else if (op.Retain != null)
                    {
                        FormatText(transaction, this, currPos, op.Retain.Value, ToOrdered(op.Attributes));
                    }
                    else if (op.Delete != null)
                    {
                        DeleteText(transaction, currPos, op.Delete.Value);
                    }
                }
            });
        }
        else
        {
            _pending!.Add(() => ApplyDelta(delta, sanitize));
        }
    }

    private static OrderedMap<string, object?> ToOrdered(IEnumerable<KeyValuePair<string, object?>>? attrs)
    {
        var m = new OrderedMap<string, object?>();
        if (attrs != null)
        {
            foreach (var (k, v) in attrs) m[k] = v;
        }
        return m;
    }

    /// <summary>
    /// Returns the delta representation of this YText type. Optionally relative to snapshots (to render changes).
    /// </summary>
    public List<DeltaOp> ToDelta(Snapshot? snapshot = null, Snapshot? prevSnapshot = null, Func<string, ID, object?>? computeYChange = null)
    {
        EnsureIntegrated();
        var ops = new List<DeltaOp>();
        var currentAttributes = new OrderedMap<string, object?>();
        var doc = Doc!;
        var str = new StringBuilder();
        var n = Start;
        void PackStr()
        {
            if (str.Length > 0)
            {
                // pack str with attributes to ops
                var op = DeltaOp.Inserting(str.ToString());
                if (currentAttributes.Count > 0)
                {
                    op.Attributes = currentAttributes.ToDictionary(kv => kv.Key, kv => kv.Value);
                }
                ops.Add(op);
                str.Clear();
            }
        }
        void ComputeDelta()
        {
            while (n != null)
            {
                if (Snapshot.IsVisible(n, snapshot) || (prevSnapshot != null && Snapshot.IsVisible(n, prevSnapshot)))
                {
                    switch (n.Content)
                    {
                        case ContentString cs:
                            {
                                var cur = currentAttributes.TryGetValue("ychange", out var c) ? c as IDictionary<string, object?> : null;
                                if (snapshot != null && !Snapshot.IsVisible(n, snapshot))
                                {
                                    if (cur == null || !JsEquality.StrictEquals((cur.TryGetValue("user", out var cu) ? cu : null), n.Id.Client) || !Equals((cur.TryGetValue("type", out var cty) ? cty : null), "removed"))
                                    {
                                        PackStr();
                                        currentAttributes["ychange"] = computeYChange != null ? computeYChange("removed", n.Id) : new Dictionary<string, object?> { ["type"] = "removed" };
                                    }
                                }
                                else if (prevSnapshot != null && !Snapshot.IsVisible(n, prevSnapshot))
                                {
                                    if (cur == null || !JsEquality.StrictEquals((cur.TryGetValue("user", out var cu) ? cu : null), n.Id.Client) || !Equals((cur.TryGetValue("type", out var cty) ? cty : null), "added"))
                                    {
                                        PackStr();
                                        currentAttributes["ychange"] = computeYChange != null ? computeYChange("added", n.Id) : new Dictionary<string, object?> { ["type"] = "added" };
                                    }
                                }
                                else if (currentAttributes.ContainsKey("ychange"))
                                {
                                    PackStr();
                                    currentAttributes.Remove("ychange");
                                }
                                str.Append(cs.Str);
                                break;
                            }
                        case ContentType:
                        case ContentEmbed:
                            {
                                PackStr();
                                var op = DeltaOp.Inserting(n.Content.GetContent()[0]);
                                if (currentAttributes.Count > 0)
                                {
                                    op.Attributes = currentAttributes.ToDictionary(kv => kv.Key, kv => kv.Value);
                                }
                                ops.Add(op);
                                break;
                            }
                        case ContentFormat cf:
                            if (Snapshot.IsVisible(n, snapshot))
                            {
                                PackStr();
                                UpdateCurrentAttributes(currentAttributes, cf);
                            }
                            break;
                    }
                }
                n = n.Right;
            }
            PackStr();
        }
        if (snapshot != null || prevSnapshot != null)
        {
            // snapshots are merged again after the transaction, so we need to keep the
            // transaction alive until we are done
            Transaction.Transact(doc, transaction =>
            {
                if (snapshot != null) Snapshot.SplitSnapshotAffectedStructs(transaction, snapshot);
                if (prevSnapshot != null) Snapshot.SplitSnapshotAffectedStructs(transaction, prevSnapshot);
                ComputeDelta();
            }, "cleanup");
        }
        else
        {
            ComputeDelta();
        }
        return ops;
    }

    /// <summary>
    /// Insert text at a given index. If <paramref name="attributes"/> is null, the text inherits the formatting
    /// at the insert position.
    /// </summary>
    public void Insert(int index, string text, IDictionary<string, object?>? attributes = null)
    {
        if (text.Length <= 0) return;
        var y = Doc;
        if (y != null)
        {
            Transaction.Transact(y, transaction =>
            {
                var pos = FindPosition(transaction, this, index, attributes == null);
                OrderedMap<string, object?> attrs;
                if (attributes == null)
                {
                    attrs = new OrderedMap<string, object?>();
                    foreach (var (k, v) in pos.CurrentAttributes) attrs[k] = v;
                }
                else
                {
                    attrs = ToOrdered(attributes);
                }
                InsertText(transaction, this, pos, text, attrs);
            });
        }
        else
        {
            _pending!.Add(() => Insert(index, text, attributes));
        }
    }

    /// <summary>Inserts an embed at an index. The embed can be a JSON-like value or a shared type.</summary>
    public void InsertEmbed(int index, object embed, IDictionary<string, object?>? attributes = null)
    {
        var y = Doc;
        if (y != null)
        {
            Transaction.Transact(y, transaction =>
            {
                var pos = FindPosition(transaction, this, index, attributes == null);
                InsertText(transaction, this, pos, embed, ToOrdered(attributes));
            });
        }
        else
        {
            _pending!.Add(() => InsertEmbed(index, embed, attributes));
        }
    }

    /// <summary>Deletes text starting from an index.</summary>
    public void Delete(int index, int length)
    {
        if (length == 0) return;
        var y = Doc;
        if (y != null)
        {
            Transaction.Transact(y, transaction => DeleteText(transaction, FindPosition(transaction, this, index, true), length));
        }
        else
        {
            _pending!.Add(() => Delete(index, length));
        }
    }

    /// <summary>Assigns properties to a range of text.</summary>
    public void Format(int index, int length, IDictionary<string, object?> attributes)
    {
        if (length == 0) return;
        var y = Doc;
        if (y != null)
        {
            Transaction.Transact(y, transaction =>
            {
                var pos = FindPosition(transaction, this, index, false);
                if (pos.Right == null) return;
                FormatText(transaction, this, pos, length, ToOrdered(attributes));
            });
        }
        else
        {
            _pending!.Add(() => Format(index, length, attributes));
        }
    }

    /// <summary>Removes an attribute (of the text type itself, not of the text content).</summary>
    public void RemoveAttribute(string attributeName)
    {
        if (Doc != null) Transaction.Transact(Doc, t => TypeMapDelete(t, this, attributeName));
        else _pending!.Add(() => RemoveAttribute(attributeName));
    }

    /// <summary>Sets an attribute (of the text type itself, not of the text content).</summary>
    public void SetAttribute(string attributeName, object? attributeValue)
    {
        if (Doc != null) Transaction.Transact(Doc, t => TypeMapSet(t, this, attributeName, attributeValue));
        else _pending!.Add(() => SetAttribute(attributeName, attributeValue));
    }

    public object? GetAttribute(string attributeName)
    {
        var v = TypeMapGet(this, attributeName);
        return v is Undefined ? null : v;
    }

    public Dictionary<string, object?> GetAttributes() => TypeMapGetAll(this);

    internal override void WriteType(IUpdateEncoder encoder) => encoder.WriteTypeRef(ContentType.YTextRefID);

    // ------------------------------------------------------------------
    // text algorithms
    // ------------------------------------------------------------------

    internal static void UpdateCurrentAttributes(OrderedMap<string, object?> currentAttributes, ContentFormat format)
    {
        if (format.Value == null) currentAttributes.Remove(format.Key);
        else currentAttributes[format.Key] = format.Value;
    }

    private static ItemTextListPosition FindNextPosition(Transaction transaction, ItemTextListPosition pos, long count)
    {
        while (pos.Right != null && count > 0)
        {
            switch (pos.Right.Content)
            {
                case ContentFormat cf:
                    if (!pos.Right.Deleted) UpdateCurrentAttributes(pos.CurrentAttributes, cf);
                    break;
                default:
                    if (!pos.Right.Deleted)
                    {
                        if (count < pos.Right.Length)
                        {
                            // split right
                            transaction.Doc.Store.GetItemCleanStart(transaction, new ID(pos.Right.Id.Client, pos.Right.Id.Clock + count));
                        }
                        pos.Index += pos.Right.Length;
                        count -= pos.Right.Length;
                    }
                    break;
            }
            pos.Left = pos.Right;
            pos.Right = pos.Right.Right;
            // pos.forward() - we don't forward because that would halve the performance because we already do the checks above
        }
        return pos;
    }

    private static ItemTextListPosition FindPosition(Transaction transaction, AbstractType parent, long index, bool useSearchMarker)
    {
        var currentAttributes = new OrderedMap<string, object?>();
        var marker = useSearchMarker ? FindMarker(parent, index) : null;
        if (marker != null)
        {
            var pos = new ItemTextListPosition(marker.P.Left, marker.P, marker.Index, currentAttributes);
            return FindNextPosition(transaction, pos, index - marker.Index);
        }
        else
        {
            var pos = new ItemTextListPosition(null, parent.Start, 0, currentAttributes);
            return FindNextPosition(transaction, pos, index);
        }
    }

    /// <summary>Negate applied formats.</summary>
    private static void InsertNegatedAttributes(Transaction transaction, AbstractType parent, ItemTextListPosition currPos, OrderedMap<string, object?> negatedAttributes)
    {
        // check if we really need to remove attributes
        while (currPos.Right != null && (
            currPos.Right.Deleted || (
                currPos.Right.Content is ContentFormat cf &&
                JsEquality.EqualAttrs(negatedAttributes.TryGetValue(cf.Key, out var nv) ? nv : Undefined.Value, cf.Value))))
        {
            if (!currPos.Right.Deleted)
            {
                negatedAttributes.Remove(((ContentFormat)currPos.Right.Content).Key);
            }
            currPos.Forward();
        }
        var doc = transaction.Doc;
        var ownClientId = doc.ClientID;
        foreach (var (key, val) in negatedAttributes.ToList())
        {
            var left = currPos.Left;
            var right = currPos.Right;
            var nextFormat = new Item(new ID(ownClientId, doc.Store.GetState(ownClientId)), left, left?.LastId, right, right?.Id, parent, null, new ContentFormat(key, val));
            nextFormat.Integrate(transaction, 0);
            currPos.Right = nextFormat;
            currPos.Forward();
        }
    }

    private static void MinimizeAttributeChanges(ItemTextListPosition currPos, OrderedMap<string, object?> attributes)
    {
        // go right while attributes[right.key] === right.value (or right is deleted)
        while (true)
        {
            if (currPos.Right == null)
            {
                break;
            }
            else if (currPos.Right.Deleted || (currPos.Right.Content is ContentFormat cf && JsEquality.EqualAttrs(attributes.TryGetValue(cf.Key, out var av) ? av : null, cf.Value)))
            {
                // ok, continue
            }
            else
            {
                break;
            }
            currPos.Forward();
        }
    }

    private static OrderedMap<string, object?> InsertAttributes(Transaction transaction, AbstractType parent, ItemTextListPosition currPos, OrderedMap<string, object?> attributes)
    {
        var doc = transaction.Doc;
        var ownClientId = doc.ClientID;
        var negatedAttributes = new OrderedMap<string, object?>();
        // insert format-start items
        foreach (var (key, val) in attributes.ToList())
        {
            var currentVal = currPos.CurrentAttributes.TryGetValue(key, out var cv) ? cv : null;
            if (!JsEquality.EqualAttrs(currentVal, val))
            {
                // save negated attribute (set null if currentVal undefined)
                negatedAttributes[key] = currentVal;
                var left = currPos.Left;
                var right = currPos.Right;
                currPos.Right = new Item(new ID(ownClientId, doc.Store.GetState(ownClientId)), left, left?.LastId, right, right?.Id, parent, null, new ContentFormat(key, val));
                currPos.Right.Integrate(transaction, 0);
                currPos.Forward();
            }
        }
        return negatedAttributes;
    }

    private static void InsertText(Transaction transaction, AbstractType parent, ItemTextListPosition currPos, object? text, OrderedMap<string, object?> attributes)
    {
        foreach (var (key, _) in currPos.CurrentAttributes.ToList())
        {
            if (!attributes.ContainsKey(key)) attributes[key] = null;
        }
        var doc = transaction.Doc;
        var ownClientId = doc.ClientID;
        MinimizeAttributeChanges(currPos, attributes);
        var negatedAttributes = InsertAttributes(transaction, parent, currPos, attributes);
        // insert content
        AbstractContent content = text switch
        {
            string s => new ContentString(s),
            AbstractType t => new ContentType(t),
            _ => new ContentEmbed(text),
        };
        var left = currPos.Left;
        var right = currPos.Right;
        var index = currPos.Index;
        if (parent.SearchMarker != null) UpdateMarkerChanges(parent.SearchMarker, currPos.Index, content.GetLength());
        right = new Item(new ID(ownClientId, doc.Store.GetState(ownClientId)), left, left?.LastId, right, right?.Id, parent, null, content);
        right.Integrate(transaction, 0);
        currPos.Right = right;
        currPos.Index = index;
        currPos.Forward();
        InsertNegatedAttributes(transaction, parent, currPos, negatedAttributes);
    }

    private static void FormatText(Transaction transaction, AbstractType parent, ItemTextListPosition currPos, long length, OrderedMap<string, object?> attributes)
    {
        var doc = transaction.Doc;
        var ownClientId = doc.ClientID;
        MinimizeAttributeChanges(currPos, attributes);
        var negatedAttributes = InsertAttributes(transaction, parent, currPos, attributes);
        // iterate until first non-format or null is found
        // delete all formats with attributes[format.key] != null
        // also check the attributes after the first non-format as we do not want to insert redundant negated attributes there
        while (currPos.Right != null && (length > 0 || (negatedAttributes.Count > 0 && (currPos.Right.Deleted || currPos.Right.Content is ContentFormat))))
        {
            if (!currPos.Right.Deleted)
            {
                bool breakLoop = false;
                switch (currPos.Right.Content)
                {
                    case ContentFormat cf:
                        {
                            var key = cf.Key;
                            var value = cf.Value;
                            if (attributes.TryGetValue(key, out var attr))
                            {
                                if (JsEquality.EqualAttrs(attr, value))
                                {
                                    negatedAttributes.Remove(key);
                                }
                                else
                                {
                                    if (length == 0)
                                    {
                                        // no need to further extend negatedAttributes
                                        breakLoop = true;
                                        break;
                                    }
                                    negatedAttributes[key] = value;
                                }
                                currPos.Right.Delete(transaction);
                            }
                            else
                            {
                                currPos.CurrentAttributes[key] = value;
                            }
                            break;
                        }
                    default:
                        if (length < currPos.Right.Length)
                        {
                            transaction.Doc.Store.GetItemCleanStart(transaction, new ID(currPos.Right.Id.Client, currPos.Right.Id.Clock + length));
                        }
                        length -= currPos.Right.Length;
                        break;
                }
                if (breakLoop) break;
            }
            currPos.Forward();
        }
        // Quill just assumes that the editor starts with a newline and that it always
        // ends with a newline. We only insert that newline when a new newline is
        // inserted - i.e when length is bigger than type.length
        if (length > 0)
        {
            var newlines = new string('\n', (int)length);
            currPos.Right = new Item(new ID(ownClientId, doc.Store.GetState(ownClientId)), currPos.Left, currPos.Left?.LastId, currPos.Right, currPos.Right?.Id, parent, null, new ContentString(newlines));
            currPos.Right.Integrate(transaction, 0);
            currPos.Forward();
        }
        InsertNegatedAttributes(transaction, parent, currPos, negatedAttributes);
    }

    /// <summary>
    /// Call this function after string content has been deleted in order to clean up formatting Items.
    /// </summary>
    private static int CleanupFormattingGap(Transaction transaction, Item start, Item? curr, OrderedMap<string, object?> startAttributes, OrderedMap<string, object?> currAttributes)
    {
        Item? end = start;
        var endFormats = new Dictionary<string, ContentFormat>();
        while (end != null && (!end.Countable || end.Deleted))
        {
            if (!end.Deleted && end.Content is ContentFormat cf) endFormats[cf.Key] = cf;
            end = end.Right;
        }
        int cleanups = 0;
        bool reachedCurr = false;
        Item? s = start;
        while (s != end)
        {
            if (curr == s) reachedCurr = true;
            if (!s!.Deleted)
            {
                if (s.Content is ContentFormat content)
                {
                    var key = content.Key;
                    var value = content.Value;
                    var startAttrValue = startAttributes.TryGetValue(key, out var sv) ? sv : null;
                    if (!ReferenceEquals(endFormats.GetValueOrDefault(key), content) || JsEquality.StrictEquals(startAttrValue, value))
                    {
                        // Either this format is overwritten or it is not necessary because the attribute already existed.
                        s.Delete(transaction);
                        cleanups++;
                        var currVal = currAttributes.TryGetValue(key, out var cuv) ? cuv : null;
                        if (!reachedCurr && JsEquality.StrictEquals(currVal, value) && !JsEquality.StrictEquals(startAttrValue, value))
                        {
                            if (startAttrValue == null) currAttributes.Remove(key);
                            else currAttributes[key] = startAttrValue;
                        }
                    }
                    if (!reachedCurr && !s.Deleted)
                    {
                        UpdateCurrentAttributes(currAttributes, content);
                    }
                }
            }
            s = s.Right;
        }
        return cleanups;
    }

    private static void CleanupContextlessFormattingGap(Transaction transaction, Item? item)
    {
        // iterate until item.right is null or content
        while (item != null && item.Right != null && (item.Right.Deleted || !item.Right.Countable))
        {
            item = item.Right;
        }
        var attrs = new HashSet<string>();
        // iterate back until a content item is found
        while (item != null && (item.Deleted || !item.Countable))
        {
            if (!item.Deleted && item.Content is ContentFormat cf)
            {
                var key = cf.Key;
                if (!attrs.Add(key)) item.Delete(transaction);
            }
            item = item.Left;
        }
    }

    /// <summary>
    /// This function is experimental and subject to change / be removed.
    /// Ideally, we don't need this function at all. Formatting attributes should be cleaned up
    /// automatically after each change. This function iterates twice over the complete YText type
    /// and removes unnecessary formatting attributes.
    /// </summary>
    public static int CleanupYTextFormatting(YText type)
    {
        int res = 0;
        Transaction.Transact(type.Doc!, transaction =>
        {
            var start = type.Start!;
            var end = type.Start;
            var startAttributes = new OrderedMap<string, object?>();
            var currentAttributes = new OrderedMap<string, object?>();
            while (end != null)
            {
                if (!end.Deleted)
                {
                    switch (end.Content)
                    {
                        case ContentFormat cf:
                            UpdateCurrentAttributes(currentAttributes, cf);
                            break;
                        default:
                            res += CleanupFormattingGap(transaction, start, end, startAttributes, currentAttributes);
                            startAttributes = Copy(currentAttributes);
                            start = end;
                            break;
                    }
                }
                end = end.Right;
            }
        });
        return res;
    }

    private static OrderedMap<string, object?> Copy(OrderedMap<string, object?> m)
    {
        var r = new OrderedMap<string, object?>();
        foreach (var (k, v) in m) r[k] = v;
        return r;
    }

    /// <summary>
    /// This will be called by the transaction once the event handlers are called to potentially cleanup
    /// formatting attributes.
    /// </summary>
    internal static void CleanupYTextAfterTransaction(Transaction transaction)
    {
        var needFullCleanup = new HashSet<YText>();
        // check if another formatting item was inserted
        var doc = transaction.Doc;
        foreach (var (client, afterClock) in transaction.AfterState)
        {
            long clock = transaction.BeforeState.GetValueOrDefault(client);
            if (afterClock == clock) continue;
            StructStore.IterateStructs(transaction, doc.Store.Clients[client], clock, afterClock, item =>
            {
                if (!item.Deleted && item is Item it && it.Content is ContentFormat && it.Parent is YText yt)
                {
                    needFullCleanup.Add(yt);
                }
            });
        }
        // cleanup in a new transaction
        Transaction.Transact(doc, t =>
        {
            DeleteSet.IterateDeletedStructs(transaction, transaction.DeleteSet, item =>
            {
                if (item is GC || item is not Item it || it.Parent is not YText parent || !parent.HasFormatting || needFullCleanup.Contains(parent))
                {
                    return;
                }
                if (it.Content is ContentFormat) needFullCleanup.Add(parent);
                else CleanupContextlessFormattingGap(t, it);
            });
            // If a formatting item was inserted, we simply clean the whole type.
            // We need to compute currentAttributes for the current position anyway.
            foreach (var yText in needFullCleanup) CleanupYTextFormatting(yText);
        });
    }

    private static ItemTextListPosition DeleteText(Transaction transaction, ItemTextListPosition currPos, long length)
    {
        long startLength = length;
        var startAttrs = Copy(currPos.CurrentAttributes);
        var start = currPos.Right;
        while (length > 0 && currPos.Right != null)
        {
            if (!currPos.Right.Deleted)
            {
                switch (currPos.Right.Content)
                {
                    case ContentType:
                    case ContentEmbed:
                    case ContentString:
                        if (length < currPos.Right.Length)
                        {
                            transaction.Doc.Store.GetItemCleanStart(transaction, new ID(currPos.Right.Id.Client, currPos.Right.Id.Clock + length));
                        }
                        length -= currPos.Right.Length;
                        currPos.Right.Delete(transaction);
                        break;
                }
            }
            currPos.Forward();
        }
        if (start != null)
        {
            CleanupFormattingGap(transaction, start, currPos.Right, startAttrs, currPos.CurrentAttributes);
        }
        var parent = (AbstractType)(currPos.Left ?? currPos.Right)!.Parent!;
        if (parent.SearchMarker != null)
        {
            UpdateMarkerChanges(parent.SearchMarker, currPos.Index, -startLength + length);
        }
        return currPos;
    }
}
