using System.Numerics;
using System.Security.Cryptography;
using Fairplay.Audio;
using Fairplay.Crypto;
using Fairplay.Plist;

namespace Fairplay.Cli;

/// <summary>Offline checks of the protocol building blocks.</summary>
static class SelfTest
{
    static int _failures;

    public static int Run()
    {
        Check("plist round-trip", PlistRoundTrip);
        Check("tlv8 fragmentation", Tlv8RoundTrip);
        Check("srp client vs independent server", SrpAgainstServer);
        Check("hap stream cipher", HapCipherRoundTrip);
        Check("resampler 48k→44.1k quality @1 kHz", () => ResamplerQuality(1000));
        Check("resampler 48k→44.1k quality @10 kHz", () => ResamplerQuality(10000));
        Console.WriteLine(_failures == 0 ? "all self-tests passed" : $"{_failures} self-test(s) FAILED");
        return _failures == 0 ? 0 : 1;
    }

    static void Check(string name, Func<string?> test)
    {
        try
        {
            var detail = test();
            Console.WriteLine($"  ok    {name}{(detail != null ? $"  ({detail})" : "")}");
        }
        catch (Exception e)
        {
            _failures++;
            Console.WriteLine($"  FAIL  {name}: {e.Message}");
        }
    }

    static void Assert(bool cond, string msg)
    {
        if (!cond) throw new Exception(msg);
    }

    static string? PlistRoundTrip()
    {
        var obj = new Dictionary<string, object?>
        {
            ["str"] = "hello",
            ["uni"] = "Pierce’s HomePod",
            ["int"] = 44100,
            ["neg"] = -5L,
            ["big"] = 0x1_0000_0000L,
            ["bool"] = true,
            ["data"] = new byte[] { 1, 2, 3 },
            ["real"] = 1.5,
            ["arr"] = new List<object?> { 1, "two", new Dictionary<string, object?> { ["x"] = false } },
        };
        // enough entries to need a long-form length marker
        for (int i = 0; i < 20; i++) obj[$"k{i}"] = i;
        var bytes = BinaryPlist.Write(obj);
        var back = (Dictionary<string, object?>)BinaryPlist.Read(bytes)!;
        Assert((string)back["str"]! == "hello", "string");
        Assert((string)back["uni"]! == "Pierce’s HomePod", "utf16 string");
        Assert((long)back["int"]! == 44100, "int");
        Assert((long)back["neg"]! == -5, "negative int");
        Assert((long)back["big"]! == 0x1_0000_0000L, "big int");
        Assert((bool)back["bool"]!, "bool");
        Assert(((byte[])back["data"]!).SequenceEqual(new byte[] { 1, 2, 3 }), "data");
        Assert((double)back["real"]! == 1.5, "real");
        var arr = (List<object?>)back["arr"]!;
        Assert(arr.Count == 3 && (string)arr[1]! == "two" && ((Dictionary<string, object?>)arr[2]!)["x"] is false, "array");
        Assert((long)back["k19"]! == 19, "dict size > 14");
        return $"{bytes.Length} bytes";
    }

    static string? Tlv8RoundTrip()
    {
        var big = RandomNumberGenerator.GetBytes(600);
        var enc = Tlv8.Encode((Tlv8.State, [3]), (Tlv8.PublicKey, big), (Tlv8.Proof, new byte[64]));
        var dec = Tlv8.Decode(enc);
        Assert(dec[Tlv8.State][0] == 3, "state");
        Assert(dec[Tlv8.PublicKey].SequenceEqual(big), "fragmented value");
        Assert(dec[Tlv8.Proof].Length == 64, "proof");
        return null;
    }

    /// <summary>Run a textbook SRP-6a server against our client and check both derive the same key/proofs.</summary>
    static string? SrpAgainstServer()
    {
        var N = new BigInteger(Convert.FromHexString(
            "FFFFFFFFFFFFFFFFC90FDAA22168C234C4C6628B80DC1CD129024E088A67CC74020BBEA63B139B22514A08798E3404DD" +
            "EF9519B3CD3A431B302B0A6DF25F14374FE1356D6D51C245E485B576625E7EC6F44C42E9A637ED6B0BFF5CB6F406B7ED" +
            "EE386BFB5A899FA5AE9F24117C4B1FE649286651ECE45B3DC2007CB8A163BF0598DA48361C55D39A69163FA8FD24CF5F" +
            "83655D23DCA3AD961C62F356208552BB9ED529077096966D670C354E4ABC9804F1746C08CA18217C32905E462E36CE3B" +
            "E39E772C180E86039B2783A2EC07A28FB5C55DF06F4C52C9DE2BCBF6955817183995497CEA956AE515D2261898FA0510" +
            "15728E5A8AAAC42DAD33170D04507A33A85521ABDF1CBA64ECFB850458DBEF0A8AEA71575D060C7DB3970F85A6E1E4C7" +
            "ABF5AE8CDB0933D71E8C94E04A25619DCEE3D2261AD2EE6BF12FFA06D98A0864D87602733EC86A64521F2B18177B200C" +
            "BBE117577A615D6C770988C0BAD946E208E24FA074E5AB3143DB5BFCE0FD108E4B82D120A93AD2CAFFFFFFFFFFFFFFFF"), true, true);
        var g = new BigInteger(5);
        static byte[] H(params byte[][] p) => SHA512.HashData(p.SelectMany(x => x).ToArray());
        static byte[] B2(BigInteger v) => v.ToByteArray(true, true);
        static BigInteger I(byte[] b) => new(b, true, true);
        static byte[] Pad(byte[] b) => new byte[384 - b.Length].Concat(b).ToArray();

        int rounds = 0;
        for (int round = 0; round < 5; round++, rounds++)
        {
            var salt = RandomNumberGenerator.GetBytes(16);
            var x = I(H(salt, H("Pair-Setup:3939"u8.ToArray())));
            var v = BigInteger.ModPow(g, x, N);
            var b = I(RandomNumberGenerator.GetBytes(32));
            var k = I(H(B2(N), Pad(B2(g))));
            var B = (k * v + BigInteger.ModPow(g, b, N)) % N;

            var client = new SrpClient("Pair-Setup", "3939");
            client.Process(salt, B2(B));

            var A = I(client.PublicKey);
            var u = I(H(Pad(B2(A)), Pad(B2(B))));
            var S = BigInteger.ModPow(A * BigInteger.ModPow(v, u, N) % N, b, N);
            var K = H(B2(S));
            Assert(K.SequenceEqual(client.SessionKey), "session key mismatch");

            var hn = H(B2(N)); var hg = H(B2(g));
            var hx = hn.Zip(hg, (p, q) => (byte)(p ^ q)).ToArray();
            var M1 = H(hx, H("Pair-Setup"u8.ToArray()), salt, B2(A), B2(B), K);
            Assert(M1.SequenceEqual(client.Proof), "client proof mismatch");
            Assert(client.VerifyServerProof(H(B2(A), M1, K)), "server proof rejected");
        }
        return $"{rounds} handshakes";
    }

    static string? HapCipherRoundTrip()
    {
        var k1 = RandomNumberGenerator.GetBytes(32);
        var k2 = RandomNumberGenerator.GetBytes(32);
        using var a = new HapStreamCipher(k1, k2);
        using var b = new HapStreamCipher(k2, k1);
        var msg = RandomNumberGenerator.GetBytes(3000);
        var wire = a.Encrypt(msg);
        Assert(wire.Length == 3000 + 3 * 18, "framing overhead");
        // deliver in awkward chunks
        var got = new MemoryStream();
        for (int i = 0; i < wire.Length; i += 777) got.Write(b.Decrypt(wire.AsSpan(i, Math.Min(777, wire.Length - i))));
        Assert(got.ToArray().SequenceEqual(msg), "plaintext mismatch");
        var second = b.Decrypt(a.Encrypt("again"u8));
        Assert(second.SequenceEqual("again"u8.ToArray()), "counter continuity");
        return null;
    }

    static string? ResamplerQuality(double f)
    {
        const int inRate = 48000, outRate = 44100;
        var rs = new Resampler(inRate, outRate);
        var input = new float[inRate * 2 * 2];
        for (int i = 0; i < inRate * 2; i++)
        {
            float s = (float)(0.5 * Math.Sin(2 * Math.PI * f * i / inRate));
            input[2 * i] = s;
            input[2 * i + 1] = -s;
        }
        var output = new List<float>();
        for (int off = 0; off < input.Length; off += 960)
            rs.Process(input.AsSpan(off, Math.Min(960, input.Length - off)), d => output.AddRange(d.ToArray()));

        int frames = output.Count / 2;
        Assert(Math.Abs(frames - outRate * 2) < 200, $"output length {frames}");
        // Fit amplitude/phase of the expected tone over a steady-state window and measure the residual.
        int start = 2000, len = 40000;
        double sc = 0, ss = 0;
        for (int i = start; i < start + len; i++)
        {
            double w = 2 * Math.PI * f * i / outRate;
            sc += output[2 * i] * Math.Cos(w);
            ss += output[2 * i] * Math.Sin(w);
        }
        double a = 2 * sc / len, b = 2 * ss / len;
        double amp = Math.Sqrt(a * a + b * b);
        double err = 0, sig = 0;
        for (int i = start; i < start + len; i++)
        {
            double w = 2 * Math.PI * f * i / outRate;
            double fit = a * Math.Cos(w) + b * Math.Sin(w);
            err += Math.Pow(output[2 * i] - fit, 2);
            sig += fit * fit;
            Assert(Math.Abs(output[2 * i + 1] + output[2 * i]) < 1e-5, "channel separation");
        }
        double snr = 10 * Math.Log10(sig / err);
        Assert(Math.Abs(amp - 0.5) < 0.005, $"amplitude {amp:F4}");
        Assert(snr > 85, $"SNR {snr:F1} dB");
        return $"amplitude {amp:F4}, SNR {snr:F1} dB";
    }
}
