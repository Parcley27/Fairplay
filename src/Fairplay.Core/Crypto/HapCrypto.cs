using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Fairplay.Crypto;

public static class HapKeys
{
    /// <summary>HKDF-SHA512 → 32-byte key, as used for every AirPlay/HAP channel key.</summary>
    public static byte[] Derive(byte[] sharedSecret, string salt, string info) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA512, sharedSecret, 32,
            Encoding.ASCII.GetBytes(salt), Encoding.ASCII.GetBytes(info));

    /// <summary>12-byte ChaCha20 nonce: 4 zero bytes followed by a little-endian 64-bit counter.</summary>
    public static void CounterNonce(Span<byte> nonce12, ulong counter)
    {
        nonce12[..4].Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(nonce12[4..], counter);
    }
}

/// <summary>
/// HAP "secure session" framing over a byte stream: plaintext is cut into blocks of at most
/// 1024 bytes, each sent as [len:u16le][ChaCha20-Poly1305(block, aad=len)][tag:16].
/// Each direction keeps its own nonce counter.
/// </summary>
public sealed class HapStreamCipher : IDisposable
{
    const int MaxBlock = 1024;
    const int TagLen = 16;

    readonly ChaCha20Poly1305 _enc;
    readonly ChaCha20Poly1305 _dec;
    ulong _encCounter;
    ulong _decCounter;
    readonly MemoryStream _pending = new();

    public HapStreamCipher(byte[] writeKey, byte[] readKey)
    {
        _enc = new ChaCha20Poly1305(writeKey);
        _dec = new ChaCha20Poly1305(readKey);
    }

    public byte[] Encrypt(ReadOnlySpan<byte> plain)
    {
        var output = new MemoryStream(plain.Length + (plain.Length / MaxBlock + 1) * (2 + TagLen));
        Span<byte> nonce = stackalloc byte[12];
        Span<byte> lenBytes = stackalloc byte[2];
        Span<byte> tag = stackalloc byte[TagLen];
        int offset = 0;
        do
        {
            int len = Math.Min(MaxBlock, plain.Length - offset);
            BinaryPrimitives.WriteUInt16LittleEndian(lenBytes, (ushort)len);
            HapKeys.CounterNonce(nonce, _encCounter++);
            var cipher = new byte[len];
            _enc.Encrypt(nonce, plain.Slice(offset, len), cipher, tag, lenBytes);
            output.Write(lenBytes);
            output.Write(cipher);
            output.Write(tag);
            offset += len;
        } while (offset < plain.Length);
        return output.ToArray();
    }

    /// <summary>Feed raw bytes from the socket; returns any plaintext from complete frames.</summary>
    public byte[] Decrypt(ReadOnlySpan<byte> data)
    {
        _pending.Write(data);
        var buf = _pending.GetBuffer();
        int avail = (int)_pending.Length;
        int pos = 0;
        var plain = new MemoryStream();
        Span<byte> nonce = stackalloc byte[12];
        while (avail - pos >= 2)
        {
            int len = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(pos, 2));
            if (avail - pos < 2 + len + TagLen) break;
            HapKeys.CounterNonce(nonce, _decCounter++);
            var block = new byte[len];
            _dec.Decrypt(nonce, buf.AsSpan(pos + 2, len), buf.AsSpan(pos + 2 + len, TagLen), block, buf.AsSpan(pos, 2));
            plain.Write(block);
            pos += 2 + len + TagLen;
        }
        // keep the unconsumed tail
        var rest = buf.AsSpan(pos, avail - pos).ToArray();
        _pending.SetLength(0);
        _pending.Write(rest);
        return plain.ToArray();
    }

    public void Dispose()
    {
        _enc.Dispose();
        _dec.Dispose();
    }
}
