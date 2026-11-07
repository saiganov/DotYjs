using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace DotYjs.Lib0;

/// <summary>
/// Binary decoder compatible with lib0/decoding.
/// </summary>
public class Decoder
{
    private readonly byte[] _arr;
    private readonly int _start;
    private readonly int _end;
    private int _pos;

    public Decoder(byte[] data) : this(data, 0, data.Length) { }

    public Decoder(byte[] data, int offset, int length)
    {
        _arr = data;
        _start = offset;
        _pos = offset;
        _end = offset + length;
    }

    /// <summary>Current reading position relative to the beginning of the decoded segment.</summary>
    public int Position
    {
        get => _pos - _start;
        set => _pos = _start + value;
    }

    public bool HasContent => _pos < _end;

    private static InvalidDataException EndOfArray() => new InvalidDataException("Unexpected end of array");

    /// <summary>Integers are JavaScript numbers in lib0, so larger values cannot come from a valid message (Number.MAX_SAFE_INTEGER).</summary>
    private const ulong MaxSafeInteger = (1UL << 53) - 1;

    /// <summary>The number of bytes left to read.</summary>
    internal int Remaining => _end - _pos;

    /// <summary>Reads a var uint that is used as a length or count; it must fit into an <see cref="int"/>.</summary>
    internal int ReadLength()
    {
        long len = ReadVarUint();
        if (len > int.MaxValue) throw new InvalidDataException("Length out of range");
        return (int)len;
    }

    public byte ReadUint8()
    {
        if (_pos >= _end) throw EndOfArray();
        return _arr[_pos++];
    }

    public byte PeekUint8()
    {
        if (_pos >= _end) throw EndOfArray();
        return _arr[_pos];
    }

    public int ReadUint16()
    {
        int v = ReadUint8();
        v |= ReadUint8() << 8;
        return v;
    }

    public uint ReadUint32()
    {
        uint v = 0;
        for (int i = 0; i < 4; i++) v |= (uint)ReadUint8() << (8 * i);
        return v;
    }

    public uint ReadUint32BigEndian()
    {
        uint v = 0;
        for (int i = 0; i < 4; i++) v = (v << 8) | ReadUint8();
        return v;
    }

    /// <summary>Returns a copy of the next <paramref name="len"/> bytes.</summary>
    public byte[] ReadUint8Array(int len)
    {
        if (len < 0 || len > _end - _pos) throw EndOfArray();
        var res = _arr.AsSpan(_pos, len).ToArray();
        _pos += len;
        return res;
    }

    /// <summary>Returns a view over the next <paramref name="len"/> bytes without copying.</summary>
    public ReadOnlySpan<byte> ReadSpan(int len)
    {
        if (len < 0 || len > _end - _pos) throw EndOfArray();
        var res = _arr.AsSpan(_pos, len);
        _pos += len;
        return res;
    }

    /// <summary>Creates a sub-decoder over the next <paramref name="len"/> bytes (no copy).</summary>
    internal Decoder ReadSubDecoder(int len)
    {
        if (len < 0 || len > _end - _pos) throw EndOfArray();
        var d = new Decoder(_arr, _pos, len);
        _pos += len;
        return d;
    }

    public byte[] ReadVarUint8Array() => ReadUint8Array(ReadLength());

    public byte[] ReadTailAsUint8Array() => ReadUint8Array(_end - _pos);

    public long ReadVarUint()
    {
        ulong num = 0;
        int shift = 0;
        while (_pos < _end)
        {
            byte r = _arr[_pos++];
            num |= (ulong)(r & 0x7f) << shift;
            shift += 7;
            if (r < 0x80) return num <= MaxSafeInteger ? (long)num : throw new InvalidDataException("Integer out of Range");
            if (shift > 53) throw new InvalidDataException("Integer out of Range");
        }
        throw EndOfArray();
    }

    public long ReadVarInt() => ReadVarInt(out _);

    /// <summary>Reads a signed varint; <paramref name="isNegative"/> is true also for an encoded <c>-0</c>.</summary>
    public long ReadVarInt(out bool isNegative)
    {
        byte r = ReadUint8();
        ulong num = (ulong)(r & 0x3f);
        int shift = 6;
        isNegative = (r & 0x40) > 0;
        if ((r & 0x80) == 0) return isNegative ? -(long)num : (long)num;
        while (_pos < _end)
        {
            r = _arr[_pos++];
            num |= (ulong)(r & 0x7f) << shift;
            shift += 7;
            if (r < 0x80)
            {
                if (num > MaxSafeInteger) throw new InvalidDataException("Integer out of Range");
                return isNegative ? -(long)num : (long)num;
            }
            if (shift > 53) throw new InvalidDataException("Integer out of Range");
        }
        throw EndOfArray();
    }

    public long PeekVarUint()
    {
        int p = _pos;
        var v = ReadVarUint();
        _pos = p;
        return v;
    }

    public string ReadVarString()
    {
        int len = ReadLength();
        if (len > _end - _pos) throw EndOfArray();
        var s = Encoding.UTF8.GetString(_arr, _pos, len);
        _pos += len;
        return s;
    }

    public string PeekVarString()
    {
        int p = _pos;
        var v = ReadVarString();
        _pos = p;
        return v;
    }

    public byte[] ReadTerminatedUint8Array()
    {
        var e = new Encoder();
        while (true)
        {
            byte b = ReadUint8();
            if (b == 0) return e.ToArray();
            if (b == 1) b = ReadUint8();
            e.Write(b);
        }
    }

    public string ReadTerminatedString() => Encoding.UTF8.GetString(ReadTerminatedUint8Array());

    public float ReadFloat32() => BinaryPrimitives.ReadSingleBigEndian(ReadSpan(4));

    public double ReadFloat64() => BinaryPrimitives.ReadDoubleBigEndian(ReadSpan(8));

    public long ReadBigInt64() => BinaryPrimitives.ReadInt64BigEndian(ReadSpan(8));

    public ulong ReadBigUint64() => BinaryPrimitives.ReadUInt64BigEndian(ReadSpan(8));

    /// <summary>
    /// Reads a value encoded with the lib0 "any" encoding. Integers are returned as <see cref="long"/>, floats as
    /// <see cref="double"/>, objects as <see cref="Dictionary{TKey,TValue}"/> and arrays as <see cref="List{T}"/>.
    /// </summary>
    public object? ReadAny()
    {
        byte t = ReadUint8();
        switch (t)
        {
            case 127: return Undefined.Value;
            case 126: return null;
            case 125: return ReadVarInt();
            case 124: return (double)ReadFloat32();
            case 123: return ReadFloat64();
            case 122: return new BigInteger(ReadBigInt64());
            case 121: return false;
            case 120: return true;
            case 119: return ReadVarString();
            case 118:
                {
                    int len = ReadLength();
                    // every entry takes at least two bytes, so a corrupt length cannot reserve more memory than the input justifies
                    var obj = new Dictionary<string, object?>(Math.Min(len, Remaining / 2));
                    for (int i = 0; i < len; i++)
                    {
                        var key = ReadVarString();
                        obj[key] = ReadAny();
                    }
                    return obj;
                }
            case 117:
                {
                    int len = ReadLength();
                    var arr = new List<object?>(Math.Min(len, Remaining));
                    for (int i = 0; i < len; i++) arr.Add(ReadAny());
                    return arr;
                }
            case 116: return ReadVarUint8Array();
            default: throw new InvalidDataException($"Unknown any-type tag {t}");
        }
    }
}

internal sealed class RleDecoder
{
    private readonly Decoder _d;
    private byte _s;
    private long _count;

    public RleDecoder(Decoder d) { _d = d; }

    public byte Read()
    {
        if (_count == 0)
        {
            _s = _d.ReadUint8();
            _count = _d.HasContent ? _d.ReadVarUint() + 1 : -1;
        }
        _count--;
        return _s;
    }
}

internal sealed class UintOptRleDecoder
{
    private readonly Decoder _d;
    private long _s;
    private long _count;

    public UintOptRleDecoder(Decoder d) { _d = d; }

    public long Read()
    {
        if (_count == 0)
        {
            _s = _d.ReadVarInt(out bool neg);
            _count = 1;
            if (neg)
            {
                _s = -_s;
                _count = _d.ReadVarUint() + 2;
            }
        }
        _count--;
        return _s;
    }
}

internal sealed class IncUintOptRleDecoder
{
    private readonly Decoder _d;
    private long _s;
    private long _count;

    public IncUintOptRleDecoder(Decoder d) { _d = d; }

    public long Read()
    {
        if (_count == 0)
        {
            _s = _d.ReadVarInt(out bool neg);
            _count = 1;
            if (neg)
            {
                _s = -_s;
                _count = _d.ReadVarUint() + 2;
            }
        }
        _count--;
        return _s++;
    }
}

internal sealed class IntDiffOptRleDecoder
{
    private readonly Decoder _d;
    private long _s;
    private long _count;
    private long _diff;

    public IntDiffOptRleDecoder(Decoder d) { _d = d; }

    public long Read()
    {
        if (_count == 0)
        {
            long diff = _d.ReadVarInt();
            bool hasCount = (diff & 1) != 0;
            _diff = diff >> 1; // floor(diff / 2)
            _count = 1;
            if (hasCount) _count = _d.ReadVarUint() + 2;
        }
        _s += _diff;
        _count--;
        return _s;
    }
}

internal sealed class StringDecoder
{
    private readonly UintOptRleDecoder _lens;
    private readonly string _str;
    private int _spos;

    public StringDecoder(Decoder d)
    {
        _str = d.ReadVarString();
        _lens = new UintOptRleDecoder(d);
    }

    public string Read()
    {
        long len = _lens.Read();
        if (len < 0 || len > _str.Length - _spos) throw new InvalidDataException("Unexpected end of string");
        int end = _spos + (int)len;
        var res = _str.Substring(_spos, end - _spos);
        _spos = end;
        return res;
    }
}
