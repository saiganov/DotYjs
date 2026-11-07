using DotYjs.Lib0;

namespace DotYjs;

/// <summary>Encoder for delete sets / state vectors (v1 or v2 flavour).</summary>
public interface IDSEncoder
{
    /// <summary>The underlying lib0 encoder.</summary>
    Encoder RestEncoder { get; }
    /// <summary>Returns the encoded bytes.</summary>
    byte[] ToArray();
    /// <summary>Resets the delta state before the next client of a delete set.</summary>
    void ResetDsCurVal();
    /// <summary>Writes the clock of a deleted range.</summary>
    void WriteDsClock(long clock);
    /// <summary>Writes the length of a deleted range.</summary>
    void WriteDsLen(long len);
}

/// <summary>Encoder for document updates (v1 or v2 flavour).</summary>
public interface IUpdateEncoder : IDSEncoder
{
    /// <summary>Writes the id of the left origin of an item.</summary>
    void WriteLeftID(ID id);
    /// <summary>Writes the id of the right origin of an item.</summary>
    void WriteRightID(ID id);
    /// <summary>Writes a client id.</summary>
    void WriteClient(long client);
    /// <summary>Writes the info byte of a struct (content type and flags).</summary>
    void WriteInfo(byte info);
    /// <summary>Writes a string.</summary>
    void WriteString(string s);
    /// <summary>Writes whether the parent is given as a root type name (true) or as an item id (false).</summary>
    void WriteParentInfo(bool isYKey);
    /// <summary>Writes the type reference of a shared type.</summary>
    void WriteTypeRef(int info);
    /// <summary>Writes a length.</summary>
    void WriteLen(long len);
    /// <summary>Writes a value in the lib0 any encoding.</summary>
    void WriteAny(object? any);
    /// <summary>Writes a byte array.</summary>
    void WriteBuf(byte[] buf);
    /// <summary>Writes a JSON value (embed).</summary>
    void WriteJSON(object? embed);
    /// <summary>Writes a map key or attribute name.</summary>
    void WriteKey(string key);
}

/// <summary>Writes delete sets and state vectors in the v1 encoding.</summary>
public class DSEncoderV1 : IDSEncoder
{
    /// <inheritdoc />
    public Encoder RestEncoder { get; internal set; } = new();
    /// <inheritdoc />
    public virtual byte[] ToArray() => RestEncoder.ToArray();
    /// <inheritdoc />
    public void ResetDsCurVal() { }
    /// <inheritdoc />
    public void WriteDsClock(long clock) => RestEncoder.WriteVarUint(clock);
    /// <inheritdoc />
    public void WriteDsLen(long len) => RestEncoder.WriteVarUint(len);
}

/// <summary>Writes updates in the v1 encoding.</summary>
public sealed class UpdateEncoderV1 : DSEncoderV1, IUpdateEncoder
{
    /// <inheritdoc />
    public void WriteLeftID(ID id)
    {
        RestEncoder.WriteVarUint(id.Client);
        RestEncoder.WriteVarUint(id.Clock);
    }

    /// <inheritdoc />
    public void WriteRightID(ID id)
    {
        RestEncoder.WriteVarUint(id.Client);
        RestEncoder.WriteVarUint(id.Clock);
    }

    /// <inheritdoc />
    public void WriteClient(long client) => RestEncoder.WriteVarUint(client);
    /// <inheritdoc />
    public void WriteInfo(byte info) => RestEncoder.Write(info);
    /// <inheritdoc />
    public void WriteString(string s) => RestEncoder.WriteVarString(s);
    /// <inheritdoc />
    public void WriteParentInfo(bool isYKey) => RestEncoder.WriteVarUint(isYKey ? 1 : 0);
    /// <inheritdoc />
    public void WriteTypeRef(int info) => RestEncoder.WriteVarUint(info);
    /// <inheritdoc />
    public void WriteLen(long len) => RestEncoder.WriteVarUint(len);
    /// <inheritdoc />
    public void WriteAny(object? any) => RestEncoder.WriteAny(any);
    /// <inheritdoc />
    public void WriteBuf(byte[] buf) => RestEncoder.WriteVarUint8Array(buf);
    /// <inheritdoc />
    public void WriteJSON(object? embed) => RestEncoder.WriteVarString(JsonValues.Stringify(embed));
    /// <inheritdoc />
    public void WriteKey(string key) => RestEncoder.WriteVarString(key);
}

/// <summary>Writes delete sets and state vectors in the v2 encoding.</summary>
public class DSEncoderV2 : IDSEncoder
{
    private long _dsCurrVal;
    /// <inheritdoc />
    public Encoder RestEncoder { get; internal set; } = new();
    /// <inheritdoc />
    public virtual byte[] ToArray() => RestEncoder.ToArray();
    /// <inheritdoc />
    public void ResetDsCurVal() => _dsCurrVal = 0;

    /// <inheritdoc />
    public void WriteDsClock(long clock)
    {
        long diff = clock - _dsCurrVal;
        _dsCurrVal = clock;
        RestEncoder.WriteVarUint(diff);
    }

    /// <inheritdoc />
    public void WriteDsLen(long len)
    {
        if (len == 0) throw new InvalidOperationException("Unexpected case");
        RestEncoder.WriteVarUint(len - 1);
        _dsCurrVal += len;
    }
}

/// <summary>Writes updates in the v2 encoding.</summary>
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

    /// <inheritdoc />
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

    /// <inheritdoc />
    public void WriteLeftID(ID id)
    {
        _clientEncoder.Write(id.Client);
        _leftClockEncoder.Write(id.Clock);
    }

    /// <inheritdoc />
    public void WriteRightID(ID id)
    {
        _clientEncoder.Write(id.Client);
        _rightClockEncoder.Write(id.Clock);
    }

    /// <inheritdoc />
    public void WriteClient(long client) => _clientEncoder.Write(client);
    /// <inheritdoc />
    public void WriteInfo(byte info) => _infoEncoder.Write(info);
    /// <inheritdoc />
    public void WriteString(string s) => _stringEncoder.Write(s);
    /// <inheritdoc />
    public void WriteParentInfo(bool isYKey) => _parentInfoEncoder.Write(isYKey ? (byte)1 : (byte)0);
    /// <inheritdoc />
    public void WriteTypeRef(int info) => _typeRefEncoder.Write(info);
    /// <inheritdoc />
    public void WriteLen(long len) => _lenEncoder.Write(len);
    /// <inheritdoc />
    public void WriteAny(object? any) => RestEncoder.WriteAny(any);
    /// <inheritdoc />
    public void WriteBuf(byte[] buf) => RestEncoder.WriteVarUint8Array(buf);
    /// <inheritdoc />
    public void WriteJSON(object? embed) => RestEncoder.WriteAny(embed);

    /// <inheritdoc />
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
