using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Fairplay.Util;

namespace Fairplay.Discovery;

/// <summary>A resource record pulled out of an mDNS response.</summary>
public sealed record DnsRecord(string Name, ushort Type, string? Target, ushort Port, byte[]? Rdata, IPAddress? Address);

/// <summary>
/// Minimal multicast DNS querier. Binds the shared mDNS port (5353, SO_REUSEADDR) on every usable
/// IPv4 interface so it receives normal multicast answers, and falls back to an ephemeral port
/// (RFC 6762 "legacy unicast" replies) if 5353 cannot be shared.
/// </summary>
public static class Mdns
{
    public const ushort TypeA = 1, TypePtr = 12, TypeTxt = 16, TypeAaaa = 28, TypeSrv = 33, TypeAny = 255;
    static readonly IPAddress Group = IPAddress.Parse("224.0.0.251");
    const int Port = 5353;

    public static IEnumerable<IPAddress> LocalIPv4Addresses()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (!ni.SupportsMulticast) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var b = ua.Address.GetAddressBytes();
                if (b[0] == 169 && b[1] == 254) continue; // link-local, never where speakers live
                yield return ua.Address;
            }
        }
    }

    /// <summary>Sends the given questions on all interfaces and collects records for <paramref name="duration"/>.</summary>
    public static async Task<List<DnsRecord>> QueryAsync(IEnumerable<(string name, ushort type)> questions, TimeSpan duration, CancellationToken ct = default)
    {
        var query = BuildQuery(questions.ToList());
        var records = new List<DnsRecord>();
        var sockets = new List<Socket>();
        try
        {
            foreach (var local in LocalIPv4Addresses())
            {
                var s = OpenSocket(local);
                if (s != null) sockets.Add(s);
            }
            if (sockets.Count == 0) throw new InvalidOperationException("no usable network interface for mDNS");

            var end = DateTime.UtcNow + duration;
            var receives = sockets.Select(s => ReceiveLoop(s, records, end, ct)).ToList();

            // Send the query a few times; mDNS is lossy and responders rate-limit.
            for (int attempt = 0; attempt < 3 && DateTime.UtcNow < end; attempt++)
            {
                foreach (var s in sockets)
                {
                    try { await s.SendToAsync(query, SocketFlags.None, new IPEndPoint(Group, Port), ct); }
                    catch (SocketException e) { Log.Debug($"mDNS send failed on {s.LocalEndPoint}: {e.Message}"); }
                }
                var wait = TimeSpan.FromMilliseconds(Math.Min(400 * (attempt + 1), Math.Max(0, (end - DateTime.UtcNow).TotalMilliseconds)));
                await Task.Delay(wait, ct);
            }
            await Task.WhenAll(receives);
        }
        finally
        {
            foreach (var s in sockets) s.Dispose();
        }
        lock (records) return records.ToList();
    }

    static Socket? OpenSocket(IPAddress local)
    {
        var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            try
            {
                s.Bind(new IPEndPoint(IPAddress.Any, Port));
                s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(Group, local));
            }
            catch (SocketException)
            {
                s.Dispose();
                s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                s.Bind(new IPEndPoint(local, 0)); // legacy unicast mode
            }
            s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
            s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
            return s;
        }
        catch (Exception e)
        {
            Log.Debug($"mDNS: cannot open socket on {local}: {e.Message}");
            s.Dispose();
            return null;
        }
    }

    static async Task ReceiveLoop(Socket s, List<DnsRecord> sink, DateTime end, CancellationToken ct)
    {
        var buf = new byte[9000];
        while (true)
        {
            var remaining = end - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) return;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(remaining);
            int n;
            try
            {
                var r = await s.ReceiveFromAsync(buf, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), cts.Token);
                n = r.ReceivedBytes;
            }
            catch (OperationCanceledException) { return; }
            catch (SocketException) { return; }
            catch (ObjectDisposedException) { return; }
            try
            {
                var recs = Parse(buf.AsSpan(0, n));
                lock (sink) sink.AddRange(recs);
            }
            catch (Exception e)
            {
                Log.Debug($"mDNS: bad packet: {e.Message}");
            }
        }
    }

    static byte[] BuildQuery(List<(string name, ushort type)> questions)
    {
        var ms = new MemoryStream();
        Span<byte> hdr = stackalloc byte[12];
        hdr.Clear();
        BinaryPrimitives.WriteUInt16BigEndian(hdr[4..], (ushort)questions.Count);
        ms.Write(hdr);
        foreach (var (name, type) in questions)
        {
            foreach (var label in name.TrimEnd('.').Split('.'))
            {
                var lb = Encoding.UTF8.GetBytes(label);
                ms.WriteByte((byte)lb.Length);
                ms.Write(lb);
            }
            ms.WriteByte(0);
            Span<byte> q = stackalloc byte[4];
            BinaryPrimitives.WriteUInt16BigEndian(q, type);
            BinaryPrimitives.WriteUInt16BigEndian(q[2..], 1); // class IN, QM
            ms.Write(q);
        }
        return ms.ToArray();
    }

    public static List<DnsRecord> Parse(ReadOnlySpan<byte> msg)
    {
        var list = new List<DnsRecord>();
        if (msg.Length < 12) return list;
        ushort flags = BinaryPrimitives.ReadUInt16BigEndian(msg[2..]);
        if ((flags & 0x8000) == 0) return list; // a query, not a response
        int qd = BinaryPrimitives.ReadUInt16BigEndian(msg[4..]);
        int an = BinaryPrimitives.ReadUInt16BigEndian(msg[6..]);
        int ns = BinaryPrimitives.ReadUInt16BigEndian(msg[8..]);
        int ar = BinaryPrimitives.ReadUInt16BigEndian(msg[10..]);
        int pos = 12;
        for (int i = 0; i < qd; i++)
        {
            ReadName(msg, ref pos);
            pos += 4;
        }
        int total = an + ns + ar;
        for (int i = 0; i < total && pos < msg.Length; i++)
        {
            string name = ReadName(msg, ref pos);
            ushort type = BinaryPrimitives.ReadUInt16BigEndian(msg[pos..]);
            int rdlen = BinaryPrimitives.ReadUInt16BigEndian(msg[(pos + 8)..]);
            int rdata = pos + 10;
            pos = rdata + rdlen;
            if (pos > msg.Length) break;
            switch (type)
            {
                case TypePtr:
                {
                    int p = rdata;
                    list.Add(new DnsRecord(name, type, ReadName(msg, ref p), 0, null, null));
                    break;
                }
                case TypeSrv:
                {
                    ushort port = BinaryPrimitives.ReadUInt16BigEndian(msg[(rdata + 4)..]);
                    int p = rdata + 6;
                    list.Add(new DnsRecord(name, type, ReadName(msg, ref p), port, null, null));
                    break;
                }
                case TypeTxt:
                    list.Add(new DnsRecord(name, type, null, 0, msg.Slice(rdata, rdlen).ToArray(), null));
                    break;
                case TypeA when rdlen == 4:
                case TypeAaaa when rdlen == 16:
                    list.Add(new DnsRecord(name, type, null, 0, null, new IPAddress(msg.Slice(rdata, rdlen))));
                    break;
            }
        }
        return list;
    }

    static string ReadName(ReadOnlySpan<byte> msg, ref int pos)
    {
        var sb = new StringBuilder();
        int p = pos;
        bool jumped = false;
        int guard = 0;
        while (true)
        {
            if (p >= msg.Length || ++guard > 128) throw new FormatException("bad DNS name");
            int len = msg[p];
            if (len == 0)
            {
                p++;
                break;
            }
            if ((len & 0xC0) == 0xC0)
            {
                int ptr = ((len & 0x3F) << 8) | msg[p + 1];
                if (!jumped) pos = p + 2;
                jumped = true;
                p = ptr;
                continue;
            }
            sb.Append(Encoding.UTF8.GetString(msg.Slice(p + 1, len))).Append('.');
            p += 1 + len;
        }
        if (!jumped) pos = p;
        return sb.ToString();
    }

    public static Dictionary<string, string> ParseTxt(byte[] rdata)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int i = 0;
        while (i < rdata.Length)
        {
            int len = rdata[i];
            if (i + 1 + len > rdata.Length) break;
            var s = Encoding.UTF8.GetString(rdata, i + 1, len);
            int eq = s.IndexOf('=');
            if (eq > 0) d[s[..eq]] = s[(eq + 1)..];
            else if (s.Length > 0) d[s] = "";
            i += 1 + len;
        }
        return d;
    }
}
