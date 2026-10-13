using System.Numerics;
using System.Security.Cryptography;

namespace Fairplay.Crypto;

/// <summary>
/// SRP-6a client as used by HomeKit / AirPlay pair-setup: RFC 5054 3072-bit group, g = 5, SHA-512.
///   k  = H(N | PAD(g))          u  = H(PAD(A) | PAD(B))
///   x  = H(s | H(I ":" P))      S  = (B - k*g^x) ^ (a + u*x) mod N
///   K  = H(S)                   M1 = H(H(N) xor H(g) | H(I) | s | A | B | K)
///   M2 = H(A | M1 | K)
/// </summary>
public sealed class SrpClient
{
    static readonly byte[] NBytes = Convert.FromHexString(
        "FFFFFFFFFFFFFFFFC90FDAA22168C234C4C6628B80DC1CD129024E088A67CC74020BBEA63B139B22514A08798E3404DD" +
        "EF9519B3CD3A431B302B0A6DF25F14374FE1356D6D51C245E485B576625E7EC6F44C42E9A637ED6B0BFF5CB6F406B7ED" +
        "EE386BFB5A899FA5AE9F24117C4B1FE649286651ECE45B3DC2007CB8A163BF0598DA48361C55D39A69163FA8FD24CF5F" +
        "83655D23DCA3AD961C62F356208552BB9ED529077096966D670C354E4ABC9804F1746C08CA18217C32905E462E36CE3B" +
        "E39E772C180E86039B2783A2EC07A28FB5C55DF06F4C52C9DE2BCBF6955817183995497CEA956AE515D2261898FA0510" +
        "15728E5A8AAAC42DAD33170D04507A33A85521ABDF1CBA64ECFB850458DBEF0A8AEA71575D060C7DB3970F85A6E1E4C7" +
        "ABF5AE8CDB0933D71E8C94E04A25619DCEE3D2261AD2EE6BF12FFA06D98A0864D87602733EC86A64521F2B18177B200C" +
        "BBE117577A615D6C770988C0BAD946E208E24FA074E5AB3143DB5BFCE0FD108E4B82D120A93AD2CAFFFFFFFFFFFFFFFF");

    static readonly BigInteger N = FromBytes(NBytes);
    static readonly BigInteger G = 5;
    const int PadLen = 384;

    readonly string _user;
    readonly string _password;
    readonly BigInteger _a;

    public byte[] PublicKey { get; private set; } = [];
    public byte[] Proof { get; private set; } = [];
    public byte[] SessionKey { get; private set; } = [];

    public SrpClient(string user, string password, byte[]? privateKey = null)
    {
        _user = user;
        _password = password;
        _a = FromBytes(privateKey ?? RandomNumberGenerator.GetBytes(32));
    }

    /// <summary>Consume the server's salt and public key B; computes A, K and the client proof M1.</summary>
    public void Process(byte[] salt, byte[] serverPublic)
    {
        var B = FromBytes(serverPublic);
        if (B % N == 0) throw new CryptographicException("invalid SRP server public key");

        var A = BigInteger.ModPow(G, _a, N);
        var aBytes = ToBytes(A);

        var k = FromBytes(H(NBytes, Pad(ToBytes(G))));
        var u = FromBytes(H(Pad(aBytes), Pad(serverPublic)));
        if (u.IsZero) throw new CryptographicException("invalid SRP scrambler");

        var inner = H(System.Text.Encoding.UTF8.GetBytes($"{_user}:{_password}"));
        var x = FromBytes(H(salt, inner));
        var v = BigInteger.ModPow(G, x, N);

        var baseVal = ((B - k * v) % N + N) % N;
        var S = BigInteger.ModPow(baseVal, _a + u * x, N);
        var K = H(ToBytes(S));

        var hn = H(NBytes);
        var hg = H(ToBytes(G));
        var hxor = new byte[hn.Length];
        for (int i = 0; i < hn.Length; i++) hxor[i] = (byte)(hn[i] ^ hg[i]);
        var hi = H(System.Text.Encoding.UTF8.GetBytes(_user));

        PublicKey = aBytes;
        SessionKey = K;
        Proof = H(hxor, hi, salt, aBytes, ToBytes(B), K);
    }

    public bool VerifyServerProof(byte[] serverProof)
    {
        var expected = H(PublicKey, Proof, SessionKey);
        return CryptographicOperations.FixedTimeEquals(expected, serverProof);
    }

    static byte[] H(params byte[][] parts)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        foreach (var p in parts) sha.AppendData(p);
        return sha.GetHashAndReset();
    }

    static BigInteger FromBytes(byte[] b) => new(b, isUnsigned: true, isBigEndian: true);

    static byte[] ToBytes(BigInteger v) => v.ToByteArray(isUnsigned: true, isBigEndian: true);

    static byte[] Pad(byte[] b)
    {
        if (b.Length >= PadLen) return b;
        var r = new byte[PadLen];
        b.CopyTo(r, PadLen - b.Length);
        return r;
    }
}
