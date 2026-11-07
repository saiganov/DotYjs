using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DotYjs.Lib0;

/// <summary>
/// Helpers that convert between JSON and the plain value model used by DotYjs
/// (null, bool, long, double, string, byte[], List&lt;object?&gt;, Dictionary&lt;string, object?&gt;),
/// and a JSON serializer that mimics JavaScript's <c>JSON.stringify</c>.
/// </summary>
public static class JsonValues
{
    /// <summary>Parses JSON that is part of binary data (an update or an awareness message); invalid JSON is reported as <see cref="InvalidDataException"/>.</summary>
    internal static object? ParseEncoded(string json)
    {
        try
        {
            return Parse(json);
        }
        catch (JsonException e)
        {
            throw new InvalidDataException("Invalid JSON: " + e.Message, e);
        }
    }

    public static object? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return FromJsonElement(doc.RootElement);
    }

    public static object? FromJsonElement(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return null;
            case JsonValueKind.True: return true;
            case JsonValueKind.False: return false;
            case JsonValueKind.String: return e.GetString();
            case JsonValueKind.Number:
                if (e.TryGetInt64(out var l)) return l;
                var d = e.GetDouble();
                if (Math.Floor(d) == d && Math.Abs(d) < 9007199254740992d) return (long)d;
                return d;
            case JsonValueKind.Array:
                {
                    var list = new List<object?>();
                    #pragma warning disable IDISP004 // struct enumerators consumed by foreach
                    foreach (var item in e.EnumerateArray()) list.Add(FromJsonElement(item));
                    return list;
                }
            case JsonValueKind.Object:
                {
                    var dict = new Dictionary<string, object?>();
                    foreach (var p in e.EnumerateObject()) dict[p.Name] = FromJsonElement(p.Value);
#pragma warning restore IDISP004
                    return dict;
                }
            default:
                throw new ArgumentException("Unsupported JSON value", nameof(e));
        }
    }

    public static object? FromJsonNode(JsonNode? node)
    {
        if (node is null) return null;
        return FromJsonElement(JsonSerializer.SerializeToElement(node));
    }

    /// <summary>
    /// Structural equality of two JSON-like values (lib0 <c>equalityDeep</c>): dictionaries by key/value,
    /// lists element-wise, numbers by numeric value, byte arrays by content.
    /// </summary>
    public static bool DeepEquals(object? a, object? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        switch (a)
        {
            case string sa: return b is string sb && sa == sb;
            case bool ba: return b is bool bb && ba == bb;
            case Undefined: return b is Undefined;
            case byte[] xa: return b is byte[] xb && xa.AsSpan().SequenceEqual(xb);
            case BigInteger bia: return b is BigInteger bib && bia == bib;
            case IDictionary<string, object?> da:
                if (b is not IDictionary<string, object?> db || da.Count != db.Count) return false;
                foreach (var (k, v) in da)
                {
                    if (!db.TryGetValue(k, out var bv) || !DeepEquals(v, bv)) return false;
                }
                return true;
            case IList<object?> la:
                if (b is not IList<object?> lb || la.Count != lb.Count) return false;
                for (int i = 0; i < la.Count; i++)
                {
                    if (!DeepEquals(la[i], lb[i])) return false;
                }
                return true;
        }
        if (IsNumber(a) && IsNumber(b)) return Convert.ToDouble(a, CultureInfo.InvariantCulture) == Convert.ToDouble(b, CultureInfo.InvariantCulture);
        return a.Equals(b);
    }

    private static bool IsNumber(object o) => o is int or long or short or sbyte or byte or ushort or uint or ulong or float or double or decimal;

    /// <summary>Serializes a value like JavaScript's <c>JSON.stringify</c> (no whitespace, JS number formatting).</summary>
    public static string Stringify(object? value)
    {
        var sb = new StringBuilder();
        Write(sb, value, topLevel: true);
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, object? value, bool topLevel = false)
    {
        switch (value)
        {
            case null:
                sb.Append("null");
                break;
            case Undefined:
                // JSON.stringify(undefined) === undefined at the top level, null inside arrays.
                sb.Append(topLevel ? "undefined" : "null");
                break;
            case string s:
                WriteString(sb, s);
                break;
            case bool b:
                sb.Append(b ? "true" : "false");
                break;
            case int or long or short or sbyte or byte or ushort or uint or ulong:
                sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                break;
            case float f:
                sb.Append(FormatNumber(f));
                break;
            case double d:
                sb.Append(FormatNumber(d));
                break;
            case decimal m:
                sb.Append(FormatNumber((double)m));
                break;
            case BigInteger:
                throw new ArgumentException("Do not know how to serialize a BigInt", nameof(value));
            case byte[] bytes:
                // JSON.stringify(Uint8Array) produces an object with index keys
                sb.Append('{');
                for (int i = 0; i < bytes.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"').Append(i.ToString(CultureInfo.InvariantCulture)).Append("\":").Append(bytes[i].ToString(CultureInfo.InvariantCulture));
                }
                sb.Append('}');
                break;
            case JsonElement je:
                Write(sb, FromJsonElement(je), topLevel);
                break;
            case JsonNode jn:
                Write(sb, FromJsonNode(jn), topLevel);
                break;
            case IDictionary<string, object?> dict:
                {
                    sb.Append('{');
                    bool first = true;
                    foreach (var kv in dict)
                    {
                        if (kv.Value is Undefined) continue;
                        if (!first) sb.Append(',');
                        first = false;
                        WriteString(sb, kv.Key);
                        sb.Append(':');
                        Write(sb, kv.Value);
                    }
                    sb.Append('}');
                    break;
                }
            case IDictionary nd:
                {
                    sb.Append('{');
                    bool first = true;
                    foreach (DictionaryEntry kv in nd)
                    {
                        if (kv.Value is Undefined) continue;
                        if (!first) sb.Append(',');
                        first = false;
                        WriteString(sb, kv.Key.ToString()!);
                        sb.Append(':');
                        Write(sb, kv.Value);
                    }
                    sb.Append('}');
                    break;
                }
            case IEnumerable en:
                {
                    sb.Append('[');
                    bool first = true;
                    foreach (var item in en)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        Write(sb, item);
                    }
                    sb.Append(']');
                    break;
                }
            default:
                throw new ArgumentException($"Unsupported value type for JSON: {value.GetType()}", nameof(value));
        }
    }

    /// <summary>Formats a number the way JavaScript's Number.prototype.toString does.</summary>
    public static string FormatNumber(double d)
    {
        if (double.IsNaN(d) || double.IsInfinity(d)) return "null";
        if (d == 0) return "0";
        var r = d.ToString("R", CultureInfo.InvariantCulture);
        int ePos = r.IndexOf('E');
        string mantissa = ePos >= 0 ? r[..ePos] : r;
        int exp = ePos >= 0 ? int.Parse(r[(ePos + 1)..], CultureInfo.InvariantCulture) : 0;
        bool neg = mantissa.StartsWith('-');
        if (neg) mantissa = mantissa[1..];
        // digits and decimal exponent n such that value = 0.digits * 10^n
        int dot = mantissa.IndexOf('.');
        string intPart = dot >= 0 ? mantissa[..dot] : mantissa;
        string fracPart = dot >= 0 ? mantissa[(dot + 1)..] : "";
        string digits = (intPart + fracPart).TrimStart('0');
        int leadingZeros = (intPart + fracPart).Length - (intPart + fracPart).TrimStart('0').Length;
        int n = intPart.Length + exp - leadingZeros;
        digits = digits.TrimEnd('0');
        if (digits.Length == 0) return "0";
        int k = digits.Length;
        var sb = new StringBuilder();
        if (neg) sb.Append('-');
        if (k <= n && n <= 21)
        {
            sb.Append(digits).Append('0', n - k);
        }
        else if (0 < n && n <= 21)
        {
            sb.Append(digits, 0, n).Append('.').Append(digits, n, k - n);
        }
        else if (-6 < n && n <= 0)
        {
            sb.Append("0.").Append('0', -n).Append(digits);
        }
        else
        {
            int e = n - 1;
            sb.Append(digits[0]);
            if (k > 1) sb.Append('.').Append(digits, 1, k - 1);
            sb.Append('e').Append(e >= 0 ? "+" : "-").Append(Math.Abs(e).ToString(CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    private static void WriteString(StringBuilder sb, string s)
    {
        sb.Append('"');
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else if (char.IsHighSurrogate(c))
                    {
                        if (i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                        {
                            sb.Append(c).Append(s[i + 1]);
                            i++;
                        }
                        else
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                    }
                    else if (char.IsLowSurrogate(c))
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
        sb.Append('"');
    }
}
