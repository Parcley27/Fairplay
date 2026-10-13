using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Fairplay.Util;

namespace Fairplay.AirPlay;

/// <summary>
/// Answers the receiver's NTP-style timing requests (RTP payload type 0x52 → reply 0x53).
/// The receiver uses these exchanges to map our <see cref="NtpClock"/> onto its own clock,
/// which is what lets it schedule our audio (and keep a stereo pair in lock-step).
/// </summary>
public sealed class TimingServer : IDisposable
{
    readonly UdpClient _udp;
    readonly CancellationTokenSource _cts = new();
    long _requests;

    public int Port { get; }
    public long RequestCount => Interlocked.Read(ref _requests);

    readonly System.Collections.Concurrent.ConcurrentDictionary<IPAddress, int> _perSender = new();

    /// <summary>Timing requests seen from one receiver.</summary>
    public int RequestsFrom(IPAddress address) => _perSender.TryGetValue(address, out var n) ? n : 0;
    public DateTime LastRequest { get; private set; }

    public TimingServer(IPAddress local)
    {
        _udp = new UdpClient(new IPEndPoint(local, 0));
        IgnoreConnReset(_udp.Client);
        Port = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
        var t = new Thread(Run) { IsBackground = true, Name = "AirPlay timing", Priority = ThreadPriority.AboveNormal };
        t.Start();
    }

    void Run()
    {
        var reply = new byte[32];
        var remote = new IPEndPoint(IPAddress.Any, 0);
        while (!_cts.IsCancellationRequested)
        {
            byte[] req;
            try { req = _udp.Receive(ref remote); }
            catch (SocketException) { if (_cts.IsCancellationRequested) return; continue; }
            catch (ObjectDisposedException) { return; }

            ulong recv = NtpClock.Now();
            if (req.Length < 32 || (req[1] & 0x7F) != 0x52) continue;

            reply[0] = req[0];
            reply[1] = 0x53 | 0x80;
            reply[2] = 0x00;
            reply[3] = 0x07;
            BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(4), 0);
            // origin timestamp = the requester's transmit timestamp
            req.AsSpan(24, 8).CopyTo(reply.AsSpan(8));
            BinaryPrimitives.WriteUInt64BigEndian(reply.AsSpan(16), recv);
            BinaryPrimitives.WriteUInt64BigEndian(reply.AsSpan(24), NtpClock.Now());
            try { _udp.Send(reply, reply.Length, remote); } catch (SocketException) { }

            Interlocked.Increment(ref _requests);
            LastRequest = DateTime.UtcNow;
            if (_perSender.AddOrUpdate(remote.Address, 1, (_, c) => c + 1) == 1)
                Log.Info($"timing: first request from {remote} — receiver is syncing to our clock");
        }
    }

    internal static void IgnoreConnReset(Socket s)
    {
        // Windows reports ICMP port-unreachable as a socket error on the next receive; disable that.
        const int SIO_UDP_CONNRESET = -1744830452;
        try { s.IOControl(SIO_UDP_CONNRESET, [0, 0, 0, 0], null); } catch { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _udp.Dispose();
    }
}
