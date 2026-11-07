using DotYjs.Lib0;

namespace DotYjs;

/// <summary>Base class for the content carried by an <see cref="Item"/>.</summary>
public abstract class AbstractContent
{
    /// <summary>The number of clocks this content covers.</summary>
    public abstract long GetLength();
    /// <summary>The values of this content as they appear in a type (empty for content that is not countable).</summary>
    public abstract IReadOnlyList<object?> GetContent();
    /// <summary>Whether this content counts towards the length of the type (formatting and deleted content do not).</summary>
    public abstract bool IsCountable { get; }
    internal abstract AbstractContent Copy();
    internal abstract AbstractContent Splice(long offset);
    internal abstract bool MergeWith(AbstractContent right);
    internal virtual void Integrate(Transaction transaction, Item item) { }
    internal virtual void Delete(Transaction transaction) { }
    internal virtual void Gc(StructStore store) { }
    internal abstract void Write(IUpdateEncoder encoder, long offset);
    internal abstract int Ref { get; }

    internal static AbstractContent Read(IUpdateDecoder decoder, byte info) => (info & 0x1f) switch
    {
        1 => new ContentDeleted(decoder.ReadLen()),
        2 => ContentJSON.Read(decoder),
        3 => new ContentBinary(decoder.ReadBuf()),
        4 => new ContentString(decoder.ReadString()),
        5 => new ContentEmbed(decoder.ReadJSON()),
        6 => new ContentFormat(decoder.ReadKey(), decoder.ReadJSON()),
        7 => new ContentType(AbstractType.ReadType(decoder)),
        8 => ContentAny.Read(decoder),
        9 => ContentDoc.Read(decoder),
        _ => throw new InvalidDataException($"Unknown content ref {info & 0x1f}"),
    };
}

/// <summary>Content with JSON-like values (an array or map value).</summary>
public sealed class ContentAny : AbstractContent
{
    /// <summary>Creates the content.</summary>
    public ContentAny(List<object?> arr) { Arr = arr; }
    /// <summary>The values.</summary>
    public List<object?> Arr { get; internal set; }
    /// <inheritdoc />
    public override long GetLength() => Arr.Count;
    /// <inheritdoc />
    public override IReadOnlyList<object?> GetContent() => Arr;
    /// <inheritdoc />
    public override bool IsCountable => true;
    internal override AbstractContent Copy() => new ContentAny(new List<object?>(Arr));

    internal override AbstractContent Splice(long offset)
    {
        int o = (int)offset;
        var right = new ContentAny(Arr.GetRange(o, Arr.Count - o));
        Arr = Arr.GetRange(0, o);
        return right;
    }

    internal override bool MergeWith(AbstractContent right)
    {
        Arr = new List<object?>(Arr.Concat(((ContentAny)right).Arr));
        return true;
    }

    internal override void Write(IUpdateEncoder encoder, long offset)
    {
        int len = Arr.Count;
        encoder.WriteLen(len - offset);
        for (int i = (int)offset; i < len; i++) encoder.WriteAny(Arr[i]);
    }

    internal override int Ref => 8;

    internal static ContentAny Read(IUpdateDecoder decoder)
    {
        long len = decoder.ReadLen();
        var cs = new List<object?>((int)len);
        for (long i = 0; i < len; i++) cs.Add(decoder.ReadAny());
        return new ContentAny(cs);
    }
}

/// <summary>Content with a byte array.</summary>
public sealed class ContentBinary : AbstractContent
{
    /// <summary>Creates the content.</summary>
    public ContentBinary(byte[] content) { Content = content; }
    /// <summary>The bytes.</summary>
    public byte[] Content { get; internal set; }
    /// <inheritdoc />
    public override long GetLength() => 1;
    /// <inheritdoc />
    public override IReadOnlyList<object?> GetContent() => new object?[] { Content };
    /// <inheritdoc />
    public override bool IsCountable => true;
    internal override AbstractContent Copy() => new ContentBinary(Content);
    internal override AbstractContent Splice(long offset) => throw new NotSupportedException();
    internal override bool MergeWith(AbstractContent right) => false;
    internal override void Write(IUpdateEncoder encoder, long offset) => encoder.WriteBuf(Content);
    internal override int Ref => 3;
}

/// <summary>Placeholder for deleted content that has been garbage collected.</summary>
public sealed class ContentDeleted : AbstractContent
{
    /// <summary>Creates the content.</summary>
    public ContentDeleted(long len) { Len = len; }
    /// <summary>The number of deleted elements.</summary>
    public long Len { get; internal set; }
    /// <inheritdoc />
    public override long GetLength() => Len;
    /// <inheritdoc />
    public override IReadOnlyList<object?> GetContent() => Array.Empty<object?>();
    /// <inheritdoc />
    public override bool IsCountable => false;
    internal override AbstractContent Copy() => new ContentDeleted(Len);

    internal override AbstractContent Splice(long offset)
    {
        var right = new ContentDeleted(Len - offset);
        Len = offset;
        return right;
    }

    internal override bool MergeWith(AbstractContent right)
    {
        Len += ((ContentDeleted)right).Len;
        return true;
    }

    internal override void Integrate(Transaction transaction, Item item)
    {
        transaction.DeleteSet.Add(item.Id.Client, item.Id.Clock, Len);
        item.MarkDeleted();
    }

    internal override void Write(IUpdateEncoder encoder, long offset) => encoder.WriteLen(Len - offset);
    internal override int Ref => 1;
}

/// <summary>Content holding a sub-document.</summary>
public sealed class ContentDoc : AbstractContent
{
    /// <summary>Creates the content for a subdocument.</summary>
    public ContentDoc(Doc doc)
    {
        if (doc.Item != null)
        {
            Console.Error.WriteLine("This document was already integrated as a sub-document. You should create a second instance instead with the same guid.");
        }
        Doc = doc;
        var opts = new Dictionary<string, object?>();
        if (!doc.Gc) opts["gc"] = false;
        if (doc.AutoLoad) opts["autoLoad"] = true;
        if (doc.Meta != null) opts["meta"] = doc.Meta;
        Opts = opts;
    }

    /// <summary>The subdocument.</summary>
    public Doc Doc { get; internal set; }
    /// <summary>The options of the subdocument that are stored with it (gc, autoLoad, meta).</summary>
    public Dictionary<string, object?> Opts { get; internal set; }

    internal static Doc CreateDocFromOpts(string guid, IDictionary<string, object?> opts)
    {
        var o = new DocOptions { Guid = guid };
        if (opts.TryGetValue("gc", out var gc) && gc is bool g) o.Gc = g;
        if (opts.TryGetValue("autoLoad", out var al) && al is bool a) o.AutoLoad = a;
        if (opts.TryGetValue("meta", out var meta)) o.Meta = meta;
        bool shouldLoad = (opts.TryGetValue("shouldLoad", out var sl) && sl is true) || o.AutoLoad;
        o.ShouldLoad = shouldLoad;
        return new Doc(o);
    }

    /// <inheritdoc />
    public override long GetLength() => 1;
    /// <inheritdoc />
    public override IReadOnlyList<object?> GetContent() => new object?[] { Doc };
    /// <inheritdoc />
    public override bool IsCountable => true;
    internal override AbstractContent Copy() => new ContentDoc(CreateDocFromOpts(Doc.Guid, Opts));
    internal override AbstractContent Splice(long offset) => throw new NotSupportedException();
    internal override bool MergeWith(AbstractContent right) => false;

    internal override void Integrate(Transaction transaction, Item item)
    {
        Doc.Item = item;
        transaction.SubdocsAdded.Add(Doc);
        if (Doc.ShouldLoad) transaction.SubdocsLoaded.Add(Doc);
    }

    internal override void Delete(Transaction transaction)
    {
        if (transaction.SubdocsAdded.Contains(Doc)) transaction.SubdocsAdded.Remove(Doc);
        else transaction.SubdocsRemoved.Add(Doc);
    }

    internal override void Write(IUpdateEncoder encoder, long offset)
    {
        encoder.WriteString(Doc.Guid);
        encoder.WriteAny(Opts);
    }

    internal override int Ref => 9;

    internal static ContentDoc Read(IUpdateDecoder decoder)
    {
        var guid = decoder.ReadString();
        var opts = decoder.ReadAny() as IDictionary<string, object?> ?? new Dictionary<string, object?>();
        return new ContentDoc(CreateDocFromOpts(guid, opts));
    }
}

/// <summary>An embedded object in a text (e.g. an image in a rich-text document).</summary>
public sealed class ContentEmbed : AbstractContent
{
    /// <summary>Creates the content.</summary>
    public ContentEmbed(object? embed) { Embed = embed; }
    /// <summary>The embedded value.</summary>
    public object? Embed { get; internal set; }
    /// <inheritdoc />
    public override long GetLength() => 1;
    /// <inheritdoc />
    public override IReadOnlyList<object?> GetContent() => new[] { Embed };
    /// <inheritdoc />
    public override bool IsCountable => true;
    internal override AbstractContent Copy() => new ContentEmbed(Embed);
    internal override AbstractContent Splice(long offset) => throw new NotSupportedException();
    internal override bool MergeWith(AbstractContent right) => false;
    internal override void Write(IUpdateEncoder encoder, long offset) => encoder.WriteJSON(Embed);
    internal override int Ref => 5;
}

/// <summary>A formatting marker in a text.</summary>
public sealed class ContentFormat : AbstractContent
{
    /// <summary>Creates the content.</summary>
    public ContentFormat(string key, object? value)
    {
        Key = key;
        Value = value;
    }

    /// <summary>The attribute name.</summary>
    public string Key { get; internal set; }
    /// <summary>The attribute value; null ends the formatting.</summary>
    public object? Value { get; internal set; }
    /// <inheritdoc />
    public override long GetLength() => 1;
    /// <inheritdoc />
    public override IReadOnlyList<object?> GetContent() => Array.Empty<object?>();
    /// <inheritdoc />
    public override bool IsCountable => false;
    internal override AbstractContent Copy() => new ContentFormat(Key, Value);
    internal override AbstractContent Splice(long offset) => throw new NotSupportedException();
    internal override bool MergeWith(AbstractContent right) => false;

    internal override void Integrate(Transaction transaction, Item item)
    {
        // the parent may still be an untyped placeholder (root type not yet requested as YText)
        var p = (AbstractType)item.Parent!;
        p.SearchMarker = null;
        if (p is YText yt) yt.HasFormatting = true;
    }

    internal override void Write(IUpdateEncoder encoder, long offset)
    {
        encoder.WriteKey(Key);
        encoder.WriteJSON(Value);
    }

    internal override int Ref => 6;
}

/// <summary>Legacy JSON content (ref 2). New documents use <see cref="ContentAny"/>.</summary>
public sealed class ContentJSON : AbstractContent
{
    /// <summary>Creates the content.</summary>
    public ContentJSON(List<object?> arr) { Arr = arr; }
    /// <summary>The values.</summary>
    public List<object?> Arr { get; internal set; }
    /// <inheritdoc />
    public override long GetLength() => Arr.Count;
    /// <inheritdoc />
    public override IReadOnlyList<object?> GetContent() => Arr;
    /// <inheritdoc />
    public override bool IsCountable => true;
    internal override AbstractContent Copy() => new ContentJSON(new List<object?>(Arr));

    internal override AbstractContent Splice(long offset)
    {
        int o = (int)offset;
        var right = new ContentJSON(Arr.GetRange(o, Arr.Count - o));
        Arr = Arr.GetRange(0, o);
        return right;
    }

    internal override bool MergeWith(AbstractContent right)
    {
        Arr = new List<object?>(Arr.Concat(((ContentJSON)right).Arr));
        return true;
    }

    internal override void Write(IUpdateEncoder encoder, long offset)
    {
        int len = Arr.Count;
        encoder.WriteLen(len - offset);
        for (int i = (int)offset; i < len; i++)
        {
            var c = Arr[i];
            encoder.WriteString(c is Undefined ? "undefined" : JsonValues.Stringify(c));
        }
    }

    internal override int Ref => 2;

    internal static ContentJSON Read(IUpdateDecoder decoder)
    {
        long len = decoder.ReadLen();
        var cs = new List<object?>((int)len);
        for (long i = 0; i < len; i++)
        {
            var c = decoder.ReadString();
            cs.Add(c == "undefined" ? Undefined.Value : JsonValues.Parse(c));
        }
        return new ContentJSON(cs);
    }
}

/// <summary>Text content.</summary>
public sealed class ContentString : AbstractContent
{
    /// <summary>Creates the content.</summary>
    public ContentString(string str) { Str = str; }
    /// <summary>The text. Its length is counted in UTF-16 code units, like in JavaScript.</summary>
    public string Str { get; internal set; }
    /// <inheritdoc />
    public override long GetLength() => Str.Length;

    /// <inheritdoc />
    public override IReadOnlyList<object?> GetContent()
    {
        var res = new object?[Str.Length];
        for (int i = 0; i < Str.Length; i++) res[i] = Str[i].ToString();
        return res;
    }

    /// <inheritdoc />
    public override bool IsCountable => true;
    internal override AbstractContent Copy() => new ContentString(Str);

    internal override AbstractContent Splice(long offset)
    {
        int o = (int)offset;
        var right = new ContentString(Str[o..]);
        Str = Str[..o];
        // prevent splitting a surrogate pair: replace broken halves with U+FFFD (same as yjs)
        if (o > 0)
        {
            char firstCharCode = Str[o - 1];
            if (firstCharCode >= 0xD800 && firstCharCode <= 0xDBFF)
            {
                Str = Str[..(o - 1)] + "�";
                right.Str = "�" + right.Str[1..];
            }
        }
        return right;
    }

    internal override bool MergeWith(AbstractContent right)
    {
        Str += ((ContentString)right).Str;
        return true;
    }

    internal override void Write(IUpdateEncoder encoder, long offset) => encoder.WriteString(offset == 0 ? Str : Str[(int)offset..]);
    internal override int Ref => 4;
}

/// <summary>Content holding a nested shared type.</summary>
public sealed class ContentType : AbstractContent
{
    /// <summary>Type reference of <see cref="YArray"/> in the binary encoding.</summary>
    public const int YArrayRefID = 0;
    /// <summary>Type reference of <see cref="YMap"/> in the binary encoding.</summary>
    public const int YMapRefID = 1;
    /// <summary>Type reference of <see cref="YText"/> in the binary encoding.</summary>
    public const int YTextRefID = 2;
    /// <summary>Type reference of <see cref="YXmlElement"/> in the binary encoding.</summary>
    public const int YXmlElementRefID = 3;
    /// <summary>Type reference of <see cref="YXmlFragment"/> in the binary encoding.</summary>
    public const int YXmlFragmentRefID = 4;
    /// <summary>Type reference of <see cref="YXmlHook"/> in the binary encoding.</summary>
    public const int YXmlHookRefID = 5;
    /// <summary>Type reference of <see cref="YXmlText"/> in the binary encoding.</summary>
    public const int YXmlTextRefID = 6;

    /// <summary>Creates the content.</summary>
    public ContentType(AbstractType type) { Type = type; }
    /// <summary>The nested shared type.</summary>
    public AbstractType Type { get; }
    /// <inheritdoc />
    public override long GetLength() => 1;
    /// <inheritdoc />
    public override IReadOnlyList<object?> GetContent() => new object?[] { Type };
    /// <inheritdoc />
    public override bool IsCountable => true;
    internal override AbstractContent Copy() => new ContentType(Type.InternalCopy());
    internal override AbstractContent Splice(long offset) => throw new NotSupportedException();
    internal override bool MergeWith(AbstractContent right) => false;

    internal override void Integrate(Transaction transaction, Item item) => Type.Integrate(transaction.Doc, item);

    internal override void Delete(Transaction transaction)
    {
        var item = Type.Start;
        while (item != null)
        {
            if (!item.Deleted) item.Delete(transaction);
            else if (item.Id.Clock < transaction.BeforeState.GetValueOrDefault(item.Id.Client)) transaction.MergeStructs.Add(item);
            item = item.Right;
        }
        foreach (var it in Type.Map.Values)
        {
            if (!it.Deleted) it.Delete(transaction);
            else if (it.Id.Clock < transaction.BeforeState.GetValueOrDefault(it.Id.Client)) transaction.MergeStructs.Add(it);
        }
        transaction.Changed.Remove(Type);
    }

    internal override void Gc(StructStore store)
    {
        var item = Type.Start;
        while (item != null)
        {
            item.Gc(store, true);
            item = item.Right;
        }
        Type.Start = null;
        foreach (var start in Type.Map.Values)
        {
            Item? it = start;
            while (it != null)
            {
                it.Gc(store, true);
                it = it.Left;
            }
        }
        Type.Map = new Dictionary<string, Item>();
    }

    internal override void Write(IUpdateEncoder encoder, long offset) => Type.WriteType(encoder);
    internal override int Ref => 7;
}
