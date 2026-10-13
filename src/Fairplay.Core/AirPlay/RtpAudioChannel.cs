using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Fairplay.Crypto;
using Fairplay.Util;

namespace Fairplay.AirPlay;

public static class AudioFormat
{
    public const int SampleRate = 44100;
    public const int Channels = 2;
    public const int FramesPerPacket = 352;
    public const int BytesPerFrame = 4;
    public const int PayloadBytes = FramesPerPacket * BytesPerFrame;
}

/// <summary>
/// UDP side of an AirPlay 2 realtime audio stream.
///  • audio:   RTP(pt 96) header | ChaCha20-Poly1305(PCM s16be, aad = header[4..12]) | tag | nonce[8]
///  • control: periodic sync packets (pt 0x54) mapping RTP time ↔ our NTP clock;
///             answers retransmit requests (pt 0x55) from a packet backlog with pt 0x56.
/// </summary>
public sealed class RtpAudioChannel : IDisposable
{
    const int BacklogSize = 1024; // ~8 s of packets

    readonly Socket _control;
    readonly Socket _data;
    readonly ChaCha20Poly1305 _aead;
    readonly byte[][] _backlog = new byte[BacklogSize][];
    readonly ushort[] _backlogSeq = new ushort[BacklogSize];
    readonly CancellationTokenSource _cts = new();
    readonly object _sendLock = new();
    IPEndPoint? _remoteControl;
    IPEndPoint? _remoteData;
    ulong _nonce;
    readonly uint _ssrc = (uint)RandomNumberGenerator.GetInt32(int.MaxValue);

    public byte[] StreamKey { get; } = RandomNumberGenerator.GetBytes(32);
    public int LocalControlPort { get; }

    /// <summary>Diagnostics only: break every packet's auth tag to observe receiver behavior.</summary>
    public bool CorruptAudioForTesting { get; set; }

    public long PacketsSent;
    public long RetransmitRequests;
    public long PacketsResent;
    public long PacketsMissing;

    public RtpAudioChannel(IPAddress local)
    {
        _control = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _control.Bind(new IPEndPoint(local, 0));
        TimingServer.IgnoreConnReset(_control);
        LocalControlPort = ((IPEndPoint)_control.LocalEndPoint!).Port;

        _data = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _data.Bind(new IPEndPoint(local, 0));
        _data.SendBufferSize = 1 << 20;
        TimingServer.IgnoreConnReset(_data);
        // DSCP EF-ish priority for audio; ignored if the network doesn't honor it.
        try { _data.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.TypeOfService, 0xB8); } catch { }

        _aead = new ChaCha20Poly1305(StreamKey);
    }

    public void Start(IPAddress remote, StreamEndpoints ep)
    {
        _remoteControl = new IPEndPoint(remote, ep.ControlPort);
        _remoteData = new IPEndPoint(remote, ep.DataPort);
        var t = new Thread(ControlLoop) { IsBackground = true, Name = "AirPlay control", Priority = ThreadPriority.AboveNormal };
        t.Start();
    }

    /// <summary>Encrypt and send one packet of 352 stereo frames (s16 big-endian already).</summary>
    public void SendAudio(ushort seq, uint timestamp, ReadOnlySpan<byte> pcmBigEndian, bool marker)
    {
        var pkt = new byte[12 + AudioFormat.PayloadBytes + 16 + 8];
        pkt[0] = 0x80;
        pkt[1] = (byte)(marker ? 0xE0 : 0x60);
        BinaryPrimitives.WriteUInt16BigEndian(pkt.AsSpan(2), seq);
        BinaryPrimitives.WriteUInt32BigEndian(pkt.AsSpan(4), timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(pkt.AsSpan(8), _ssrc);

        Span<byte> nonce = stackalloc byte[12];
        ulong n = _nonce++;
        HapKeys.CounterNonce(nonce, n);
        var payload = pkt.AsSpan(12, AudioFormat.PayloadBytes);
        var tag = pkt.AsSpan(12 + AudioFormat.PayloadBytes, 16);
        _aead.Encrypt(nonce, pcmBigEndian[..AudioFormat.PayloadBytes], payload, tag, pkt.AsSpan(4, 8));
        BinaryPrimitives.WriteUInt64LittleEndian(pkt.AsSpan(12 + AudioFormat.PayloadBytes + 16), n);
        if (CorruptAudioForTesting) tag[0] ^= 0xFF;

        int slot = seq % BacklogSize;
        lock (_sendLock)
        {
            _backlog[slot] = pkt;
            _backlogSeq[slot] = seq;
        }
        try
        {
            _data.SendTo(pkt, _remoteData!);
            Interlocked.Increment(ref PacketsSent);
        }
        catch (SocketException e) { Log.Debug($"audio send failed: {e.SocketErrorCode}"); }
    }

    /// <summary>
    /// Sync packet: "the frame with RTP time <paramref name="rtpPlayNow"/> must be heard at NTP
    /// time <paramref name="ntpNow"/>"; <paramref name="rtpNext"/> is the next timestamp we send.
    /// </summary>
    public void SendSync(uint rtpPlayNow, ulong ntpNow, uint rtpNext, bool first)
    {
        Span<byte> p = stackalloc byte[20];
        p[0] = (byte)(first ? 0x90 : 0x80);
        p[1] = 0xD4;
        BinaryPrimitives.WriteUInt16BigEndian(p[2..], 7);
        BinaryPrimitives.WriteUInt32BigEndian(p[4..], rtpPlayNow);
        BinaryPrimitives.WriteUInt64BigEndian(p[8..], ntpNow);
        BinaryPrimitives.WriteUInt32BigEndian(p[16..], rtpNext);
        try { _control.SendTo(p, _remoteControl!); }
        catch (SocketException e) { Log.Debug($"sync send failed: {e.SocketErrorCode}"); }
    }

    void ControlLoop()
    {
        var buf = new byte[2048];
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
        while (!_cts.IsCancellationRequested)
        {
            int n;
            try { n = _control.ReceiveFrom(buf, ref from); }
            catch (SocketException) { if (_cts.IsCancellationRequested) return; continue; }
            catch (ObjectDisposedException) { return; }
            if (n < 8) continue;
            int type = buf[1] & 0x7F;
            if (type == 0x55)
            {
                ushort first = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(4));
                ushort count = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(6));
                Interlocked.Increment(ref RetransmitRequests);
                Resend(first, count, from);
            }
            else
            {
                Log.Debug($"control: unhandled packet type 0x{type:x2} ({n} bytes)");
            }
        }
    }

    void Resend(ushort first, ushort count, EndPoint to)
    {
        for (int i = 0; i < Math.Min((int)count, 128); i++)
        {
            ushort seq = (ushort)(first + i);
            int slot = seq % BacklogSize;
            byte[]? pkt;
            lock (_sendLock) pkt = _backlogSeq[slot] == seq ? _backlog[slot] : null;
            if (pkt == null)
            {
                Interlocked.Increment(ref PacketsMissing);
                continue;
            }
            var resp = new byte[4 + pkt.Length];
            resp[0] = 0x80;
            resp[1] = 0xD6;
            resp[2] = pkt[2];
            resp[3] = pkt[3];
            pkt.CopyTo(resp, 4);
            try
            {
                _control.SendTo(resp, to);
                Interlocked.Increment(ref PacketsResent);
            }
            catch (SocketException) { }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _control.Dispose();
        _data.Dispose();
        _aead.Dispose();
    }
}
