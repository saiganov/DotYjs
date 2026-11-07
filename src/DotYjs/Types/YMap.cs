using System.Collections;
using DotYjs.Lib0;

namespace DotYjs;

/// <summary>Event describing the changes of a <see cref="YMap"/> in a transaction.</summary>
public sealed class YMapEvent : YEvent
{
    /// <summary>Creates the event.</summary>
    public YMapEvent(YMap target, Transaction transaction, HashSet<string?> subs) : base(target, transaction)
    {
        KeysChanged = subs;
    }

    /// <summary>The keys that changed.</summary>
    public HashSet<string?> KeysChanged { get; }
}

/// <summary>A shared Map implementation.</summary>
public class YMap : AbstractType, IEnumerable<KeyValuePair<string, object?>>
{
    private Dictionary<string, object?>? _prelimContent;

    /// <summary>Creates an empty map that is not yet part of a document; insert it into a document type to use it.</summary>
    public YMap() : this(null) { }

    /// <summary>Creates a map with the given entries that is not yet part of a document; insert it into a document type to use it.</summary>
    public YMap(IEnumerable<KeyValuePair<string, object?>>? entries)
    {
        _prelimContent = entries == null ? new Dictionary<string, object?>() : new Dictionary<string, object?>(entries);
    }

    internal override void Integrate(Doc y, Item? item)
    {
        base.Integrate(y, item);
        var prelim = _prelimContent!;
        _prelimContent = null;
        foreach (var (key, value) in prelim) Set(key, value);
    }

    internal override AbstractType InternalCopy() => new YMap();

    /// <inheritdoc />
    public override AbstractType Clone()
    {
        var map = new YMap();
        foreach (var (key, value) in this) map.Set(key, value is AbstractType t ? t.Clone() : value);
        return map;
    }

    internal override void CallObserver(Transaction transaction, HashSet<string?> parentSubs)
        => CallTypeObservers(transaction, new YMapEvent(this, transaction, parentSubs));

    /// <summary>Transforms this shared type to a JSON-like dictionary.</summary>
    public override object? ToJSON()
    {
        if (_prelimContent != null) return _prelimContent.ToDictionary(kv => kv.Key, kv => ToJsonValue(kv.Value));
        var map = new Dictionary<string, object?>();
        foreach (var (key, item) in Map)
        {
            if (!item.Deleted)
            {
                var v = item.Content.GetContent()[(int)item.Length - 1];
                map[key] = ToJsonValue(v);
            }
        }
        return map;
    }

    /// <summary>Returns the number of stored elements.</summary>
    public int Size => _prelimContent?.Count ?? Map.Count(kv => !kv.Value.Deleted);

    /// <summary>The keys of the stored elements.</summary>
    public IEnumerable<string> Keys => this.Select(kv => kv.Key);

    /// <summary>The values of the stored elements.</summary>
    public IEnumerable<object?> Values => this.Select(kv => kv.Value);

    /// <summary>Remove a specified element from this YMap.</summary>
    public void Delete(string key)
    {
        if (Doc != null) Transaction.Transact(Doc, t => TypeMapDelete(t, this, key));
        else _prelimContent!.Remove(key);
    }

    /// <summary>Adds or updates an element with a specified key and value.</summary>
    public T Set<T>(string key, T value)
    {
        if (Doc != null) Transaction.Transact(Doc, t => TypeMapSet(t, this, key, value));
        else _prelimContent![key] = value;
        return value;
    }

    /// <summary>Returns the value for <paramref name="key"/>, or null if the key does not exist.</summary>
    public object? Get(string key) => TryGet(key, out var v) ? v : null;

    /// <summary>Gets the value for a key (null if it does not exist) or sets it.</summary>
    public object? this[string key]
    {
        get => Get(key);
        set => Set(key, value);
    }

    /// <summary>Gets the value for a key, if it exists.</summary>
    public bool TryGet(string key, out object? value)
    {
        if (_prelimContent != null) return _prelimContent.TryGetValue(key, out value);
        var v = TypeMapGet(this, key);
        if (v is Undefined && !TypeMapHas(this, key))
        {
            value = null;
            return false;
        }
        value = v;
        return true;
    }

    /// <summary>Returns a boolean indicating whether the specified key exists or not.</summary>
    public bool Has(string key) => _prelimContent?.ContainsKey(key) ?? TypeMapHas(this, key);

    /// <summary>Removes all elements from this YMap.</summary>
    public void Clear()
    {
        if (Doc != null)
        {
            Transaction.Transact(Doc, t =>
            {
                foreach (var key in Keys.ToList()) TypeMapDelete(t, this, key);
            });
        }
        else
        {
            _prelimContent!.Clear();
        }
    }

    /// <summary>Enumerates the stored elements.</summary>
    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
    {
        if (_prelimContent != null)
        {
            foreach (var kv in _prelimContent) yield return kv;
            yield break;
        }
        EnsureIntegrated();
        foreach (var (key, item) in Map.ToList())
        {
            if (!item.Deleted) yield return new KeyValuePair<string, object?>(key, item.Content.GetContent()[(int)item.Length - 1]);
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    internal override void WriteType(IUpdateEncoder encoder) => encoder.WriteTypeRef(ContentType.YMapRefID);
}
