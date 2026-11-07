namespace DotYjs;

/// <summary>Base class of all structs stored in the <see cref="StructStore"/>: <see cref="Item"/>, <see cref="GC"/> and <see cref="Skip"/>.</summary>
public abstract class AbstractStruct
{
    protected AbstractStruct(ID id, long length)
    {
        Id = id;
        Length = length;
    }

    public ID Id { get; internal set; }
    public long Length { get; internal set; }

    public abstract bool Deleted { get; }

    internal virtual bool MergeWith(AbstractStruct right) => false;

    internal abstract void Write(IUpdateEncoder encoder, long offset);

    internal abstract void Integrate(Transaction transaction, long offset);

    internal virtual long? GetMissing(Transaction transaction, StructStore store) => null;
}

/// <summary>A garbage-collected range of deleted content.</summary>
public sealed class GC : AbstractStruct
{
    internal const int RefNumber = 0;

    public GC(ID id, long length) : base(id, length) { }

    public override bool Deleted => true;

    internal override bool MergeWith(AbstractStruct right)
    {
        if (right is not GC) return false;
        Length += right.Length;
        return true;
    }

    internal override void Integrate(Transaction transaction, long offset)
    {
        if (offset > 0)
        {
            Id.Clock += offset;
            Length -= offset;
        }
        transaction.Doc.Store.AddStruct(this);
    }

    internal override void Write(IUpdateEncoder encoder, long offset)
    {
        encoder.WriteInfo(RefNumber);
        encoder.WriteLen(Length - offset);
    }
}

/// <summary>Placeholder for a range of missing structs, only used while merging updates.</summary>
public sealed class Skip : AbstractStruct
{
    internal const int RefNumber = 10;

    public Skip(ID id, long length) : base(id, length) { }

    public override bool Deleted => true;

    internal override bool MergeWith(AbstractStruct right)
    {
        if (right is not Skip) return false;
        Length += right.Length;
        return true;
    }

    internal override void Integrate(Transaction transaction, long offset) => throw new InvalidOperationException("Unexpected case");

    internal override void Write(IUpdateEncoder encoder, long offset)
    {
        encoder.WriteInfo(RefNumber);
        encoder.RestEncoder.WriteVarUint(Length - offset);
    }
}
