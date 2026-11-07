using DotYjs.Lib0;

namespace DotYjs;

internal sealed class ArraySearchMarker
{
    private static long _globalSearchMarkerTimestamp;

    public ArraySearchMarker(Item p, long index)
    {
        p.Marker = true;
        P = p;
        Index = index;
        Timestamp = _globalSearchMarkerTimestamp++;
    }

    public Item P { get; set; }
    public long Index { get; set; }
    public long Timestamp { get; set; }

    public void RefreshTimestamp() => Timestamp = _globalSearchMarkerTimestamp++;

    public void Overwrite(Item p, long index)
    {
        P.Marker = false;
        P = p;
        p.Marker = true;
        Index = index;
        Timestamp = _globalSearchMarkerTimestamp++;
    }
}

/// <summary>
/// Base class of all shared types. An instance of this class itself is used as a placeholder for root types
/// whose concrete type is not known yet.
/// </summary>
public class AbstractType
{
    private const int MaxSearchMarker = 80;

    private readonly List<Action<YEvent, Transaction>> _eH = new();
    private readonly List<Action<IReadOnlyList<YEvent>, Transaction>> _dEH = new();

    /// <summary>The item that holds this type (null for root types).</summary>
    public Item? Item { get; internal set; }

    internal Dictionary<string, Item> Map { get; set; } = new();
    internal Item? Start { get; set; }

    /// <summary>The document this type is bound to (null for prelim types).</summary>
    public Doc? Doc { get; internal set; }

    internal long LengthInternal { get; set; }

    internal List<ArraySearchMarker>? SearchMarker { get; set; }

    internal int DEH => _dEH.Count;

    /// <summary>Returns the parent type, or null for root types.</summary>
    public AbstractType? Parent => Item?.Parent as AbstractType;

    /// <summary>
    /// Integrate this type into the Yjs instance: save this struct in the os, this type now exists on the doc.
    /// </summary>
    internal virtual void Integrate(Doc y, Item? item)
    {
        Doc = y;
        Item = item;
    }

    internal virtual AbstractType InternalCopy() => new AbstractType();

    /// <summary>Makes a copy of this data type that can be included somewhere else.</summary>
    public virtual AbstractType Clone() => throw new NotSupportedException();

    internal virtual void WriteType(IUpdateEncoder encoder) { }

    /// <summary>The first non-deleted item.</summary>
    internal Item? First
    {
        get
        {
            var n = Start;
            while (n != null && n.Deleted) n = n.Right;
            return n;
        }
    }

    /// <summary>Creates YEvent and calls all type observers. Must be implemented by each type.</summary>
    internal virtual void CallObserver(Transaction transaction, HashSet<string?> parentSubs)
    {
        if (!transaction.Local && SearchMarker != null) SearchMarker.Clear();
    }

    /// <summary>Observe all events that are created on this type.</summary>
    public void Observe(Action<YEvent, Transaction> f) => _eH.Add(f);

    /// <summary>Observe all events that are created by this type and its children.</summary>
    public void ObserveDeep(Action<IReadOnlyList<YEvent>, Transaction> f) => _dEH.Add(f);

    /// <summary>Removes an observer added with <see cref="Observe"/>.</summary>
    public void Unobserve(Action<YEvent, Transaction> f) => _eH.Remove(f);

    /// <summary>Removes an observer added with <see cref="ObserveDeep"/>.</summary>
    public void UnobserveDeep(Action<IReadOnlyList<YEvent>, Transaction> f) => _dEH.Remove(f);

    internal void CallDeepEventHandlers(IReadOnlyList<YEvent> events, Transaction transaction)
    {
        var fs = _dEH.Select(f => (Action)(() => f(events, transaction))).ToList();
        Transaction.CallAll(fs);
    }

    /// <summary>Converts this type to a plain JSON-like value.</summary>
    public virtual object? ToJSON() => null;

    /// <summary>
    /// Call event listeners with an event. This will also add an event to all parents (for <c>.ObserveDeep</c>
    /// handlers).
    /// </summary>
    internal void CallTypeObservers(Transaction transaction, YEvent evt)
    {
        var changedType = this;
        var type = this;
        var changedParentTypes = transaction.ChangedParentTypes;
        while (true)
        {
            if (!changedParentTypes.TryGetValue(type, out var list))
            {
                list = new List<YEvent>();
                changedParentTypes[type] = list;
            }
            list.Add(evt);
            if (type.Item == null) break;
            type = (AbstractType)type.Item.Parent!;
        }
        var fs = changedType._eH.Select(f => (Action)(() => f(evt, transaction))).ToList();
        Transaction.CallAll(fs);
    }

    internal static AbstractType ReadType(IUpdateDecoder decoder)
    {
        int typeRef = decoder.ReadTypeRef();
        return typeRef switch
        {
            ContentType.YArrayRefID => new YArray(),
            ContentType.YMapRefID => new YMap(),
            ContentType.YTextRefID => new YText(),
            ContentType.YXmlElementRefID => new YXmlElement(decoder.ReadKey()),
            ContentType.YXmlFragmentRefID => new YXmlFragment(),
            ContentType.YXmlHookRefID => new YXmlHook(decoder.ReadKey()),
            ContentType.YXmlTextRefID => new YXmlText(),
            _ => throw new InvalidDataException($"Unknown type ref {typeRef}"),
        };
    }

    internal void EnsureIntegrated()
    {
        if (Doc == null) System.Diagnostics.Debug.WriteLine("[yjs] Invalid access: Add Yjs type to a document before reading data.");
    }

    // ------------------------------------------------------------------
    // search markers
    // ------------------------------------------------------------------

    private static ArraySearchMarker MarkPosition(List<ArraySearchMarker> searchMarker, Item p, long index)
    {
        if (searchMarker.Count >= MaxSearchMarker)
        {
            // override oldest marker (we don't want to create more objects)
            var marker = searchMarker.Aggregate((a, b) => a.Timestamp < b.Timestamp ? a : b);
            marker.Overwrite(p, index);
            return marker;
        }
        var pm = new ArraySearchMarker(p, index);
        searchMarker.Add(pm);
        return pm;
    }

    /// <summary>
    /// Search marker help us to find positions in the associative array faster.
    /// They speed up the process of finding a position without much bookkeeping.
    /// </summary>
    internal static ArraySearchMarker? FindMarker(AbstractType yarray, long index)
    {
        if (yarray.Start == null || index == 0 || yarray.SearchMarker == null) return null;
        var marker = yarray.SearchMarker.Count == 0 ? null : yarray.SearchMarker.Aggregate((a, b) => Math.Abs(index - a.Index) < Math.Abs(index - b.Index) ? a : b);
        var p = yarray.Start;
        long pindex = 0;
        if (marker != null)
        {
            p = marker.P;
            pindex = marker.Index;
            marker.RefreshTimestamp(); // we used it, we might need to use it again
        }
        // iterate to right if possible
        while (p.Right != null && pindex < index)
        {
            if (!p.Deleted && p.Countable)
            {
                if (index < pindex + p.Length) break;
                pindex += p.Length;
            }
            p = p.Right;
        }
        // iterate to left if necessary (might be that pindex > index)
        while (p.Left != null && pindex > index)
        {
            p = p.Left;
            if (!p.Deleted && p.Countable) pindex -= p.Length;
        }
        // we want to make sure that p can't be merged with left, because that would screw up everything
        // in that case just return what we have (it is most likely the best marker anyway)
        // iterate to left until p can't be merged with left
        while (p.Left != null && p.Left.Id.Client == p.Id.Client && p.Left.Id.Clock + p.Left.Length == p.Id.Clock)
        {
            p = p.Left;
            if (!p.Deleted && p.Countable) pindex -= p.Length;
        }
        if (marker != null && Math.Abs(marker.Index - pindex) < (double)((AbstractType)p.Parent!).LengthInternal / MaxSearchMarker)
        {
            // adjust existing marker
            marker.Overwrite(p, pindex);
            return marker;
        }
        // create new marker
        return MarkPosition(yarray.SearchMarker, p, pindex);
    }

    /// <summary>
    /// Update markers when a change happened. This should be called before doing a deletion!
    /// </summary>
    internal static void UpdateMarkerChanges(List<ArraySearchMarker> searchMarker, long index, long len)
    {
        for (int i = searchMarker.Count - 1; i >= 0; i--)
        {
            var m = searchMarker[i];
            if (len > 0)
            {
                Item? p = m.P;
                p.Marker = false;
                // Ideally we just want to do a simple position comparison, but this will only work if
                // search markers don't point to deleted items for formats.
                // Iterate marker to prev undeleted countable position so we know what to do when updating a position
                while (p != null && (p.Deleted || !p.Countable))
                {
                    p = p.Left;
                    if (p != null && !p.Deleted && p.Countable)
                    {
                        // adjust position. the loop should break now
                        m.Index -= p.Length;
                    }
                }
                if (p == null || p.Marker)
                {
                    // remove search marker if updated position is null or if position is already marked
                    searchMarker.RemoveAt(i);
                    continue;
                }
                m.P = p;
                p.Marker = true;
            }
            if (index < m.Index || (len > 0 && index == m.Index))
            {
                // a simple index <= m.index check would actually suffice
                m.Index = Math.Max(index, m.Index + len);
            }
        }
    }

    // ------------------------------------------------------------------
    // list operations
    // ------------------------------------------------------------------

    internal static List<Item> GetTypeChildren(AbstractType t)
    {
        var s = t.Start;
        var arr = new List<Item>();
        while (s != null)
        {
            arr.Add(s);
            s = s.Right;
        }
        return arr;
    }

    internal static List<object?> TypeListSlice(AbstractType type, long start, long end)
    {
        type.EnsureIntegrated();
        if (start < 0) start = type.LengthInternal + start;
        if (end < 0) end = type.LengthInternal + end;
        long len = end - start;
        var cs = new List<object?>();
        var n = type.Start;
        while (n != null && len > 0)
        {
            if (n.Countable && !n.Deleted)
            {
                var c = n.Content.GetContent();
                if (c.Count <= start)
                {
                    start -= c.Count;
                }
                else
                {
                    for (long i = start; i < c.Count && len > 0; i++)
                    {
                        cs.Add(c[(int)i]);
                        len--;
                    }
                    start = 0;
                }
            }
            n = n.Right;
        }
        return cs;
    }

    internal static List<object?> TypeListToArray(AbstractType type)
    {
        type.EnsureIntegrated();
        var cs = new List<object?>();
        var n = type.Start;
        while (n != null)
        {
            if (n.Countable && !n.Deleted)
            {
                cs.AddRange(n.Content.GetContent());
            }
            n = n.Right;
        }
        return cs;
    }

    internal static List<object?> TypeListToArraySnapshot(AbstractType type, Snapshot snapshot)
    {
        var cs = new List<object?>();
        var n = type.Start;
        while (n != null)
        {
            if (n.Countable && Snapshot.IsVisible(n, snapshot))
            {
                cs.AddRange(n.Content.GetContent());
            }
            n = n.Right;
        }
        return cs;
    }

    internal static IEnumerable<object?> TypeListEnumerate(AbstractType type)
    {
        type.EnsureIntegrated();
        var n = type.Start;
        while (n != null)
        {
            if (n.Countable && !n.Deleted)
            {
                var c = n.Content.GetContent();
                foreach (var v in c) yield return v;
            }
            n = n.Right;
        }
    }

    internal static object? TypeListGet(AbstractType type, long index)
    {
        type.EnsureIntegrated();
        var marker = FindMarker(type, index);
        var n = type.Start;
        if (marker != null)
        {
            n = marker.P;
            index -= marker.Index;
        }
        for (; n != null; n = n.Right)
        {
            if (!n.Deleted && n.Countable)
            {
                if (index < n.Length) return n.Content.GetContent()[(int)index];
                index -= n.Length;
            }
        }
        return Undefined.Value;
    }

    private static bool IsPlainValue(object? c) => c switch
    {
        null => true,
        AbstractType => false,
        DotYjs.Doc => false,
        byte[] => false,
        _ => true,
    };

    internal static void TypeListInsertGenericsAfter(Transaction transaction, AbstractType parent, Item? referenceItem, IReadOnlyList<object?> content)
    {
        var left = referenceItem;
        var doc = transaction.Doc;
        var ownClientId = doc.ClientID;
        var store = doc.Store;
        var right = referenceItem == null ? parent.Start : referenceItem.Right;
        var jsonContent = new List<object?>();
        void PackJsonContent()
        {
            if (jsonContent.Count > 0)
            {
                left = new Item(new ID(ownClientId, store.GetState(ownClientId)), left, left?.LastId, right, right?.Id, parent, null, new ContentAny(jsonContent));
                left.Integrate(transaction, 0);
                jsonContent = new List<object?>();
            }
        }
        foreach (var c in content)
        {
            if (IsPlainValue(c))
            {
                jsonContent.Add(c);
                continue;
            }
            PackJsonContent();
            AbstractContent ct = c switch
            {
                byte[] b => new ContentBinary(b),
                Doc d => new ContentDoc(d),
                AbstractType t => new ContentType(t),
                _ => throw new ArgumentException("Unexpected content type in insert operation", nameof(content)),
            };
            left = new Item(new ID(ownClientId, store.GetState(ownClientId)), left, left?.LastId, right, right?.Id, parent, null, ct);
            left.Integrate(transaction, 0);
        }
        PackJsonContent();
    }

    internal static void TypeListInsertGenerics(Transaction transaction, AbstractType parent, long index, IReadOnlyList<object?> content)
    {
        if (index > parent.LengthInternal) throw new ArgumentOutOfRangeException(nameof(index), "Length exceeded!");
        if (index == 0)
        {
            if (parent.SearchMarker != null) UpdateMarkerChanges(parent.SearchMarker, index, content.Count);
            TypeListInsertGenericsAfter(transaction, parent, null, content);
            return;
        }
        long startIndex = index;
        var marker = FindMarker(parent, index);
        var n = parent.Start;
        if (marker != null)
        {
            n = marker.P;
            index -= marker.Index;
            // we need to iterate one to the left so that the algorithm works
            if (index == 0)
            {
                // @todo refactor this as it actually doesn't consider formats
                n = n.Prev; // important! get the left undeleted item so that we can actually decrease index
                index += (n != null && n.Countable && !n.Deleted) ? n.Length : 0;
            }
        }
        for (; n != null; n = n.Right)
        {
            if (!n.Deleted && n.Countable)
            {
                if (index <= n.Length)
                {
                    if (index < n.Length)
                    {
                        // insert in-between
                        transaction.Doc.Store.GetItemCleanStart(transaction, new ID(n.Id.Client, n.Id.Clock + index));
                    }
                    break;
                }
                index -= n.Length;
            }
        }
        if (parent.SearchMarker != null) UpdateMarkerChanges(parent.SearchMarker, startIndex, content.Count);
        TypeListInsertGenericsAfter(transaction, parent, n, content);
    }

    /// <summary>Pushing content is special as we generally want to push after the last item. So we don't have to update the search marker.</summary>
    internal static void TypeListPushGenerics(Transaction transaction, AbstractType parent, IReadOnlyList<object?> content)
    {
        // Use the marker with the highest index and iterate to the right.
        Item? n = parent.Start;
        long maxIndex = 0;
        if (parent.SearchMarker != null)
        {
            foreach (var m in parent.SearchMarker)
            {
                if (m.Index > maxIndex)
                {
                    maxIndex = m.Index;
                    n = m.P;
                }
            }
        }
        if (n != null)
        {
            while (n.Right != null) n = n.Right;
        }
        TypeListInsertGenericsAfter(transaction, parent, n, content);
    }

    internal static void TypeListDelete(Transaction transaction, AbstractType parent, long index, long length)
    {
        if (length == 0) return;
        long startIndex = index;
        long startLength = length;
        var marker = FindMarker(parent, index);
        var n = parent.Start;
        if (marker != null)
        {
            n = marker.P;
            index -= marker.Index;
        }
        // compute the first item to be deleted
        for (; n != null && index > 0; n = n.Right)
        {
            if (!n.Deleted && n.Countable)
            {
                if (index < n.Length)
                {
                    transaction.Doc.Store.GetItemCleanStart(transaction, new ID(n.Id.Client, n.Id.Clock + index));
                }
                index -= n.Length;
            }
        }
        // delete all items until done
        while (length > 0 && n != null)
        {
            if (!n.Deleted)
            {
                if (length < n.Length)
                {
                    transaction.Doc.Store.GetItemCleanStart(transaction, new ID(n.Id.Client, n.Id.Clock + length));
                }
                n.Delete(transaction);
                length -= n.Length;
            }
            n = n.Right;
        }
        if (length > 0) throw new ArgumentOutOfRangeException(nameof(length), "Length exceeded!");
        if (parent.SearchMarker != null)
        {
            UpdateMarkerChanges(parent.SearchMarker, startIndex, -startLength + length);
        }
    }

    // ------------------------------------------------------------------
    // map operations
    // ------------------------------------------------------------------

    internal static void TypeMapDelete(Transaction transaction, AbstractType parent, string key)
    {
        if (parent.Map.TryGetValue(key, out var c)) c.Delete(transaction);
    }

    internal static void TypeMapSet(Transaction transaction, AbstractType parent, string key, object? value)
    {
        var left = parent.Map.GetValueOrDefault(key);
        var doc = transaction.Doc;
        var ownClientId = doc.ClientID;
        AbstractContent content = value switch
        {
            null => new ContentAny(new List<object?> { null }),
            byte[] b => new ContentBinary(b),
            Doc d => new ContentDoc(d),
            AbstractType t => new ContentType(t),
            _ => new ContentAny(new List<object?> { value }),
        };
        new Item(new ID(ownClientId, doc.Store.GetState(ownClientId)), left, left?.LastId, null, null, parent, key, content).Integrate(transaction, 0);
    }

    internal static object? TypeMapGet(AbstractType parent, string key)
    {
        parent.EnsureIntegrated();
        return parent.Map.TryGetValue(key, out var val) && !val.Deleted ? val.Content.GetContent()[(int)val.Length - 1] : Undefined.Value;
    }

    internal static Dictionary<string, object?> TypeMapGetAll(AbstractType parent)
    {
        parent.EnsureIntegrated();
        var res = new Dictionary<string, object?>();
        foreach (var (key, value) in parent.Map)
        {
            if (!value.Deleted) res[key] = value.Content.GetContent()[(int)value.Length - 1];
        }
        return res;
    }

    internal static bool TypeMapHas(AbstractType parent, string key)
    {
        parent.EnsureIntegrated();
        return parent.Map.TryGetValue(key, out var val) && !val.Deleted;
    }

    internal static object? TypeMapGetSnapshot(AbstractType parent, string key, Snapshot snapshot)
    {
        var v = parent.Map.GetValueOrDefault(key);
        while (v != null && (!snapshot.Sv.ContainsKey(v.Id.Client) || v.Id.Clock >= snapshot.Sv.GetValueOrDefault(v.Id.Client)))
        {
            v = v.Left;
        }
        return v != null && Snapshot.IsVisible(v, snapshot) ? v.Content.GetContent()[(int)v.Length - 1] : Undefined.Value;
    }

    internal static Dictionary<string, object?> TypeMapGetAllSnapshot(AbstractType parent, Snapshot snapshot)
    {
        var res = new Dictionary<string, object?>();
        foreach (var (key, value) in parent.Map)
        {
            Item? v = value;
            while (v != null && (!snapshot.Sv.ContainsKey(v.Id.Client) || v.Id.Clock >= snapshot.Sv.GetValueOrDefault(v.Id.Client)))
            {
                v = v.Left;
            }
            if (v != null && Snapshot.IsVisible(v, snapshot)) res[key] = v.Content.GetContent()[(int)v.Length - 1];
        }
        return res;
    }

    internal static object? ToJsonValue(object? v) => v is AbstractType t ? t.ToJSON() : v;
}
