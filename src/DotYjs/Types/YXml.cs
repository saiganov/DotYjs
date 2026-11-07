using System.Collections;
using System.Text;

namespace DotYjs;

/// <summary>Event describing the changes of an XML type in a transaction.</summary>
public sealed class YXmlEvent : YEvent
{
    /// <summary>Creates the event.</summary>
    public YXmlEvent(AbstractType target, HashSet<string?> subs, Transaction transaction) : base(target, transaction)
    {
        foreach (var sub in subs)
        {
            if (sub == null) ChildListChanged = true;
            else AttributesChanged.Add(sub);
        }
    }

    /// <summary>Whether the children changed.</summary>
    public bool ChildListChanged { get; }

    /// <summary>Set of all changed attributes.</summary>
    public HashSet<string> AttributesChanged { get; } = new();
}

/// <summary>Walks the XML tree in document order (depth-first), yielding types matching a filter.</summary>
public sealed class YXmlTreeWalker : IEnumerable<AbstractType>
{
    private readonly YXmlFragment _root;
    private readonly Func<AbstractType, bool> _filter;

    /// <summary>Creates a walker over the descendants of <paramref name="root"/>; with no filter it yields every descendant.</summary>
    public YXmlTreeWalker(YXmlFragment root, Func<AbstractType, bool>? filter = null)
    {
        _root = root;
        _filter = filter ?? (_ => true);
        root.EnsureIntegrated();
    }

    /// <summary>Enumerates the matching descendants in document order.</summary>
    public IEnumerator<AbstractType> GetEnumerator()
    {
        Item? n = _root.Start;
        bool firstCall = true;
        while (true)
        {
            var type = n?.Content is ContentType ct ? ct.Type : null;
            if (n != null && (!firstCall || n.Deleted || type == null || !_filter(type)))
            {
                do
                {
                    // a deleted (possibly garbage collected) node no longer carries a ContentType
                    type = !n.Deleted && n.Content is ContentType nct ? nct.Type : null;
                    if (type != null && (type is YXmlElement || type.GetType() == typeof(YXmlFragment)) && type.Start != null)
                    {
                        // walk down in the tree
                        n = type.Start;
                    }
                    else
                    {
                        // walk right or up in the tree
                        while (n != null)
                        {
                            var nxt = n.Next;
                            if (nxt != null)
                            {
                                n = nxt;
                                break;
                            }
                            else if (n.Parent == _root)
                            {
                                n = null;
                            }
                            else
                            {
                                n = ((AbstractType)n.Parent!).Item;
                            }
                        }
                    }
                } while (n != null && (n.Deleted || n.Content is not ContentType || !_filter(((ContentType)n.Content).Type)));
            }
            firstCall = false;
            if (n == null) yield break;
            yield return ((ContentType)n.Content).Type;
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// Represents a list of <see cref="YXmlElement"/> and <see cref="YXmlText"/> types. A YXmlFragment is similar to a
/// <see cref="YXmlElement"/>, but it does not have a nodeName and it does not have attributes.
/// </summary>
public class YXmlFragment : AbstractType, IEnumerable<object?>
{
    private List<object?>? _prelimContent = new();

    /// <summary>Creates a fragment that is not yet part of a document; insert it into a document type to use it.</summary>
    public YXmlFragment() { }

    /// <summary>The first child, or null if there are no children.</summary>
    public AbstractType? FirstChild
    {
        get
        {
            var first = First;
            return first != null ? first.Content.GetContent()[0] as AbstractType : null;
        }
    }

    internal override void Integrate(Doc y, Item? item)
    {
        base.Integrate(y, item);
        var prelim = _prelimContent!;
        _prelimContent = null;
        Insert(0, prelim);
    }

    internal override AbstractType InternalCopy() => new YXmlFragment();

    /// <inheritdoc />
    public override AbstractType Clone()
    {
        var el = new YXmlFragment();
        el.Insert(0, ToArray().Select(i => i is AbstractType t ? t.Clone() : i).ToList());
        return el;
    }

    /// <summary>The number of children.</summary>
    public int Length => _prelimContent?.Count ?? (int)LengthInternal;

    /// <summary>Create a subtree of children matching a filter.</summary>
    public YXmlTreeWalker CreateTreeWalker(Func<AbstractType, bool> filter) => new(this, filter);

    /// <summary>Returns the first element that matches the nodeName query (case-insensitive), or null.</summary>
    public AbstractType? QuerySelector(string query)
    {
        return new YXmlTreeWalker(this, el => el is YXmlElement e && string.Equals(e.NodeName, query, StringComparison.OrdinalIgnoreCase)).FirstOrDefault();
    }

    /// <summary>Returns all elements that match the nodeName query (case-insensitive).</summary>
    public List<AbstractType> QuerySelectorAll(string query)
    {
        return new YXmlTreeWalker(this, el => el is YXmlElement e && string.Equals(e.NodeName, query, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    internal override void CallObserver(Transaction transaction, HashSet<string?> parentSubs)
        => CallTypeObservers(transaction, new YXmlEvent(this, parentSubs, transaction));

    /// <summary>Get the string representation of all the children of this YXmlFragment.</summary>
    public override string ToString() => string.Concat(TypeListToArray(this).Select(x => x?.ToString()));

    /// <inheritdoc />
    public override object? ToJSON() => ToString();

    /// <summary>Inserts new content at an index.</summary>
    public void Insert(int index, IReadOnlyList<object?> content)
    {
        if (Doc != null) Transaction.Transact(Doc, t => TypeListInsertGenerics(t, this, index, content));
        else _prelimContent!.InsertRange(index, content);
    }

    /// <summary>Inserts the given children at <paramref name="index"/>.</summary>
    public void Insert(int index, params object?[] content) => Insert(index, (IReadOnlyList<object?>)content);

    /// <summary>Inserts new content after a reference element (null to insert at the start).</summary>
    public void InsertAfter(AbstractType? reference, IReadOnlyList<object?> content)
    {
        if (Doc != null)
        {
            Transaction.Transact(Doc, t => TypeListInsertGenericsAfter(t, this, reference?.Item, content));
        }
        else
        {
            var pc = _prelimContent!;
            int index = reference == null ? 0 : pc.FindIndex(el => ReferenceEquals(el, reference)) + 1;
            if (index == 0 && reference != null) throw new ArgumentException("Reference item not found", nameof(reference));
            pc.InsertRange(index, content);
        }
    }

    /// <summary>Deletes elements starting from an index.</summary>
    public void Delete(int index, int length = 1)
    {
        if (Doc != null) Transaction.Transact(Doc, t => TypeListDelete(t, this, index, length));
        else _prelimContent!.RemoveRange(index, length);
    }

    /// <summary>Returns the children as a list.</summary>
    public List<object?> ToArray() => _prelimContent != null ? new List<object?>(_prelimContent) : TypeListToArray(this);

    /// <summary>Appends the given children at the end.</summary>
    public void Push(IReadOnlyList<object?> content) => Insert(Length, content);
    /// <summary>Appends the given children at the end.</summary>
    public void Push(params object?[] content) => Push((IReadOnlyList<object?>)content);
    /// <summary>Inserts the given children at the start.</summary>
    public void Unshift(IReadOnlyList<object?> content) => Insert(0, content);

    /// <summary>Returns the child at <paramref name="index"/>.</summary>
    public object? Get(int index) => _prelimContent != null ? _prelimContent[index] : TypeListGet(this, index);

    /// <summary>Returns the children from <paramref name="start"/> up to, but not including, <paramref name="end"/> (the end of the list by default).</summary>
    public List<object?> Slice(int start = 0, int? end = null) => TypeListSlice(this, start, end ?? Length);

    /// <summary>Enumerates the children.</summary>
    public IEnumerator<object?> GetEnumerator() => (_prelimContent ?? TypeListEnumerate(this)).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    internal override void WriteType(IUpdateEncoder encoder) => encoder.WriteTypeRef(ContentType.YXmlFragmentRefID);
}

/// <summary>An YXmlElement imitates the behavior of a DOM Element: it has a nodeName, attributes and children.</summary>
public class YXmlElement : YXmlFragment
{
    private Dictionary<string, object?>? _prelimAttrs = new();

    /// <summary>Creates an element named "UNDEFINED" that is not yet part of a document.</summary>
    public YXmlElement() : this("UNDEFINED") { }

    /// <summary>Creates an element with the given node name that is not yet part of a document; insert it into a document type to use it.</summary>
    public YXmlElement(string nodeName)
    {
        NodeName = nodeName;
    }

    /// <summary>The node name (tag name) of the element.</summary>
    public string NodeName { get; internal set; }

    /// <summary>The next sibling in the parent, or null.</summary>
    public AbstractType? NextSibling
    {
        get
        {
            var n = Item?.Next;
            return n != null ? ((ContentType)n.Content).Type : null;
        }
    }

    /// <summary>The previous sibling in the parent, or null.</summary>
    public AbstractType? PrevSibling
    {
        get
        {
            var n = Item?.Prev;
            return n != null ? ((ContentType)n.Content).Type : null;
        }
    }

    internal override void Integrate(Doc y, Item? item)
    {
        base.Integrate(y, item);
        var prelim = _prelimAttrs!;
        _prelimAttrs = null;
        foreach (var (key, value) in prelim) SetAttribute(key, value);
    }

    internal override AbstractType InternalCopy() => new YXmlElement(NodeName);

    /// <inheritdoc />
    public override AbstractType Clone()
    {
        var el = new YXmlElement(NodeName);
        foreach (var (key, value) in GetAttributes())
        {
            if (value is string s) el.SetAttribute(key, s);
        }
        el.Insert(0, ToArray().Select(i => i is AbstractType t ? t.Clone() : i).ToList());
        return el;
    }

    /// <summary>Returns the XML serialization of this YXmlElement. Attributes are ordered by name.</summary>
    public override string ToString()
    {
        var attrs = GetAttributes();
        var sb = new StringBuilder();
        var nodeName = NodeName.ToLowerInvariant();
        sb.Append('<').Append(nodeName);
        foreach (var key in attrs.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            sb.Append(' ').Append(key).Append("=\"").Append(attrs[key]).Append('"');
        }
        sb.Append('>').Append(base.ToString()).Append("</").Append(nodeName).Append('>');
        return sb.ToString();
    }

    /// <summary>Removes an attribute.</summary>
    public void RemoveAttribute(string attributeName)
    {
        if (Doc != null) Transaction.Transact(Doc, t => TypeMapDelete(t, this, attributeName));
        else _prelimAttrs!.Remove(attributeName);
    }

    /// <summary>Sets an attribute.</summary>
    public void SetAttribute(string attributeName, object? attributeValue)
    {
        if (Doc != null) Transaction.Transact(Doc, t => TypeMapSet(t, this, attributeName, attributeValue));
        else _prelimAttrs![attributeName] = attributeValue;
    }

    /// <summary>Returns the value of an attribute, or null.</summary>
    public object? GetAttribute(string attributeName)
    {
        if (_prelimAttrs != null) return _prelimAttrs.GetValueOrDefault(attributeName);
        var v = TypeMapGet(this, attributeName);
        return v is Lib0.Undefined ? null : v;
    }

    /// <summary>Whether the element has the attribute.</summary>
    public bool HasAttribute(string attributeName) => _prelimAttrs?.ContainsKey(attributeName) ?? TypeMapHas(this, attributeName);

    /// <summary>Returns all attributes, optionally as they were at <paramref name="snapshot"/>.</summary>
    public Dictionary<string, object?> GetAttributes(Snapshot? snapshot = null)
    {
        if (_prelimAttrs != null) return new Dictionary<string, object?>(_prelimAttrs);
        return snapshot != null ? TypeMapGetAllSnapshot(this, snapshot) : TypeMapGetAll(this);
    }

    internal override void WriteType(IUpdateEncoder encoder)
    {
        encoder.WriteTypeRef(ContentType.YXmlElementRefID);
        encoder.WriteKey(NodeName);
    }
}

/// <summary>Represents text in an XML tree.</summary>
public class YXmlText : YText
{
    /// <summary>Creates an empty XML text that is not yet part of a document.</summary>
    public YXmlText() { }

    /// <summary>Creates an XML text with initial content that is not yet part of a document; insert it into an XML type to use it.</summary>
    public YXmlText(string str) : base(str) { }

    /// <summary>The next sibling in the parent, or null.</summary>
    public AbstractType? NextSibling
    {
        get
        {
            var n = Item?.Next;
            return n != null ? ((ContentType)n.Content).Type : null;
        }
    }

    /// <summary>The previous sibling in the parent, or null.</summary>
    public AbstractType? PrevSibling
    {
        get
        {
            var n = Item?.Prev;
            return n != null ? ((ContentType)n.Content).Type : null;
        }
    }

    internal override AbstractType InternalCopy() => new YXmlText();

    /// <inheritdoc />
    public override AbstractType Clone()
    {
        var text = new YXmlText();
        text.ApplyDelta(ToDelta());
        return text;
    }

    /// <summary>Returns the text as XML: formatting attributes become nested tags, like yjs YXmlText.toString.</summary>
    public override string ToString()
    {
        var sb = new StringBuilder();
        foreach (var delta in ToDelta())
        {
            var nestedNodes = new List<(string nodeName, List<(string key, object? value)> attrs)>();
            if (delta.Attributes != null)
            {
                foreach (var (nodeName, attrsObj) in delta.Attributes)
                {
                    var attrs = new List<(string key, object? value)>();
                    if (attrsObj is IDictionary<string, object?> d)
                    {
                        foreach (var (k, v) in d) attrs.Add((k, v));
                    }
                    attrs.Sort((a, b) => string.CompareOrdinal(a.key, b.key));
                    nestedNodes.Add((nodeName, attrs));
                }
            }
            nestedNodes.Sort((a, b) => string.CompareOrdinal(a.nodeName, b.nodeName));
            foreach (var node in nestedNodes)
            {
                sb.Append('<').Append(node.nodeName);
                foreach (var attr in node.attrs) sb.Append(' ').Append(attr.key).Append("=\"").Append(attr.value).Append('"');
                sb.Append('>');
            }
            sb.Append(delta.Insert);
            for (int i = nestedNodes.Count - 1; i >= 0; i--) sb.Append("</").Append(nestedNodes[i].nodeName).Append('>');
        }
        return sb.ToString();
    }

    /// <inheritdoc />
    public override object? ToJSON() => ToString();

    internal override void WriteType(IUpdateEncoder encoder) => encoder.WriteTypeRef(ContentType.YXmlTextRefID);
}

/// <summary>You can manage binding to a custom type with YXmlHook.</summary>
public class YXmlHook : YMap
{
    /// <summary>Creates a hook named "UNDEFINED" that is not yet part of a document.</summary>
    public YXmlHook() : this("UNDEFINED") { }

    /// <summary>Creates a hook with the given name that is not yet part of a document; insert it into an XML type to use it.</summary>
    public YXmlHook(string hookName)
    {
        HookName = hookName;
    }

    /// <summary>The name of the hook, used to find the custom binding.</summary>
    public string HookName { get; internal set; }

    internal override AbstractType InternalCopy() => new YXmlHook(HookName);

    /// <inheritdoc />
    public override AbstractType Clone()
    {
        var el = new YXmlHook(HookName);
        foreach (var (key, value) in this) el.Set(key, value);
        return el;
    }

    internal override void WriteType(IUpdateEncoder encoder)
    {
        encoder.WriteTypeRef(ContentType.YXmlHookRefID);
        encoder.WriteKey(HookName);
    }
}
