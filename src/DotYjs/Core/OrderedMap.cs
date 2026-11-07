using System.Collections;
using System.Diagnostics.CodeAnalysis;

namespace DotYjs;

/// <summary>
/// A dictionary that preserves insertion order even after removals (like JavaScript's <c>Map</c>).
/// </summary>
public sealed class OrderedMap<TKey, TValue> : IEnumerable<KeyValuePair<TKey, TValue>> where TKey : notnull
{
    private readonly Dictionary<TKey, LinkedListNode<KeyValuePair<TKey, TValue>>> _dict;
    private readonly LinkedList<KeyValuePair<TKey, TValue>> _list = new();

    /// <summary>Creates an empty map with the default key comparer.</summary>
    public OrderedMap() => _dict = new Dictionary<TKey, LinkedListNode<KeyValuePair<TKey, TValue>>>();

    /// <summary>Creates an empty map with the given key comparer.</summary>
    public OrderedMap(IEqualityComparer<TKey> comparer) => _dict = new Dictionary<TKey, LinkedListNode<KeyValuePair<TKey, TValue>>>(comparer);

    /// <summary>The number of entries.</summary>
    public int Count => _dict.Count;

    /// <summary>Gets or sets the value for a key. Setting a new key appends it at the end; setting an existing key keeps its position.</summary>
    public TValue this[TKey key]
    {
        get => _dict[key].Value.Value;
        set
        {
            if (_dict.TryGetValue(key, out var node)) node.Value = new KeyValuePair<TKey, TValue>(key, value);
            else _dict[key] = _list.AddLast(new KeyValuePair<TKey, TValue>(key, value));
        }
    }

    /// <summary>Gets the value for a key, if present.</summary>
    public bool TryGetValue(TKey key, [MaybeNullWhen(false)] out TValue value)
    {
        if (_dict.TryGetValue(key, out var node))
        {
            value = node.Value.Value;
            return true;
        }
        value = default;
        return false;
    }

    /// <summary>Whether the map contains the key.</summary>
    public bool ContainsKey(TKey key) => _dict.ContainsKey(key);

    /// <summary>Removes the key. Returns false if it was not present.</summary>
    public bool Remove(TKey key)
    {
        if (_dict.Remove(key, out var node))
        {
            _list.Remove(node);
            return true;
        }
        return false;
    }

    /// <summary>Removes all entries.</summary>
    public void Clear()
    {
        _dict.Clear();
        _list.Clear();
    }

    /// <summary>The keys in insertion order.</summary>
    public IEnumerable<TKey> Keys => _list.Select(kv => kv.Key);
    /// <summary>The values in insertion order.</summary>
    public IEnumerable<TValue> Values => _list.Select(kv => kv.Value);

    /// <summary>Enumerates the entries in insertion order.</summary>
    public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() => _list.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
