using DotYjs.Lib0;

namespace DotYjs;

/// <summary>Encoder for delete sets / state vectors (v1 or v2 flavour).</summary>
public interface IDSEncoder
{
    Encoder RestEncoder { get; }
    byte[] ToArray();
    void ResetDsCurVal();
    void WriteDsClock(long clock);
    void WriteDsLen(long len);
}

/// <summary>Encoder for document updates (v1 or v2 flavour).</summary>
public interface IUpdateEncoder : IDSEncoder
{
    void WriteLeftID(ID id);
    void WriteRightID(ID id);
    void WriteClient(long client);
    void WriteInfo(byte info);
    void WriteString(string s);
    void WriteParentInfo(bool isYKey);
    void WriteTypeRef(int info);
    void WriteLen(long len);
    void WriteAny(object? any);
    void WriteBuf(byte[] buf);
    void WriteJSON(object? embed);
    void WriteKey(string key);
}

public class DSEncoderV1 : IDSEncoder
{
    public Encoder RestEncoder { get; internal set; } = new();
    public virtual byte[] ToArray() => RestEncoder.ToArray();
    public void ResetDsCurVal() { }
    public void WriteDsClock(long clock) => RestEncoder.WriteVarUint(clock);
    public void WriteDsLen(long len) => RestEncoder.WriteVarUint(len);
}

public sealed class UpdateEncoderV1 : DSEncoderV1, IUpdateEncoder
{
    public void WriteLeftID(ID id)
    {
        RestEncoder.WriteVarUint(id.Client);
        RestEncoder.WriteVarUint(id.Clock);
    }

    public void WriteRightID(ID id)
    {
        RestEncoder.WriteVarUint(id.Client);
        RestEncoder.WriteVarUint(id.Clock);
    }

    public void WriteClient(long client) => RestEncoder.WriteVarUint(client);
    public void WriteInfo(byte info) => RestEncoder.Write(info);
    public void WriteString(string s) => RestEncoder.WriteVarString(s);
    public void WriteParentInfo(bool isYKey) => RestEncoder.WriteVarUint(isYKey ? 1 : 0);
    public void WriteTypeRef(int info) => RestEncoder.WriteVarUint(info);
    public void WriteLen(long len) => RestEncoder.WriteVarUint(len);
    public void WriteAny(object? any) => RestEncoder.WriteAny(any);
    public void WriteBuf(byte[] buf) => RestEncoder.WriteVarUint8Array(buf);
    public void WriteJSON(object? embed) => RestEncoder.WriteVarString(JsonValues.Stringify(embed));
    public void WriteKey(string key) => RestEncoder.WriteVarString(key);
}

public class DSEncoderV2 : IDSEncoder
{
    private long _dsCurrVal;
    public Encoder RestEncoder { get; internal set; } = new();
    public virtual byte[] ToArray() => RestEncoder.ToArray();
    public void ResetDsCurVal() => _dsCurrVal = 0;

    public void WriteDsClock(long clock)
    {
        long diff = clock - _dsCurrVal;
        _dsCurrVal = clock;
        RestEncoder.WriteVarUint(diff);
    }

    public void WriteDsLen(long len)
    {
        if (len == 0) throw new InvalidOperationException("Unexpected case");
        RestEncoder.WriteVarUint(len - 1);
        _dsCurrVal += len;
    }
}

public sealed class UpdateEncoderV2 : DSEncoderV2, IUpdateEncoder
{
    private readonly Dictionary<string, long> _keyMap = new();
    private long _keyClock;
    private readonly IntDiffOptRleEncoder _keyClockEncoder = new();
    private readonly UintOptRleEncoder _clientEncoder = new();
    private readonly IntDiffOptRleEncoder _leftClockEncoder = new();
    private readonly IntDiffOptRleEncoder _rightClockEncoder = new();
    private readonly RleEncoder _infoEncoder = new();
    private readonly StringEncoder _stringEncoder = new();
    private readonly RleEncoder _parentInfoEncoder = new();
    private readonly UintOptRleEncoder _typeRefEncoder = new();
    private readonly UintOptRleEncoder _lenEncoder = new();

    public override byte[] ToArray()
    {
        var e = new Encoder();
        e.WriteVarUint(0); // feature flag
        e.WriteVarUint8Array(_keyClockEncoder.ToArray());
        e.WriteVarUint8Array(_clientEncoder.ToArray());
        e.WriteVarUint8Array(_leftClockEncoder.ToArray());
        e.WriteVarUint8Array(_rightClockEncoder.ToArray());
        e.WriteVarUint8Array(_infoEncoder.ToArray());
        e.WriteVarUint8Array(_stringEncoder.ToArray());
        e.WriteVarUint8Array(_parentInfoEncoder.ToArray());
        e.WriteVarUint8Array(_typeRefEncoder.ToArray());
        e.WriteVarUint8Array(_lenEncoder.ToArray());
        e.WriteUint8Array(RestEncoder.AsSpan());
        return e.ToArray();
    }

    public void WriteLeftID(ID id)
    {
        _clientEncoder.Write(id.Client);
        _leftClockEncoder.Write(id.Clock);
    }

    public void WriteRightID(ID id)
    {
        _clientEncoder.Write(id.Client);
        _rightClockEncoder.Write(id.Clock);
    }

    public void WriteClient(long client) => _clientEncoder.Write(client);
    public void WriteInfo(byte info) => _infoEncoder.Write(info);
    public void WriteString(string s) => _stringEncoder.Write(s);
    public void WriteParentInfo(bool isYKey) => _parentInfoEncoder.Write(isYKey ? (byte)1 : (byte)0);
    public void WriteTypeRef(int info) => _typeRefEncoder.Write(info);
    public void WriteLen(long len) => _lenEncoder.Write(len);
    public void WriteAny(object? any) => RestEncoder.WriteAny(any);
    public void WriteBuf(byte[] buf) => RestEncoder.WriteVarUint8Array(buf);
    public void WriteJSON(object? embed) => RestEncoder.WriteAny(embed);

    public void WriteKey(string key)
    {
        if (_keyMap.TryGetValue(key, out var clock))
        {
            _keyClockEncoder.Write(clock);
        }
        else
        {
            // Note: yjs never stores the key in keyMap, so every key is re-sent. Mirror that for byte compatibility.
            _keyClockEncoder.Write(_keyClock++);
            _stringEncoder.Write(key);
        }
    }
}
