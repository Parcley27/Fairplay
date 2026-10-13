using System.Buffers.Binary;
using System.Text;

namespace Fairplay.Plist;

/// <summary>
/// Minimal Apple binary property list ("bplist00") reader and writer.
/// Values map to: Dictionary&lt;string, object?&gt;, List&lt;object?&gt;, string, long, ulong, double,
/// bool, byte[], DateTime, null.
/// </summary>
public static class BinaryPlist
{
    // ---------------------------------------------------------------- writer

    public static byte[] Write(object? root)
    {
        var objects = new List<object?>();
        Flatten(root, objects);
        int count = objects.Count;
        int refSize = count < 256 ? 1 : count < 65536 ? 2 : 4;

        var body = new MemoryStream();
        body.Write("bplist00"u8);
        var offsets = new long[count];
        for (int i = 0; i < count; i++)
        {
            offsets[i] = body.Position;
            WriteObject(body, objects[i], refSize);
        }

        long offsetTableOffset = body.Position;
        int offsetSize = offsetTableOffset < 256 ? 1 : offsetTableOffset < 65536 ? 2 : offsetTableOffset < 0x100000000 ? 4 : 8;
        foreach (var off in offsets) WriteSized(body, (ulong)off, offsetSize);

        Span<byte> trailer = stackalloc byte[32];
        trailer.Clear();
        trailer[6] = (byte)offsetSize;
        trailer[7] = (byte)refSize;
        BinaryPrimitives.WriteUInt64BigEndian(trailer[8..], (ulong)count);
        BinaryPrimitives.WriteUInt64BigEndian(trailer[16..], 0);
        BinaryPrimitives.WriteUInt64BigEndian(trailer[24..], (ulong)offsetTableOffset);
        body.Write(trailer);
        return body.ToArray();
    }

    // Objects are laid out depth-first: a container is appended first, then its children,
    // and the container records the child indexes it must reference.
    sealed class Container
    {
        public required object Value;
        public required int[] Refs;
    }

    static int Flatten(object? value, List<object?> list)
    {
        int index = list.Count;
        switch (value)
        {
            case IDictionary<string, object?> dict:
            {
                var c = new Container { Value = value, Refs = new int[dict.Count * 2] };
                list.Add(c);
                int n = 0;
                var keys = dict.Keys.ToList();
                foreach (var k in keys) c.Refs[n++] = Flatten(k, list);
                foreach (var k in keys) c.Refs[n++] = Flatten(dict[k], list);
                return index;
            }
            case System.Collections.IList arr when value is not byte[]:
            {
                var c = new Container { Value = value, Refs = new int[arr.Count] };
                list.Add(c);
                for (int i = 0; i < arr.Count; i++) c.Refs[i] = Flatten(arr[i], list);
                return index;
            }
            default:
                list.Add(value);
                return index;
        }
    }

    static void WriteObject(Stream s, object? obj, int refSize)
    {
        switch (obj)
        {
            case null: s.WriteByte(0x00); break;
            case bool b: s.WriteByte(b ? (byte)0x09 : (byte)0x08); break;
            case Container c when c.Value is IDictionary<string, object?>:
                WriteMarker(s, 0xD, c.Refs.Length / 2);
                foreach (var r in c.Refs) WriteSized(s, (ulong)r, refSize);
                break;
            case Container c:
                WriteMarker(s, 0xA, c.Refs.Length);
                foreach (var r in c.Refs) WriteSized(s, (ulong)r, refSize);
                break;
            case string str:
                if (str.All(ch => ch < 128))
                {
                    WriteMarker(s, 0x5, str.Length);
                    s.Write(Encoding.ASCII.GetBytes(str));
                }
                else
                {
                    WriteMarker(s, 0x6, str.Length);
                    s.Write(Encoding.BigEndianUnicode.GetBytes(str));
                }
                break;
            case byte[] data:
                WriteMarker(s, 0x4, data.Length);
                s.Write(data);
                break;
            case double d:
                s.WriteByte(0x23);
                Span<byte> db = stackalloc byte[8];
                BinaryPrimitives.WriteDoubleBigEndian(db, d);
                s.Write(db);
                break;
            case float f:
                s.WriteByte(0x23);
                Span<byte> fb = stackalloc byte[8];
                BinaryPrimitives.WriteDoubleBigEndian(fb, f);
                s.Write(fb);
                break;
            case ulong ul when ul > long.MaxValue:
                s.WriteByte(0x14); // 16-byte int, upper 8 bytes zero
                Span<byte> ub = stackalloc byte[16];
                ub.Clear();
                BinaryPrimitives.WriteUInt64BigEndian(ub[8..], ul);
                s.Write(ub);
                break;
            case DateTime dt:
                s.WriteByte(0x33);
                Span<byte> dtb = stackalloc byte[8];
                BinaryPrimitives.WriteDoubleBigEndian(dtb, (dt.ToUniversalTime() - new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds);
                s.Write(dtb);
                break;
            default:
                if (obj is IConvertible conv && IsInteger(obj))
                {
                    WriteInt(s, conv.ToInt64(null));
                    break;
                }
                throw new NotSupportedException($"plist: unsupported type {obj.GetType()}");
        }
    }

    static bool IsInteger(object o) => o is byte or sbyte or short or ushort or int or uint or long or ulong;

    static void WriteInt(Stream s, long v)
    {
        if (v >= 0 && v <= 0xFF) { s.WriteByte(0x10); s.WriteByte((byte)v); }
        else if (v >= 0 && v <= 0xFFFF) { s.WriteByte(0x11); WriteSized(s, (ulong)v, 2); }
        else if (v >= 0 && v <= 0xFFFFFFFF) { s.WriteByte(0x12); WriteSized(s, (ulong)v, 4); }
        else { s.WriteByte(0x13); WriteSized(s, (ulong)v, 8); }
    }

    static void WriteMarker(Stream s, int type, int length)
    {
        if (length < 15) s.WriteByte((byte)((type << 4) | length));
        else
        {
            s.WriteByte((byte)((type << 4) | 0xF));
            WriteInt(s, length);
        }
    }

    static void WriteSized(Stream s, ulong v, int size)
    {
        for (int i = size - 1; i >= 0; i--) s.WriteByte((byte)(v >> (8 * i)));
    }

    // ---------------------------------------------------------------- reader

    public static object? Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < 40 || !data[..8].SequenceEqual("bplist00"u8))
            throw new FormatException("not a binary plist");
        var trailer = data[^32..];
        int offsetSize = trailer[6];
        int refSize = trailer[7];
        long numObjects = (long)BinaryPrimitives.ReadUInt64BigEndian(trailer[8..]);
        long top = (long)BinaryPrimitives.ReadUInt64BigEndian(trailer[16..]);
        long tableOffset = (long)BinaryPrimitives.ReadUInt64BigEndian(trailer[24..]);

        var offsets = new long[numObjects];
        for (long i = 0; i < numObjects; i++)
            offsets[i] = (long)ReadSized(data.Slice((int)(tableOffset + i * offsetSize), offsetSize));

        var reader = new Reader(data.ToArray(), offsets, refSize);
        return reader.ReadObject(top, 0);
    }

    static ulong ReadSized(ReadOnlySpan<byte> b)
    {
        ulong v = 0;
        foreach (var x in b) v = (v << 8) | x;
        return v;
    }

    sealed class Reader(byte[] data, long[] offsets, int refSize)
    {
        public object? ReadObject(long index, int depth)
        {
            if (depth > 64) throw new FormatException("plist too deep");
            int pos = (int)offsets[index];
            byte marker = data[pos];
            int type = marker >> 4;
            int info = marker & 0xF;
            switch (type)
            {
                case 0x0:
                    return info switch { 0x8 => false, 0x9 => true, _ => null };
                case 0x1:
                {
                    int n = 1 << info;
                    var span = data.AsSpan(pos + 1, n);
                    if (n == 8) return BinaryPrimitives.ReadInt64BigEndian(span);
                    if (n == 16) return BinaryPrimitives.ReadUInt64BigEndian(span[8..]);
                    return (long)ReadSized(span);
                }
                case 0x2:
                {
                    int n = 1 << info;
                    return n == 4
                        ? BinaryPrimitives.ReadSingleBigEndian(data.AsSpan(pos + 1, 4))
                        : BinaryPrimitives.ReadDoubleBigEndian(data.AsSpan(pos + 1, 8));
                }
                case 0x3:
                    return new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                        .AddSeconds(BinaryPrimitives.ReadDoubleBigEndian(data.AsSpan(pos + 1, 8)));
                case 0x4:
                {
                    var (len, start) = Length(pos, info);
                    return data.AsSpan(start, len).ToArray();
                }
                case 0x5:
                {
                    var (len, start) = Length(pos, info);
                    return Encoding.ASCII.GetString(data, start, len);
                }
                case 0x6:
                {
                    var (len, start) = Length(pos, info);
                    return Encoding.BigEndianUnicode.GetString(data, start, len * 2);
                }
                case 0x8:
                    return (long)ReadSized(data.AsSpan(pos + 1, info + 1));
                case 0xA:
                {
                    var (len, start) = Length(pos, info);
                    var list = new List<object?>(len);
                    for (int i = 0; i < len; i++)
                        list.Add(ReadObject(Ref(start + i * refSize), depth + 1));
                    return list;
                }
                case 0xD:
                {
                    var (len, start) = Length(pos, info);
                    var dict = new Dictionary<string, object?>(len);
                    for (int i = 0; i < len; i++)
                    {
                        var key = ReadObject(Ref(start + i * refSize), depth + 1)?.ToString() ?? "";
                        dict[key] = ReadObject(Ref(start + (len + i) * refSize), depth + 1);
                    }
                    return dict;
                }
                default:
                    throw new FormatException($"plist: unsupported marker 0x{marker:x2}");
            }
        }

        long Ref(int at) => (long)ReadSized(data.AsSpan(at, refSize));

        (int len, int start) Length(int pos, int info)
        {
            if (info != 0xF) return (info, pos + 1);
            byte m = data[pos + 1];
            int n = 1 << (m & 0xF);
            int len = (int)ReadSized(data.AsSpan(pos + 2, n));
            return (len, pos + 2 + n);
        }
    }

    // ---------------------------------------------------------------- helpers

    public static string Dump(object? o, int indent = 0)
    {
        var pad = new string(' ', indent);
        return o switch
        {
            Dictionary<string, object?> d => "{\n" + string.Join("", d.Select(kv => $"{pad}  {kv.Key}: {Dump(kv.Value, indent + 2)}\n")) + pad + "}",
            List<object?> l => "[" + string.Join(", ", l.Select(x => Dump(x, indent + 2))) + "]",
            byte[] b => $"<{b.Length} bytes {Convert.ToHexString(b.AsSpan(0, Math.Min(16, b.Length))).ToLowerInvariant()}>",
            string s => $"\"{s}\"",
            null => "null",
            _ => o.ToString() ?? "",
        };
    }
}
