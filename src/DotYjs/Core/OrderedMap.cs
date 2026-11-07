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

    public OrderedMap() => _dict = new Dictionary<TKey, LinkedListNode<KeyValuePair<TKey, TValue>>>();

    public OrderedMap(IEqualityComparer<TKey> comparer) => _dict = new Dictionary<TKey, LinkedListNode<KeyValuePair<TKey, TValue>>>(comparer);

    public int Count => _dict.Count;

    public TValue this[TKey key]
    {
        get => _dict[key].Value.Value;
        set
        {
            if (_dict.TryGetValue(key, out var node)) node.Value = new KeyValuePair<TKey, TValue>(key, value);
            else _dict[key] = _list.AddLast(new KeyValuePair<TKey, TValue>(key, value));
        }
    }

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

    public bool ContainsKey(TKey key) => _dict.ContainsKey(key);

    public bool Remove(TKey key)
    {
        if (_dict.Remove(key, out var node))
        {
            _list.Remove(node);
            return true;
        }
        return false;
    }

    public void Clear()
    {
        _dict.Clear();
        _list.Clear();
    }

    public IEnumerable<TKey> Keys => _list.Select(kv => kv.Key);
    public IEnumerable<TValue> Values => _list.Select(kv => kv.Value);

    public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() => _list.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
