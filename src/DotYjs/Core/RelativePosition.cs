using DotYjs.Lib0;

namespace DotYjs;

/// <summary>
/// A relative position is based on the Yjs model and is not affected by document changes.
/// If you place a relative position before a certain character, it will always point to this character.
/// If you place a relative position at the end of a type, it will always point to the end of the type.
/// Wire-compatible with yjs <c>encodeRelativePosition</c> / <c>decodeRelativePosition</c>.
/// </summary>
public sealed class RelativePosition : IEquatable<RelativePosition>
{
    public RelativePosition(ID? type, string? tname, ID? item, long assoc = 0)
    {
        Type = type;
        TName = tname;
        Item = item;
        Assoc = assoc;
    }

    /// <summary>ID of the item holding the parent type (when the type is nested).</summary>
    public ID? Type { get; }

    /// <summary>Name of the root type (when the type is a root type of the document).</summary>
    public string? TName { get; }

    /// <summary>ID of the character/element this position is attached to, or null for "end of type".</summary>
    public ID? Item { get; }

    /// <summary>
    /// If assoc &gt;= 0 the position is associated to the character after it (position 1 in "ab" → "b").
    /// If assoc &lt; 0 the position is associated to the character before it.
    /// </summary>
    public long Assoc { get; }

    // ---- creation ----

    /// <summary>Creates a relative position in <paramref name="type"/> that points at <paramref name="item"/> (yjs createRelativePosition).</summary>
    public static RelativePosition Create(AbstractType type, ID? item, long assoc = 0)
    {
        ID? typeId = null;
        string? tname = null;
        if (type.Item == null) tname = ID.FindRootTypeKey(type);
        else typeId = new ID(type.Item.Id.Client, type.Item.Id.Clock);
        return new RelativePosition(typeId, tname, item, assoc);
    }

    /// <summary>Creates a relative position from an absolute index inside <paramref name="type"/> (yjs createRelativePositionFromTypeIndex).</summary>
    public static RelativePosition FromTypeIndex(AbstractType type, long index, long assoc = 0)
    {
        var t = type.Start;
        if (assoc < 0)
        {
            // associated to the left character or the beginning of a type, increment index if possible.
            if (index == 0) return Create(type, null, assoc);
            index--;
        }
        while (t != null)
        {
            if (!t.Deleted && t.Countable)
            {
                if (t.Length > index)
                {
                    // case 1: found position somewhere in the linked list
                    return Create(type, new ID(t.Id.Client, t.Id.Clock + index), assoc);
                }
                index -= t.Length;
            }
            if (t.Right == null && assoc < 0)
            {
                // left-associated position, return last available id
                return Create(type, t.LastId, assoc);
            }
            t = t.Right;
        }
        return Create(type, null, assoc);
    }

    // ---- encoding ----

    public void Write(Encoder encoder)
    {
        if (Item != null)
        {
            encoder.WriteVarUint(0);
            ID.Write(encoder, Item);
        }
        else if (TName != null)
        {
            // case 2: found position at the end of the list and type is stored in y.share
            encoder.WriteUint8(1);
            encoder.WriteVarString(TName);
        }
        else if (Type != null)
        {
            // case 3: found position at the end of the list and type is attached to an item
            encoder.WriteUint8(2);
            ID.Write(encoder, Type);
        }
        else
        {
            throw new InvalidOperationException("Invalid relative position: no type, tname or item.");
        }
        encoder.WriteVarInt(Assoc);
    }

    public byte[] Encode()
    {
        var encoder = new Encoder();
        Write(encoder);
        return encoder.ToArray();
    }

    public static RelativePosition Read(Decoder decoder)
    {
        ID? type = null;
        string? tname = null;
        ID? itemId = null;
        switch (decoder.ReadVarUint())
        {
            case 0: itemId = ID.Read(decoder); break;
            case 1: tname = decoder.ReadVarString(); break;
            case 2: type = ID.Read(decoder); break;
        }
        var assoc = decoder.HasContent ? decoder.ReadVarInt() : 0;
        return new RelativePosition(type, tname, itemId, assoc);
    }

    public static RelativePosition Decode(byte[] data) => Read(new Decoder(data));

    /// <summary>JSON-like representation compatible with yjs <c>relativePositionToJSON</c>.</summary>
    public Dictionary<string, object?> ToJSON()
    {
        var json = new Dictionary<string, object?>();
        if (Type != null) json["type"] = IdToJson(Type);
        if (TName != null) json["tname"] = TName;
        if (Item != null) json["item"] = IdToJson(Item);
        json["assoc"] = Assoc;
        return json;
    }

    public static RelativePosition FromJSON(IReadOnlyDictionary<string, object?> json)
    {
        return new RelativePosition(
            json.TryGetValue("type", out var t) && t != null ? IdFromJson(t) : null,
            json.TryGetValue("tname", out var n) ? n as string : null,
            json.TryGetValue("item", out var i) && i != null ? IdFromJson(i) : null,
            json.TryGetValue("assoc", out var a) && a != null ? Convert.ToInt64(a, System.Globalization.CultureInfo.InvariantCulture) : 0);
    }

    private static Dictionary<string, object?> IdToJson(ID id) => new() { ["client"] = id.Client, ["clock"] = id.Clock };

    private static ID IdFromJson(object o)
    {
        var d = (IReadOnlyDictionary<string, object?>)o;
        return new ID(Convert.ToInt64(d["client"], System.Globalization.CultureInfo.InvariantCulture), Convert.ToInt64(d["clock"], System.Globalization.CultureInfo.InvariantCulture));
    }

    // ---- resolution ----

    /// <summary>
    /// Transforms this relative position into an absolute position in <paramref name="doc"/>.
    /// Returns null when the position cannot be resolved (e.g. the referenced item is not known yet or was GC'd).
    /// If you want to share the position with other users, set <paramref name="followUndoneDeletions"/> to false
    /// to get consistent results across clients.
    /// </summary>
    public AbsolutePosition? ToAbsolutePosition(Doc doc, bool followUndoneDeletions = true)
    {
        var store = doc.Store;
        AbstractType type;
        long index = 0;
        if (Item != null)
        {
            if (store.GetState(Item.Client) <= Item.Clock) return null;
            AbstractStruct rightStruct;
            long diff;
            if (followUndoneDeletions)
            {
                (rightStruct, diff) = DotYjs.Item.FollowRedone(store, Item);
            }
            else
            {
                rightStruct = store.Find(Item);
                diff = Item.Clock - rightStruct.Id.Clock;
            }
            if (rightStruct is not Item right) return null;
            type = (AbstractType)right.Parent!;
            if (type.Item == null || !type.Item.Deleted)
            {
                index = right.Deleted || !right.Countable ? 0 : diff + (Assoc >= 0 ? 0 : 1);
                for (var n = right.Left; n != null; n = n.Left)
                {
                    if (!n.Deleted && n.Countable) index += n.Length;
                }
            }
        }
        else
        {
            if (TName != null)
            {
                type = doc.Get(TName);
            }
            else if (Type != null)
            {
                if (store.GetState(Type.Client) <= Type.Clock) return null; // type does not exist yet
                var s = followUndoneDeletions ? DotYjs.Item.FollowRedone(store, Type).item : store.Find(Type);
                if (s is Item { Content: ContentType ct }) type = ct.Type;
                else return null; // struct is garbage collected
            }
            else
            {
                throw new InvalidOperationException("Invalid relative position: no type, tname or item.");
            }
            index = Assoc >= 0 ? type.LengthInternal : 0;
        }
        return new AbsolutePosition(type, index, Assoc);
    }

    public bool Equals(RelativePosition? other) =>
        other is not null && TName == other.TName && ID.Compare(Item, other.Item) && ID.Compare(Type, other.Type) && Assoc == other.Assoc;

    public override bool Equals(object? obj) => obj is RelativePosition rp && Equals(rp);
    public override int GetHashCode() => HashCode.Combine(Type, TName, Item, Assoc);
    public static bool Compare(RelativePosition? a, RelativePosition? b) => ReferenceEquals(a, b) || (a is not null && a.Equals(b));
}

/// <summary>An absolute position: a type and an index into it.</summary>
public sealed class AbsolutePosition
{
    public AbsolutePosition(AbstractType type, long index, long assoc = 0)
    {
        Type = type;
        Index = index;
        Assoc = assoc;
    }

    public AbstractType Type { get; }
    public long Index { get; }
    public long Assoc { get; }
}
