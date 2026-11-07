using DotYjs.Lib0;

namespace DotYjs;

/// <summary>Unique identifier of an inserted element: (client, clock).</summary>
public sealed class ID : IEquatable<ID>
{
    /// <summary>Creates an id.</summary>
    public ID(long client, long clock)
    {
        Client = client;
        Clock = clock;
    }

    /// <summary>The id of the client that created the element.</summary>
    public long Client { get; internal set; }
    /// <summary>The position of the element in the sequence of elements created by this client.</summary>
    public long Clock { get; internal set; }

    /// <inheritdoc />
    public bool Equals(ID? other) => other is not null && other.Client == Client && other.Clock == Clock;
    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ID id && Equals(id);
    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Client, Clock);
    /// <inheritdoc />
    public override string ToString() => $"({Client},{Clock})";

    /// <summary>Equality that treats two nulls as equal (yjs compareIDs).</summary>
    public static bool Compare(ID? a, ID? b) => ReferenceEquals(a, b) || (a is not null && b is not null && a.Client == b.Client && a.Clock == b.Clock);

    internal static void Write(Encoder encoder, ID id)
    {
        encoder.WriteVarUint(id.Client);
        encoder.WriteVarUint(id.Clock);
    }

    internal static ID Read(Decoder decoder) => new(decoder.ReadVarUint(), decoder.ReadVarUint());

    internal static string FindRootTypeKey(AbstractType type)
    {
        foreach (var kv in type.Doc!.Share)
        {
            if (ReferenceEquals(kv.Value, type)) return kv.Key;
        }
        throw new InvalidOperationException("Unexpected case");
    }
}
