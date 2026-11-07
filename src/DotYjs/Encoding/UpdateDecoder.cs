using DotYjs.Lib0;

namespace DotYjs;

public interface IDSDecoder
{
    Decoder RestDecoder { get; }
    void ResetDsCurVal();
    long ReadDsClock();
    long ReadDsLen();
}

public interface IUpdateDecoder : IDSDecoder
{
    ID ReadLeftID();
    ID ReadRightID();
    long ReadClient();
    byte ReadInfo();
    string ReadString();
    bool ReadParentInfo();
    int ReadTypeRef();
    long ReadLen();
    object? ReadAny();
    byte[] ReadBuf();
    object? ReadJSON();
    string ReadKey();
}

public class DSDecoderV1 : IDSDecoder
{
    public DSDecoderV1(Decoder decoder) { RestDecoder = decoder; }
    public Decoder RestDecoder { get; }
    public void ResetDsCurVal() { }
    public long ReadDsClock() => RestDecoder.ReadVarUint();
    public long ReadDsLen() => RestDecoder.ReadVarUint();
}

public sealed class UpdateDecoderV1 : DSDecoderV1, IUpdateDecoder
{
    public UpdateDecoderV1(Decoder decoder) : base(decoder) { }

    public ID ReadLeftID() => new(RestDecoder.ReadVarUint(), RestDecoder.ReadVarUint());
    public ID ReadRightID() => new(RestDecoder.ReadVarUint(), RestDecoder.ReadVarUint());
    public long ReadClient() => RestDecoder.ReadVarUint();
    public byte ReadInfo() => RestDecoder.ReadUint8();
    public string ReadString() => RestDecoder.ReadVarString();
    public bool ReadParentInfo() => RestDecoder.ReadVarUint() == 1;
    public int ReadTypeRef() => (int)RestDecoder.ReadVarUint();
    public long ReadLen() => RestDecoder.ReadVarUint();
    public object? ReadAny() => RestDecoder.ReadAny();
    public byte[] ReadBuf() => RestDecoder.ReadVarUint8Array();
    public object? ReadJSON() => JsonValues.ParseEncoded(RestDecoder.ReadVarString());
    public string ReadKey() => RestDecoder.ReadVarString();
}

public class DSDecoderV2 : IDSDecoder
{
    private long _dsCurrVal;
    public DSDecoderV2(Decoder decoder) { RestDecoder = decoder; }
    public Decoder RestDecoder { get; }
    public void ResetDsCurVal() => _dsCurrVal = 0;

    public long ReadDsClock()
    {
        _dsCurrVal += RestDecoder.ReadVarUint();
        return _dsCurrVal;
    }

    public long ReadDsLen()
    {
        long diff = RestDecoder.ReadVarUint() + 1;
        _dsCurrVal += diff;
        return diff;
    }
}

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

    private static Decoder Sub(Decoder d) => d.ReadSubDecoder(d.ReadLength());

    public ID ReadLeftID() => new(_clientDecoder.Read(), _leftClockDecoder.Read());
    public ID ReadRightID() => new(_clientDecoder.Read(), _rightClockDecoder.Read());
    public long ReadClient() => _clientDecoder.Read();
    public byte ReadInfo() => _infoDecoder.Read();
    public string ReadString() => _stringDecoder.Read();
    public bool ReadParentInfo() => _parentInfoDecoder.Read() == 1;
    public int ReadTypeRef() => (int)_typeRefDecoder.Read();
    public long ReadLen() => _lenDecoder.Read();
    public object? ReadAny() => RestDecoder.ReadAny();
    public byte[] ReadBuf() => RestDecoder.ReadVarUint8Array();
    public object? ReadJSON() => RestDecoder.ReadAny();

    public string ReadKey()
    {
        long keyClock = _keyClockDecoder.Read();
        if (keyClock < 0) throw new InvalidDataException("Invalid key reference");
        if (keyClock < _keys.Count) return _keys[(int)keyClock];
        var key = _stringDecoder.Read();
        _keys.Add(key);
        return key;
    }
}
