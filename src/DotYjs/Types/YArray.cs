using System.Collections;

namespace DotYjs;

/// <summary>Event describing the changes of a <see cref="YArray"/> in a transaction.</summary>
public sealed class YArrayEvent : YEvent
{
    /// <summary>Creates the event.</summary>
    public YArrayEvent(YArray target, Transaction transaction) : base(target, transaction) { }
}

/// <summary>A shared Array implementation.</summary>
public class YArray : AbstractType, IEnumerable<object?>
{
    private List<object?>? _prelimContent = new();

    /// <summary>Creates an array that is not yet part of a document; insert it into a document type to use it.</summary>
    public YArray()
    {
        SearchMarker = new List<ArraySearchMarker>();
    }

    /// <summary>Construct a new YArray containing the specified items.</summary>
    public static YArray From(IEnumerable<object?> items)
    {
        var a = new YArray();
        a.Push(items.ToList());
        return a;
    }

    internal override void Integrate(Doc y, Item? item)
    {
        base.Integrate(y, item);
        var prelim = _prelimContent!;
        _prelimContent = null;
        Insert(0, prelim);
    }

    internal override AbstractType InternalCopy() => new YArray();

    /// <inheritdoc />
    public override AbstractType Clone()
    {
        var arr = new YArray();
        arr.Insert(0, ToArray().Select(el => el is AbstractType t ? t.Clone() : el).ToList());
        return arr;
    }

    /// <summary>The number of elements.</summary>
    public int Length => _prelimContent?.Count ?? (int)LengthInternal;

    internal override void CallObserver(Transaction transaction, HashSet<string?> parentSubs)
    {
        base.CallObserver(transaction, parentSubs);
        CallTypeObservers(transaction, new YArrayEvent(this, transaction));
    }

    /// <summary>
    /// Inserts new content at an index. Values can be primitives, strings, byte[], lists, dictionaries, other shared
    /// types or <see cref="Doc"/>s.
    /// </summary>
    public void Insert(int index, IReadOnlyList<object?> content)
    {
        if (Doc != null)
        {
            Transaction.Transact(Doc, t => TypeListInsertGenerics(t, this, index, content));
        }
        else
        {
            _prelimContent!.InsertRange(index, content);
        }
    }

    /// <summary>Inserts the given values at <paramref name="index"/>.</summary>
    public void Insert(int index, params object?[] content) => Insert(index, (IReadOnlyList<object?>)content);

    /// <summary>Appends content to this YArray.</summary>
    public void Push(IReadOnlyList<object?> content)
    {
        if (Doc != null)
        {
            Transaction.Transact(Doc, t => TypeListPushGenerics(t, this, content));
        }
        else
        {
            _prelimContent!.AddRange(content);
        }
    }

    /// <summary>Appends the given values at the end.</summary>
    public void Push(params object?[] content) => Push((IReadOnlyList<object?>)content);

    /// <summary>Prepends content to this YArray.</summary>
    public void Unshift(IReadOnlyList<object?> content) => Insert(0, content);

    /// <summary>Deletes elements starting from an index.</summary>
    public void Delete(int index, int length = 1)
    {
        if (Doc != null)
        {
            Transaction.Transact(Doc, t => TypeListDelete(t, this, index, length));
        }
        else
        {
            _prelimContent!.RemoveRange(index, length);
        }
    }

    /// <summary>Returns the i-th element of this YArray.</summary>
    public object? Get(int index)
    {
        if (_prelimContent != null) return _prelimContent[index];
        var v = TypeListGet(this, index);
        if (v is Lib0.Undefined && (index < 0 || index >= LengthInternal)) throw new ArgumentOutOfRangeException(nameof(index));
        return v;
    }

    /// <summary>Returns the element at <paramref name="index"/>.</summary>
    public object? this[int index] => Get(index);

    /// <summary>Transforms this YArray to a List.</summary>
    public List<object?> ToArray() => _prelimContent != null ? new List<object?>(_prelimContent) : TypeListToArray(this);

    /// <summary>Returns a portion of this YArray.</summary>
    public List<object?> Slice(int start = 0, int? end = null) => TypeListSlice(this, start, end ?? Length);

    /// <summary>Transforms this shared type to a JSON-like list (nested shared types are converted too).</summary>
    public override object? ToJSON() => ToArray().Select(ToJsonValue).ToList();

    /// <summary>Enumerates the elements.</summary>
    public IEnumerator<object?> GetEnumerator() => (_prelimContent ?? TypeListEnumerate(this)).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    internal override void WriteType(IUpdateEncoder encoder) => encoder.WriteTypeRef(ContentType.YArrayRefID);
}
