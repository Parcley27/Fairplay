using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using Fairplay.Audio;
using Fairplay.Discovery;
using Fairplay.Util;

namespace Fairplay.AirPlay;

public sealed class StreamOptions
{
    /// <summary>
    /// How far behind real time the speaker plays (seconds). HomePods report ~70 ms
    /// arrival-to-render, so values below ~0.1 s are experimental.
    /// </summary>
    public double LatencySeconds { get; init; } = 0.15;

    /// <summary>Set the speaker volume (dB, -30…0) when the stream starts; null leaves it unchanged.</summary>
    public double? InitialVolumeDb { get; init; }

    public SenderIdentity Identity { get; init; } = new();

    /// <summary>Diagnostics: also write exactly what is sent (44.1 kHz s16 stereo) to this WAV file.</summary>
    public string? DumpPath { get; init; }

    /// <summary>Diagnostics only: send packets whose authentication tag is invalid.</summary>
    public bool CorruptAudioForTesting { get; init; }
}

public sealed record StreamStats(
    TimeSpan Elapsed, long PacketsSent, long RetransmitRequests, long PacketsResent,
    long TimingRequests, long SilentPackets, double LatencySeconds);

/// <summary>
/// Streams one <see cref="IAudioSource"/> to one AirPlay target — one speaker, or both halves of a
/// stereo pair — until stopped or lost.
///
/// Every receiver gets its own paired session, stream key, ports and retransmit backlog, but they
/// all share one RTP timeline and one sync anchor on our NTP clock, so they render each frame at
/// the same instant (that's what keeps a stereo pair's two sides together).
/// </summary>
public sealed class AirPlayStreamer : IAsyncDisposable
{
    sealed class Receiver(AirPlayDevice device, AirPlaySession session)
    {
        public AirPlayDevice Device { get; } = device;
        public AirPlaySession Session { get; } = session;
        public RtpAudioChannel? Rtp { get; set; }
    }

    readonly AirPlayTarget _target;
    readonly IAudioSource _source;
    readonly StreamOptions _options;
    readonly List<Receiver> _receivers = [];
    TimingServer? _timing;
    Thread? _thread;
    volatile bool _running;
    int _stopped;
    readonly Stopwatch _clock = new();
    long _silentPackets;

    public AirPlayTarget Target => _target;
    public IAudioSource Source => _source;
    public bool IsRunning => _running;
    public double? Volume => _receivers.Select(r => r.Session.ReceiverVolume).FirstOrDefault(v => v.HasValue);
    public int ArrivalToRenderLatencyMs { get; private set; }

    /// <summary>Raised once when streaming ends for any reason other than <see cref="StopAsync"/>.</summary>
    public event Action<string>? Stopped;

    public AirPlayStreamer(AirPlayTarget target, IAudioSource source, StreamOptions? options = null)
    {
        _target = target;
        _source = source;
        _options = options ?? new StreamOptions();
    }

    public StreamStats Stats
    {
        get
        {
            var rtps = _receivers.Select(r => r.Rtp).OfType<RtpAudioChannel>().ToList();
            return new(
                _clock.Elapsed,
                rtps.Sum(r => Interlocked.Read(ref r.PacketsSent)),
                rtps.Sum(r => Interlocked.Read(ref r.RetransmitRequests)),
                rtps.Sum(r => Interlocked.Read(ref r.PacketsResent)),
                _timing?.RequestCount ?? 0,
                Interlocked.Read(ref _silentPackets),
                _options.LatencySeconds);
        }
    }

    /// <summary>One line per receiver: packets, retransmit requests, timing requests.</summary>
    public IEnumerable<string> ReceiverStats() => _receivers.Select(r =>
        $"{r.Device.Name}: {Interlocked.Read(ref r.Rtp!.PacketsSent)} packets, " +
        $"{Interlocked.Read(ref r.Rtp.RetransmitRequests)} retransmit requests ({Interlocked.Read(ref r.Rtp.PacketsResent)} resent), " +
        $"{_timing?.RequestsFrom(r.Device.Address) ?? 0} timing requests");

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_target.Kind == TargetKind.StereoPair)
            Log.Info($"'{_target.DisplayName}' is a stereo pair; streaming to both {string.Join(" and ", _target.Receivers.Select(r => r.Name))}");
        else if (_target.MonoDownmix)
            Log.Info($"'{_target.DisplayName}' is one half of '{_target.PairName}'; sending it a mono mix");

        try
        {
            foreach (var device in _target.Receivers)
            {
                var session = new AirPlaySession(device, _options.Identity);
                var name = device.Name;
                session.Lost += reason => _ = FailAsync(_receivers.Count > 1 ? $"{name}: {reason}" : reason);
                _receivers.Add(new Receiver(device, session));
            }

            // One timing responder for every receiver; they all sync to the same clock.
            _timing = new TimingServer(IPAddress.Any);
            int latencyFrames = (int)Math.Round(_options.LatencySeconds * AudioFormat.SampleRate);

            await Task.WhenAll(_receivers.Select(r => SetupReceiverAsync(r, latencyFrames, ct)));

            var seq0 = (ushort)RandomNumberGenerator.GetInt32(65536);
            var rtp0 = (uint)RandomNumberGenerator.GetInt32(int.MaxValue);
            var t0 = NtpClock.NowSeconds();
            SendSync(NtpClock.QpcFromSeconds(t0), t0, rtp0, latencyFrames, first: true);

            await Task.WhenAll(_receivers.Select(async r =>
            {
                await r.Session.RecordAsync(seq0, rtp0, ct);
                if (_options.InitialVolumeDb is { } vol) await r.Session.SetVolumeAsync(vol, ct);
            }));

            _source.Faulted += reason => _ = FailAsync(reason);
            _source.Start();

            _running = true;
            _clock.Start();
            _thread = new Thread(() => SendLoop(t0, seq0, rtp0, latencyFrames))
            {
                IsBackground = true,
                Name = "AirPlay sender",
                Priority = ThreadPriority.Highest,
            };
            _thread.Start();
            Log.Info($"streaming '{_source.Name}' → '{_target.DisplayName}' (latency {_options.LatencySeconds * 1000:F0} ms)");
        }
        catch
        {
            await CleanupAsync();
            throw;
        }
    }

    async Task SetupReceiverAsync(Receiver r, int latencyFrames, CancellationToken ct)
    {
        await r.Session.ConnectAsync(ct);
        r.Rtp = new RtpAudioChannel(r.Session.LocalAddress) { CorruptAudioForTesting = _options.CorruptAudioForTesting };
        await r.Session.SetupSessionAsync(_timing!.Port, ct);

        // The receiver answers the stream SETUP only once it has synced to our clock, which needs
        // its timing requests to reach us. If none arrive, something (the firewall) is in the way.
        StreamEndpoints ep;
        using (var setupCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            setupCts.CancelAfter(TimeSpan.FromSeconds(6));
            try
            {
                ep = await r.Session.SetupAudioStreamAsync(r.Rtp.LocalControlPort, r.Rtp.StreamKey,
                    Math.Min(11025, latencyFrames), Math.Max(88200, latencyFrames), setupCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && _timing.RequestCount == 0)
            {
                throw new ReceiverCannotReachUsException(
                    $"{r.Device.Name} could not reach this PC to sync its clock (UDP port {_timing.Port}). " +
                    "Windows Firewall is most likely blocking Fairplay — allow it for private and public networks.");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new AirPlayException($"{r.Device.Name} did not finish setting up the audio stream");
            }
        }
        lock (_receivers) ArrivalToRenderLatencyMs = Math.Max(ArrivalToRenderLatencyMs, ep.ArrivalToRenderLatencyMs);
        Log.Info($"{r.Device.Name}: stream ready (data {ep.DataPort}, control {ep.ControlPort}, arrival→render {ep.ArrivalToRenderLatencyMs} ms)");
        r.Rtp.Start(r.Device.Address, ep);
    }

    /// <summary>"RTP time (now − latency) plays at NTP time now", sent identically to every receiver.</summary>
    void SendSync(long qpc, double t0, uint rtp0, int latencyFrames, bool first)
    {
        double now = NtpClock.Seconds(qpc);
        uint rtpNow = rtp0 + (uint)Math.Round((now - t0) * AudioFormat.SampleRate);
        ulong ntp = NtpClock.FromQpc(qpc);
        foreach (var r in _receivers) r.Rtp!.SendSync(rtpNow - (uint)latencyFrames, ntp, rtpNow, first);
    }

    void SendLoop(double t0, ushort seq0, uint rtp0, int latencyFrames)
    {
        WinMm.timeBeginPeriod(1);
        using var mmcss = MmcssScope.Enter("Pro Audio");
        const double packetSeconds = (double)AudioFormat.FramesPerPacket / AudioFormat.SampleRate;
        var floats = new float[AudioFormat.FramesPerPacket * 2];
        var pcm = new byte[AudioFormat.PayloadBytes];
        bool mono = _target.MonoDownmix;
        long n = 0;
        double nextSync = t0 + 1.0;
        using var dump = _options.DumpPath != null
            ? new NAudio.Wave.WaveFileWriter(_options.DumpPath, new NAudio.Wave.WaveFormat(AudioFormat.SampleRate, 16, 2))
            : null;
        var le = dump != null ? new byte[AudioFormat.PayloadBytes] : null;
        try
        {
            while (_running)
            {
                long q = Stopwatch.GetTimestamp();
                double now = NtpClock.Seconds(q);

                // If we fell far behind (machine suspended, debugger), skip ahead instead of bursting.
                double due = t0 + n * packetSeconds;
                if (now - due > 1.0)
                {
                    long skip = (long)((now - due) / packetSeconds);
                    Log.Warn($"sender stalled {now - due:F2}s; skipping {skip} packets");
                    n += skip;
                }

                while (t0 + n * packetSeconds <= now && _running)
                {
                    bool real = _source.Read(floats);
                    if (!real) Interlocked.Increment(ref _silentPackets);
                    if (mono) DownmixToMono(floats);
                    ToS16BigEndian(floats, pcm);
                    if (dump != null)
                    {
                        for (int i = 0; i < pcm.Length; i += 2) { le![i] = pcm[i + 1]; le[i + 1] = pcm[i]; }
                        dump.Write(le!, 0, le!.Length);
                    }
                    var seq = (ushort)(seq0 + n);
                    var ts = rtp0 + (uint)(n * AudioFormat.FramesPerPacket);
                    foreach (var r in _receivers) r.Rtp!.SendAudio(seq, ts, pcm, n == 0);
                    n++;
                }

                if (now >= nextSync)
                {
                    SendSync(q, t0, rtp0, latencyFrames, first: false);
                    nextSync += 1.0;
                    if (nextSync < now) nextSync = now + 1.0;
                }

                // Millisecond precision is plenty: the receiver buffers the whole latency window.
                Thread.Sleep(1);
            }
        }
        catch (Exception e)
        {
            Log.Error($"sender loop crashed: {e}");
            _ = FailAsync($"sender error: {e.Message}");
        }
        finally
        {
            WinMm.timeEndPeriod(1);
        }
    }

    static void DownmixToMono(Span<float> interleaved)
    {
        for (int i = 0; i < interleaved.Length; i += 2)
        {
            float m = 0.5f * (interleaved[i] + interleaved[i + 1]);
            interleaved[i] = m;
            interleaved[i + 1] = m;
        }
    }

    static void ToS16BigEndian(ReadOnlySpan<float> src, Span<byte> dst)
    {
        for (int i = 0; i < src.Length; i++)
        {
            float f = src[i] * 32767f;
            short s = f >= 32767f ? short.MaxValue : f <= -32768f ? short.MinValue : (short)MathF.Round(f);
            BinaryPrimitives.WriteInt16BigEndian(dst[(2 * i)..], s);
        }
    }

    /// <summary>Set the volume on every receiver (a pair keeps both sides equal).</summary>
    public Task SetVolumeAsync(double db, CancellationToken ct = default) =>
        Task.WhenAll(_receivers.Select(r => r.Session.SetVolumeAsync(db, ct)));

    async Task FailAsync(string reason)
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        await CleanupAsync();
        Stopped?.Invoke(reason);
    }

    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        await CleanupAsync();
    }

    async Task CleanupAsync()
    {
        _running = false;
        if (_thread != null && _thread != Thread.CurrentThread) _thread.Join(1000);
        _thread = null;
        try { _source.Stop(); } catch { }
        await Task.WhenAll(_receivers.Select(async r =>
        {
            await r.Session.DisposeAsync();
            r.Rtp?.Dispose();
        }));
        _timing?.Dispose();
        _clock.Stop();
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
