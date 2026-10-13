namespace Fairplay.Crypto;

/// <summary>HomeKit TLV8 encoding (values longer than 255 bytes are split into consecutive fragments).</summary>
public static class Tlv8
{
    public const byte Method = 0x00;
    public const byte Identifier = 0x01;
    public const byte Salt = 0x02;
    public const byte PublicKey = 0x03;
    public const byte Proof = 0x04;
    public const byte EncryptedData = 0x05;
    public const byte State = 0x06;
    public const byte Error = 0x07;
    public const byte RetryDelay = 0x08;
    public const byte Signature = 0x0A;
    public const byte Flags = 0x13;

    public const byte FlagTransient = 0x10;

    public static byte[] Encode(params (byte Type, byte[] Value)[] items)
    {
        var ms = new MemoryStream();
        foreach (var (type, value) in items)
        {
            int offset = 0;
            do
            {
                int len = Math.Min(255, value.Length - offset);
                ms.WriteByte(type);
                ms.WriteByte((byte)len);
                ms.Write(value, offset, len);
                offset += len;
            } while (offset < value.Length);
        }
        return ms.ToArray();
    }

    public static Dictionary<byte, byte[]> Decode(ReadOnlySpan<byte> data)
    {
        var result = new Dictionary<byte, byte[]>();
        int i = 0;
        int lastType = -1;
        while (i + 2 <= data.Length)
        {
            byte type = data[i];
            int len = data[i + 1];
            if (i + 2 + len > data.Length) throw new FormatException("truncated TLV8");
            var value = data.Slice(i + 2, len);
            if (type == lastType && result.TryGetValue(type, out var prev))
            {
                // continuation fragment
                var merged = new byte[prev.Length + len];
                prev.CopyTo(merged, 0);
                value.CopyTo(merged.AsSpan(prev.Length));
                result[type] = merged;
            }
            else
            {
                result[type] = value.ToArray();
            }
            lastType = type;
            i += 2 + len;
        }
        return result;
    }
}
