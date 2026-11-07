using DotYjs.Lib0;

namespace DotYjs;

/// <summary>Reads delete sets and state vectors (v1 or v2 encoding).</summary>
public interface IDSDecoder
{
    /// <summary>The underlying lib0 decoder.</summary>
    Decoder RestDecoder { get; }
    /// <summary>Resets the delta state before the next client of a delete set.</summary>
    void ResetDsCurVal();
    /// <summary>Reads the clock of a deleted range.</summary>
    long ReadDsClock();
    /// <summary>Reads the length of a deleted range.</summary>
    long ReadDsLen();
}

/// <summary>Reads updates (v1 or v2 encoding).</summary>
public interface IUpdateDecoder : IDSDecoder
{
    /// <summary>Reads the id of the left origin of an item.</summary>
    ID ReadLeftID();
    /// <summary>Reads the id of the right origin of an item.</summary>
    ID ReadRightID();
    /// <summary>Reads a client id.</summary>
    long ReadClient();
    /// <summary>Reads the info byte of a struct (content type and flags).</summary>
    byte ReadInfo();
    /// <summary>Reads a string.</summary>
    string ReadString();
    /// <summary>Reads whether the parent is given as a root type name (true) or as an item id (false).</summary>
    bool ReadParentInfo();
    /// <summary>Reads the type reference of a shared type.</summary>
    int ReadTypeRef();
    /// <summary>Reads a length.</summary>
    long ReadLen();
    /// <summary>Reads a value in the lib0 any encoding.</summary>
    object? ReadAny();
    /// <summary>Reads a byte array.</summary>
    byte[] ReadBuf();
    /// <summary>Reads a JSON value (embed or legacy JSON content).</summary>
    object? ReadJSON();
    /// <summary>Reads a map key or attribute name.</summary>
    string ReadKey();
}

/// <summary>Reads delete sets and state vectors in the v1 encoding.</summary>
public class DSDecoderV1 : IDSDecoder
{
    /// <summary>Creates the decoder.</summary>
    public DSDecoderV1(Decoder decoder) { RestDecoder = decoder; }
    /// <inheritdoc />
    public Decoder RestDecoder { get; }
    /// <inheritdoc />
    public void ResetDsCurVal() { }
    /// <inheritdoc />
    public long ReadDsClock() => RestDecoder.ReadVarUint();
    /// <inheritdoc />
    public long ReadDsLen() => RestDecoder.ReadVarUint();
}

/// <summary>Reads updates in the v1 encoding.</summary>
public sealed class UpdateDecoderV1 : DSDecoderV1, IUpdateDecoder
{
    /// <summary>Creates the decoder.</summary>
    public UpdateDecoderV1(Decoder decoder) : base(decoder) { }

    /// <inheritdoc />
    public ID ReadLeftID() => new(RestDecoder.ReadVarUint(), RestDecoder.ReadVarUint());
    /// <inheritdoc />
    public ID ReadRightID() => new(RestDecoder.ReadVarUint(), RestDecoder.ReadVarUint());
    /// <inheritdoc />
    public long ReadClient() => RestDecoder.ReadVarUint();
    /// <inheritdoc />
    public byte ReadInfo() => RestDecoder.ReadUint8();
    /// <inheritdoc />
    public string ReadString() => RestDecoder.ReadVarString();
    /// <inheritdoc />
    public bool ReadParentInfo() => RestDecoder.ReadVarUint() == 1;
    /// <inheritdoc />
    public int ReadTypeRef() => (int)RestDecoder.ReadVarUint();
    /// <inheritdoc />
    public long ReadLen() => RestDecoder.ReadVarUint();
    /// <inheritdoc />
    public object? ReadAny() => RestDecoder.ReadAny();
    /// <inheritdoc />
    public byte[] ReadBuf() => RestDecoder.ReadVarUint8Array();
    /// <inheritdoc />
    public object? ReadJSON() => JsonValues.Parse(RestDecoder.ReadVarString());
    /// <inheritdoc />
    public string ReadKey() => RestDecoder.ReadVarString();
}

/// <summary>Reads delete sets and state vectors in the v2 encoding.</summary>
public class DSDecoderV2 : IDSDecoder
{
    private long _dsCurrVal;
    /// <summary>Creates the decoder.</summary>
    public DSDecoderV2(Decoder decoder) { RestDecoder = decoder; }
    /// <inheritdoc />
    public Decoder RestDecoder { get; }
    /// <inheritdoc />
    public void ResetDsCurVal() => _dsCurrVal = 0;

    /// <inheritdoc />
    public long ReadDsClock()
    {
        _dsCurrVal += RestDecoder.ReadVarUint();
        return _dsCurrVal;
    }

    /// <inheritdoc />
    public long ReadDsLen()
    {
        long diff = RestDecoder.ReadVarUint() + 1;
        _dsCurrVal += diff;
        return diff;
    }
}

/// <summary>Reads updates in the v2 encoding.</summary>
public sealed class UpdateDecoderV2 : DSDecoderV2, IUpdateDecoder
{
    private readonly List<string> _keys = new();
    private readonly IntDiffOptRleDecoder _keyClockDecoder;
    private readonly UintOptRleDecoder _clientDecoder;
    private readonly IntDiffOptRleDecoder _leftClockDecoder;
    private readonly IntDiffOptRleDecoder _rightClockDecoder;
    private readonly RleDecoder _infoDecoder;
    private readonly StringDecoder _stringDecoder;
    private readonly RleDecoder _parentInfoDecoder;
    private readonly UintOptRleDecoder _typeRefDecoder;
    private readonly UintOptRleDecoder _lenDecoder;

    /// <summary>Creates the decoder; reads the column decoders from the start of the update.</summary>
    public UpdateDecoderV2(Decoder decoder) : base(decoder)
    {
        decoder.ReadVarUint(); // feature flag, currently unused
        _keyClockDecoder = new IntDiffOptRleDecoder(Sub(decoder));
        _clientDecoder = new UintOptRleDecoder(Sub(decoder));
        _leftClockDecoder = new IntDiffOptRleDecoder(Sub(decoder));
        _rightClockDecoder = new IntDiffOptRleDecoder(Sub(decoder));
        _infoDecoder = new RleDecoder(Sub(decoder));
        _stringDecoder = new StringDecoder(Sub(decoder));
        _parentInfoDecoder = new RleDecoder(Sub(decoder));
        _typeRefDecoder = new UintOptRleDecoder(Sub(decoder));
        _lenDecoder = new UintOptRleDecoder(Sub(decoder));
    }

    private static Decoder Sub(Decoder d) => d.ReadSubDecoder(checked((int)d.ReadVarUint()));

    /// <inheritdoc />
    public ID ReadLeftID() => new(_clientDecoder.Read(), _leftClockDecoder.Read());
    /// <inheritdoc />
    public ID ReadRightID() => new(_clientDecoder.Read(), _rightClockDecoder.Read());
    /// <inheritdoc />
    public long ReadClient() => _clientDecoder.Read();
    /// <inheritdoc />
    public byte ReadInfo() => _infoDecoder.Read();
    /// <inheritdoc />
    public string ReadString() => _stringDecoder.Read();
    /// <inheritdoc />
    public bool ReadParentInfo() => _parentInfoDecoder.Read() == 1;
    /// <inheritdoc />
    public int ReadTypeRef() => (int)_typeRefDecoder.Read();
    /// <inheritdoc />
    public long ReadLen() => _lenDecoder.Read();
    /// <inheritdoc />
    public object? ReadAny() => RestDecoder.ReadAny();
    /// <inheritdoc />
    public byte[] ReadBuf() => RestDecoder.ReadVarUint8Array();
    /// <inheritdoc />
    public object? ReadJSON() => RestDecoder.ReadAny();

    /// <inheritdoc />
    public string ReadKey()
    {
        long keyClock = _keyClockDecoder.Read();
        if (keyClock < _keys.Count) return _keys[(int)keyClock];
        var key = _stringDecoder.Read();
        _keys.Add(key);
        return key;
    }
}
