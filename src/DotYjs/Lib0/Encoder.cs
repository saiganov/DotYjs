using System.Buffers.Binary;
using System.Collections;
using System.Numerics;
using System.Text;

namespace DotYjs.Lib0;

/// <summary>
/// Binary encoder compatible with lib0/encoding (the encoding used by Yjs).
/// </summary>
public class Encoder
{
    private byte[] _buf;
    private int _pos;

    /// <summary>Creates an encoder with the given initial buffer size.</summary>
    public Encoder(int initialCapacity = 64)
    {
        _buf = new byte[Math.Max(initialCapacity, 16)];
    }

    /// <summary>Whether anything has been written.</summary>
    public bool HasContent => _pos > 0;

    /// <summary>Number of bytes written so far.</summary>
    public int Length => _pos;

    /// <summary>Returns a copy of the written bytes.</summary>
    public byte[] ToArray() => _buf.AsSpan(0, _pos).ToArray();

    /// <summary>Returns the written bytes without copying; valid until the next write.</summary>
    public ReadOnlySpan<byte> AsSpan() => _buf.AsSpan(0, _pos);

    private void Ensure(int extra)
    {
        if (_pos + extra > _buf.Length)
        {
            var nb = new byte[Math.Max(_buf.Length * 2, _pos + extra)];
            Buffer.BlockCopy(_buf, 0, nb, 0, _pos);
            _buf = nb;
        }
    }

    /// <summary>Writes one byte.</summary>
    public void Write(byte b)
    {
        if (_pos == _buf.Length) Ensure(1);
        _buf[_pos++] = b;
    }

    /// <summary>Writes the lowest byte of <paramref name="b"/>.</summary>
    public void WriteUint8(int b) => Write((byte)b);

    /// <summary>Writes a 16-bit unsigned integer (little endian).</summary>
    public void WriteUint16(int num)
    {
        Write((byte)(num & 0xff));
        Write((byte)((num >> 8) & 0xff));
    }

    /// <summary>Writes a 32-bit unsigned integer (little endian).</summary>
    public void WriteUint32(uint num)
    {
        for (int i = 0; i < 4; i++)
        {
            Write((byte)(num & 0xff));
            num >>= 8;
        }
    }

    /// <summary>Writes a 32-bit unsigned integer (big endian).</summary>
    public void WriteUint32BigEndian(uint num)
    {
        for (int i = 3; i >= 0; i--) Write((byte)((num >> (8 * i)) & 0xff));
    }

    /// <summary>Writes an unsigned variable-length integer (7 bits per byte, LEB128).</summary>
    public void WriteVarUint(long num)
    {
        if (num < 0) throw new ArgumentOutOfRangeException(nameof(num), "VarUint must be non-negative");
        ulong n = (ulong)num;
        while (n > 0x7f)
        {
            Write((byte)(0x80 | (n & 0x7f)));
            n >>= 7;
        }
        Write((byte)(n & 0x7f));
    }

    /// <summary>
    /// Writes a signed variable-length integer. The first byte carries a sign bit (bit 7) and 6 data bits.
    /// <paramref name="negativeZero"/> encodes JavaScript's <c>-0</c>, which lib0 uses as a flag in some RLE encoders.
    /// </summary>
    public void WriteVarInt(long num, bool negativeZero = false)
    {
        bool isNegative = num < 0 || (num == 0 && negativeZero);
        ulong n = num < 0 ? (ulong)(-num) : (ulong)num;
        Write((byte)((n > 0x3f ? 0x80 : 0) | (isNegative ? 0x40 : 0) | (byte)(n & 0x3f)));
        n >>= 6;
        while (n > 0)
        {
            Write((byte)((n > 0x7f ? 0x80 : 0) | (byte)(n & 0x7f)));
            n >>= 7;
        }
    }

    /// <summary>Writes the bytes as they are.</summary>
    public void WriteUint8Array(ReadOnlySpan<byte> data)
    {
        Ensure(data.Length);
        data.CopyTo(_buf.AsSpan(_pos));
        _pos += data.Length;
    }

    /// <summary>Writes a byte array prefixed with its length as a var uint.</summary>
    public void WriteVarUint8Array(ReadOnlySpan<byte> data)
    {
        WriteVarUint(data.Length);
        WriteUint8Array(data);
    }

    /// <summary>Writes a UTF-8 string prefixed with its byte length as a var uint (lib0 writeVarString).</summary>
    public void WriteVarString(string str)
    {
        int max = Encoding.UTF8.GetMaxByteCount(str.Length);
        if (max <= 1024)
        {
            Span<byte> tmp = stackalloc byte[max];
            int written = Encoding.UTF8.GetBytes(str, tmp);
            WriteVarUint(written);
            WriteUint8Array(tmp[..written]);
        }
        else
        {
            var bytes = Encoding.UTF8.GetBytes(str);
            WriteVarUint8Array(bytes);
        }
    }

    /// <summary>Writes a byte array terminated by 0, escaping the bytes 0 and 1 (lib0 writeTerminatedUint8Array).</summary>
    public void WriteTerminatedUint8Array(ReadOnlySpan<byte> buf)
    {
        foreach (var b in buf)
        {
            if (b == 0 || b == 1) Write(1);
            Write(b);
        }
        Write(0);
    }

    /// <summary>Writes a UTF-8 string terminated by 0 (lib0 writeTerminatedString).</summary>
    public void WriteTerminatedString(string str) => WriteTerminatedUint8Array(Encoding.UTF8.GetBytes(str));

    /// <summary>Writes a 32-bit float (big endian).</summary>
    public void WriteFloat32(float num)
    {
        Ensure(4);
        BinaryPrimitives.WriteSingleBigEndian(_buf.AsSpan(_pos), num);
        _pos += 4;
    }

    /// <summary>Writes a 64-bit float (big endian).</summary>
    public void WriteFloat64(double num)
    {
        Ensure(8);
        BinaryPrimitives.WriteDoubleBigEndian(_buf.AsSpan(_pos), num);
        _pos += 8;
    }

    /// <summary>Writes a 64-bit signed integer (big endian).</summary>
    public void WriteBigInt64(long num)
    {
        Ensure(8);
        BinaryPrimitives.WriteInt64BigEndian(_buf.AsSpan(_pos), num);
        _pos += 8;
    }

    /// <summary>Writes a 64-bit unsigned integer (big endian).</summary>
    public void WriteBigUint64(ulong num)
    {
        Ensure(8);
        BinaryPrimitives.WriteUInt64BigEndian(_buf.AsSpan(_pos), num);
        _pos += 8;
    }

    private const long Bits31 = 0x7fffffff;

    private void WriteNumber(double d)
    {
        // Mirrors JavaScript semantics: integers in 31-bit range -> varint, exact float32 -> float32, else float64.
        if (!double.IsNaN(d) && !double.IsInfinity(d) && Math.Floor(d) == d && Math.Abs(d) <= Bits31)
        {
            Write(125);
            WriteVarInt((long)d, d == 0 && double.IsNegative(d));
        }
        else if ((double)(float)d == d)
        {
            Write(124);
            WriteFloat32((float)d);
        }
        else
        {
            Write(123);
            WriteFloat64(d);
        }
    }

    private void WriteInteger(long v)
    {
        if (v >= -Bits31 && v <= Bits31)
        {
            Write(125);
            WriteVarInt(v);
        }
        else
        {
            WriteNumber(v);
        }
    }

    /// <summary>
    /// Writes a value using the lib0 "any" encoding. Supported values: null, <see cref="Undefined"/>, bool, numeric
    /// primitives, string, byte[], <see cref="BigInteger"/> (int64 on the wire), dictionaries with string keys and
    /// enumerables (arrays).
    /// </summary>
    public void WriteAny(object? data)
    {
        switch (data)
        {
            case null:
                Write(126);
                break;
            case Undefined:
                Write(127);
                break;
            case string s:
                Write(119);
                WriteVarString(s);
                break;
            case bool b:
                Write(b ? (byte)120 : (byte)121);
                break;
            case int i: WriteInteger(i); break;
            case long l: WriteInteger(l); break;
            case short sh: WriteInteger(sh); break;
            case sbyte sb: WriteInteger(sb); break;
            case byte by: WriteInteger(by); break;
            case ushort us: WriteInteger(us); break;
            case uint ui: WriteInteger(ui); break;
            case ulong ul:
                if (ul <= Bits31) WriteInteger((long)ul); else WriteNumber(ul);
                break;
            case float f: WriteNumber(f); break;
            case double d: WriteNumber(d); break;
            case decimal m: WriteNumber((double)m); break;
            case BigInteger bi:
                Write(122);
                WriteBigInt64((long)bi);
                break;
            case byte[] bytes:
                Write(116);
                WriteVarUint8Array(bytes);
                break;
            case ReadOnlyMemory<byte> rom:
                Write(116);
                WriteVarUint8Array(rom.Span);
                break;
            case System.Text.Json.JsonElement je:
                WriteAny(JsonValues.FromJsonElement(je));
                break;
            case System.Text.Json.Nodes.JsonNode jn:
                WriteAny(JsonValues.FromJsonNode(jn));
                break;
            case IDictionary<string, object?> dict:
                Write(118);
                WriteVarUint(dict.Count);
                foreach (var kv in dict)
                {
                    WriteVarString(kv.Key);
                    WriteAny(kv.Value);
                }
                break;
            case IDictionary nd:
                Write(118);
                WriteVarUint(nd.Count);
                foreach (DictionaryEntry kv in nd)
                {
                    WriteVarString(kv.Key.ToString()!);
                    WriteAny(kv.Value);
                }
                break;
            case IEnumerable en:
                {
                    var list = en as IList ?? en.Cast<object?>().ToList();
                    Write(117);
                    WriteVarUint(list.Count);
                    foreach (var item in list) WriteAny(item);
                    break;
                }
            default:
                throw new ArgumentException($"Unsupported value type for lib0 any-encoding: {data.GetType()}", nameof(data));
        }
    }
}

/// <summary>Represents JavaScript's <c>undefined</c> value.</summary>
public sealed class Undefined
{
    /// <summary>The single <c>undefined</c> value.</summary>
    public static readonly Undefined Value = new();
    private Undefined() { }
    /// <inheritdoc />
    public override string ToString() => "undefined";
}

/// <summary>Run-length encoder for byte values (lib0 RleEncoder with writeUint8).</summary>
internal sealed class RleEncoder
{
    private readonly Encoder _e = new();
    private int _s = -1;
    private long _count;

    public void Write(byte v)
    {
        if (_s == v)
        {
            _count++;
        }
        else
        {
            if (_count > 0) _e.WriteVarUint(_count - 1);
            _count = 1;
            _e.Write(v);
            _s = v;
        }
    }

    public byte[] ToArray() => _e.ToArray();
}

internal static class OptRle
{
    public static void Flush(Encoder e, long s, long count)
    {
        if (count > 0)
        {
            // single value: positive; several values: negative (incl. -0) followed by count-2
            if (count == 1) e.WriteVarInt(s);
            else e.WriteVarInt(-s, negativeZero: s == 0);
            if (count > 1) e.WriteVarUint(count - 2);
        }
    }
}

internal sealed class UintOptRleEncoder
{
    private readonly Encoder _e = new();
    private long _s;
    private long _count;

    public void Write(long v)
    {
        if (_s == v)
        {
            _count++;
        }
        else
        {
            OptRle.Flush(_e, _s, _count);
            _count = 1;
            _s = v;
        }
    }

    public byte[] ToArray()
    {
        OptRle.Flush(_e, _s, _count);
        // lib0 flushes without resetting; calling ToArray twice would duplicate. Reset to keep semantics sane.
        _count = 0;
        return _e.ToArray();
    }
}

internal sealed class IncUintOptRleEncoder
{
    private readonly Encoder _e = new();
    private long _s;
    private long _count;

    public void Write(long v)
    {
        if (_s + _count == v)
        {
            _count++;
        }
        else
        {
            OptRle.Flush(_e, _s, _count);
            _count = 1;
            _s = v;
        }
    }

    public byte[] ToArray()
    {
        OptRle.Flush(_e, _s, _count);
        _count = 0;
        return _e.ToArray();
    }
}

internal sealed class IntDiffOptRleEncoder
{
    private readonly Encoder _e = new();
    private long _s;
    private long _count;
    private long _diff;

    private void Flush()
    {
        if (_count > 0)
        {
            long encodedDiff = _diff * 2 + (_count == 1 ? 0 : 1);
            _e.WriteVarInt(encodedDiff);
            if (_count > 1) _e.WriteVarUint(_count - 2);
        }
    }

    public void Write(long v)
    {
        if (_diff == v - _s)
        {
            _s = v;
            _count++;
        }
        else
        {
            Flush();
            _count = 1;
            _diff = v - _s;
            _s = v;
        }
    }

    public byte[] ToArray()
    {
        Flush();
        _count = 0;
        return _e.ToArray();
    }
}

internal sealed class StringEncoder
{
    private readonly StringBuilder _sb = new();
    private readonly UintOptRleEncoder _lens = new();

    public void Write(string s)
    {
        _sb.Append(s);
        _lens.Write(s.Length);
    }

    public byte[] ToArray()
    {
        var e = new Encoder();
        e.WriteVarString(_sb.ToString());
        e.WriteUint8Array(_lens.ToArray());
        return e.ToArray();
    }
}
