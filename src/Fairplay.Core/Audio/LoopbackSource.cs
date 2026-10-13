using System.Runtime.InteropServices;
using Fairplay.Util;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace Fairplay.Audio;

/// <summary>A Windows playback endpoint we can capture from.</summary>
public sealed record RenderDeviceInfo(string Id, string Name, bool IsDefault);

/// <summary>Windows' volume for the captured endpoint. <see cref="Scalar"/> is the slider position (0..1).</summary>
public sealed record EndpointVolume(float Scalar, float LevelDb, bool Muted, bool AppliedInHardware);

/// <summary>
/// Captures everything Windows plays on a render endpoint (WASAPI loopback) and delivers it as
/// 44.1 kHz stereo on the sender's clock.
///
/// Pipeline: endpoint mix format → stereo float → <see cref="Resampler"/> → FIFO → <see cref="Read"/>.
/// The FIFO level is held near a small target by trimming the resampler ratio, which slaves the
/// sound card's clock to the AirPlay sender clock with no drops or repeats. A silent render stream
/// on the same endpoint keeps loopback packets flowing while nothing is playing.
/// </summary>
public sealed class LoopbackSource : IAudioSource
{
    const int OutRate = 44100;

    readonly string? _deviceId; // null = follow the default output
    double _targetSeconds;
    readonly object _fifoLock = new();
    float[] _fifo = new float[OutRate * 2 * 4];
    int _fifoStart, _fifoCount; // in floats

    Thread? _thread;
    volatile bool _running;
    volatile bool _restart;
    volatile Resampler? _resampler;
    MMDeviceEnumerator? _enumerator;
    DefaultDeviceWatcher? _watcher;

    bool _playing;
    double _smoothedLevel;
    long _underruns, _overflows, _discontinuities, _captureFrames;

    public string Name { get; private set; } = "System audio";
    public string? CurrentDeviceName { get; private set; }
    public long Underruns => Interlocked.Read(ref _underruns);
    public long Overflows => Interlocked.Read(ref _overflows);
    public long Discontinuities => Interlocked.Read(ref _discontinuities);
    public double FifoMilliseconds { get { lock (_fifoLock) return _fifoCount / 2 * 1000.0 / OutRate; } }
    public double Trim => _resampler?.Trim ?? 0;

    /// <summary>Peak level of the most recent capture (0..1) — for a UI meter.</summary>
    public float Peak { get; private set; }

    /// <summary>
    /// Undo Windows' software volume on the captured audio, so the stream stays full scale and the
    /// speaker's own volume does the attenuation (used with volume sync).
    /// </summary>
    public bool CompensateVolume { get; set; }

    public EndpointVolume? Volume { get; private set; }

    /// <summary>Raised (on a COM thread) when the captured endpoint's volume or mute changes.</summary>
    public event Action<EndpointVolume>? VolumeChanged;

    public event Action<string>? Faulted;

    volatile float _makeupGain = 1f;

    public LoopbackSource(string? deviceId = null, double targetBufferSeconds = 0.02)
    {
        _deviceId = deviceId;
        _targetSeconds = targetBufferSeconds;
    }

    public static List<RenderDeviceInfo> ListRenderDevices()
    {
        using var en = new MMDeviceEnumerator();
        string? def = null;
        try { def = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID; } catch { }
        var list = new List<RenderDeviceInfo>();
        foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            list.Add(new RenderDeviceInfo(d.ID, d.FriendlyName, d.ID == def));
            d.Dispose();
        }
        return list;
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _enumerator = new MMDeviceEnumerator();
        if (_deviceId == null)
        {
            _watcher = new DefaultDeviceWatcher(() =>
            {
                Log.Info("default output device changed — switching capture");
                _restart = true;
            });
            _enumerator.RegisterEndpointNotificationCallback(_watcher);
        }
        _thread = new Thread(CaptureLoop) { IsBackground = true, Name = "WASAPI loopback", Priority = ThreadPriority.Highest };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        _thread?.Join(2000);
        _thread = null;
        if (_watcher != null && _enumerator != null)
        {
            try { _enumerator.UnregisterEndpointNotificationCallback(_watcher); } catch { }
        }
        _enumerator?.Dispose();
        _enumerator = null;
    }

    void CaptureLoop()
    {
        WinMm.timeBeginPeriod(1);
        using var mmcss = MmcssScope.Enter("Pro Audio");
        int failures = 0;
        try
        {
            while (_running)
            {
                _restart = false;
                try
                {
                    RunCapture();
                    failures = 0;
                }
                catch (Exception e) when (_running)
                {
                    failures++;
                    Log.Warn($"loopback capture error: {e.Message}");
                    if (failures >= 20)
                    {
                        Faulted?.Invoke($"audio capture failed: {e.Message}");
                        return;
                    }
                    Thread.Sleep(Math.Min(2000, 250 * failures));
                }
            }
        }
        finally
        {
            WinMm.timeEndPeriod(1);
        }
    }

    void RunCapture()
    {
        using var device = _deviceId == null
            ? _enumerator!.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
            : _enumerator!.GetDevice(_deviceId);
        CurrentDeviceName = device.FriendlyName;
        Name = _deviceId == null ? $"System audio ({device.FriendlyName})" : device.FriendlyName;

        var client = device.AudioClient;
        var format = client.MixFormat;
        int channels = format.Channels;
        int rate = format.SampleRate;
        var kind = SampleKind(format);
        Log.Info($"capturing '{device.FriendlyName}': {rate} Hz, {channels} ch, {format.BitsPerSample}-bit {kind}");

        // 100 ms shared-mode loopback buffer; we drain it every few ms.
        client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.Loopback, 1_000_000, 0, format, Guid.Empty);
        var capture = client.AudioCaptureClient;

        // Keep the engine rendering so loopback never goes quiet (it stops delivering otherwise).
        WasapiOut? keepAlive = null;
        try
        {
            keepAlive = new WasapiOut(device, AudioClientShareMode.Shared, true, 200);
            keepAlive.Init(new SilenceProvider(format));
            keepAlive.Play();
        }
        catch (Exception e)
        {
            Log.Debug($"keep-alive stream unavailable: {e.Message}");
            keepAlive?.Dispose();
            keepAlive = null;
        }

        var endpointVolume = device.AudioEndpointVolume;
        bool hardwareVolume = ((int)endpointVolume.HardwareSupport & 1) != 0;
        void PublishVolume()
        {
            var v = new EndpointVolume(endpointVolume.MasterVolumeLevelScalar, endpointVolume.MasterVolumeLevel, endpointVolume.Mute, hardwareVolume);
            // Software volume is applied before loopback; hardware volume is not.
            _makeupGain = hardwareVolume || v.Muted ? 1f : (float)Math.Pow(10, Math.Min(60, -v.LevelDb) / 20);
            Volume = v;
            VolumeChanged?.Invoke(v);
        }
        AudioEndpointVolumeNotificationDelegate onVolume = _ =>
        {
            try { PublishVolume(); } catch (Exception e) { Log.Debug($"volume notification: {e.Message}"); }
        };
        endpointVolume.OnVolumeNotification += onVolume;
        PublishVolume();

        var resampler = new Resampler(rate, OutRate);
        _resampler = resampler;
        lock (_fifoLock)
        {
            _fifoStart = _fifoCount = 0;
            _playing = false;
        }

        float[] stereo = new float[rate * 2];
        FrameSink sink = WriteFifo;
        client.Start();
        try
        {
            while (_running && !_restart)
            {
                Thread.Sleep(3);
                int packet;
                while ((packet = capture.GetNextPacketSize()) > 0)
                {
                    var ptr = capture.GetBuffer(out int frames, out var flags);
                    if ((flags & AudioClientBufferFlags.DataDiscontinuity) != 0 && _captureFrames > 0)
                    {
                        Interlocked.Increment(ref _discontinuities);
                        Log.Debug("loopback: capture discontinuity (engine glitch)");
                    }
                    _captureFrames += frames;
                    if (stereo.Length < frames * 2) stereo = new float[frames * 2];
                    var dst = stereo.AsSpan(0, frames * 2);
                    if ((flags & AudioClientBufferFlags.Silent) != 0) dst.Clear();
                    else ToStereo(ptr, frames, channels, kind, format.BitsPerSample, dst);
                    capture.ReleaseBuffer(frames);

                    if (CompensateVolume)
                    {
                        float g = _makeupGain;
                        if (g != 1f) for (int i = 0; i < dst.Length; i++) dst[i] *= g;
                    }

                    float peak = 0;
                    foreach (var s in dst) peak = Math.Max(peak, Math.Abs(s));
                    Peak = peak;

                    resampler.Process(dst, sink);
                }
            }
        }
        finally
        {
            endpointVolume.OnVolumeNotification -= onVolume;
            try { client.Stop(); } catch { }
            client.Dispose();
            keepAlive?.Stop();
            keepAlive?.Dispose();
        }
    }

    enum Kind { Float, Int }

    static readonly Guid IeeeFloatSubtype = new("00000003-0000-0010-8000-00aa00389b71");

    static Kind SampleKind(WaveFormat f)
    {
        if (f.Encoding == WaveFormatEncoding.IeeeFloat) return Kind.Float;
        if (f is WaveFormatExtensible ext && ext.SubFormat == IeeeFloatSubtype) return Kind.Float;
        return Kind.Int;
    }

    static unsafe void ToStereo(IntPtr ptr, int frames, int channels, Kind kind, int bits, Span<float> dst)
    {
        int bytesPerSample = bits / 8;
        byte* p = (byte*)ptr;
        for (int i = 0; i < frames; i++)
        {
            float Sample(int ch)
            {
                byte* s = p + (i * channels + ch) * bytesPerSample;
                if (kind == Kind.Float) return bits == 64 ? (float)*(double*)s : *(float*)s;
                return bytesPerSample switch
                {
                    2 => *(short*)s / 32768f,
                    3 => ((s[0] << 8) | (s[1] << 16) | (s[2] << 24)) / 2147483648f,
                    4 => *(int*)s / 2147483648f,
                    _ => 0,
                };
            }

            float l, r;
            if (channels == 1) l = r = Sample(0);
            else if (channels == 2) { l = Sample(0); r = Sample(1); }
            else
            {
                // FL FR FC LFE BL BR (SL SR): ITU-style fold-down, LFE dropped.
                const float k = 0.7071f;
                l = Sample(0); r = Sample(1);
                if (channels >= 3) { float c = Sample(2) * k; l += c; r += c; }
                if (channels >= 6) { l += Sample(4) * k; r += Sample(5) * k; }
                if (channels >= 8) { l += Sample(6) * k; r += Sample(7) * k; }
            }
            dst[2 * i] = l;
            dst[2 * i + 1] = r;
        }
    }

    void WriteFifo(ReadOnlySpan<float> data)
    {
        lock (_fifoLock)
        {
            int cap = _fifo.Length;
            if (_fifoCount + data.Length > cap)
            {
                // Sender stalled; drop the oldest audio rather than grow without bound.
                int drop = _fifoCount + data.Length - cap;
                drop += drop & 1;
                _fifoStart = (_fifoStart + drop) % cap;
                _fifoCount -= drop;
            }
            int end = (_fifoStart + _fifoCount) % cap;
            int first = Math.Min(data.Length, cap - end);
            data[..first].CopyTo(_fifo.AsSpan(end));
            data[first..].CopyTo(_fifo.AsSpan(0));
            _fifoCount += data.Length;
        }
    }

    /// <summary>Called by the sender every packet (352 frames ≈ 8 ms) on the sender clock.</summary>
    public bool Read(Span<float> dest)
    {
        int need = dest.Length;
        double target = _targetSeconds * OutRate * 2; // floats
        lock (_fifoLock)
        {
            int cap = _fifo.Length;
            if (!_playing)
            {
                if (_fifoCount < target + need)
                {
                    dest.Clear();
                    return false;
                }
                _playing = true;
                _smoothedLevel = _fifoCount;
            }

            // Way too much buffered (e.g. after a stall): jump back to the target latency.
            if (_fifoCount > target + need + OutRate * 2 * 0.15)
            {
                int drop = _fifoCount - (int)target - need;
                drop -= drop & 1;
                Log.Debug($"loopback: {drop / 2 * 1000.0 / OutRate:F0} ms backlog dropped to restore latency");
                _fifoStart = (_fifoStart + drop) % cap;
                _fifoCount -= drop;
                _smoothedLevel = _fifoCount;
                Interlocked.Increment(ref _overflows);
            }

            int take = Math.Min(need, _fifoCount);
            int first = Math.Min(take, cap - _fifoStart);
            _fifo.AsSpan(_fifoStart, first).CopyTo(dest);
            _fifo.AsSpan(0, take - first).CopyTo(dest[first..]);
            _fifoStart = (_fifoStart + take) % cap;
            _fifoCount -= take;

            if (take < need)
            {
                dest[take..].Clear();
                _playing = false;
                Interlocked.Increment(ref _underruns);
                // Capture couldn't keep up with this little buffering: give it 5 ms more (max 60 ms).
                if (_targetSeconds < 0.06)
                {
                    _targetSeconds = Math.Min(0.06, _targetSeconds + 0.005);
                    Log.Info($"loopback underrun — capture buffer raised to {_targetSeconds * 1000:F0} ms");
                }
                return take > 0;
            }

            // Clock slaving: steer the FIFO level (measured after the read) towards the target.
            _smoothedLevel += (_fifoCount - _smoothedLevel) * 0.01;
            double errorSeconds = (_smoothedLevel - target) / (OutRate * 2.0);
            var rs = _resampler;
            if (rs != null) rs.Trim = Math.Clamp(errorSeconds * 0.05, -0.003, 0.003);
            return true;
        }
    }

    public void Dispose() => Stop();

    sealed class DefaultDeviceWatcher(Action changed) : IMMNotificationClient
    {
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (flow == DataFlow.Render && role == Role.Multimedia) changed();
        }
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
        public void OnDeviceAdded(string pwstrDeviceId) { }
        public void OnDeviceRemoved(string deviceId) { }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }
}

internal static class WinMm
{
    [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint ms);
}

/// <summary>Registers the current thread with MMCSS so audio work gets scheduling priority.</summary>
internal sealed class MmcssScope : IDisposable
{
    [DllImport("avrt.dll", CharSet = CharSet.Unicode)] static extern IntPtr AvSetMmThreadCharacteristicsW(string task, ref uint index);
    [DllImport("avrt.dll")] static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);

    readonly IntPtr _handle;
    MmcssScope(IntPtr h) => _handle = h;

    public static MmcssScope Enter(string task)
    {
        uint idx = 0;
        try { return new MmcssScope(AvSetMmThreadCharacteristicsW(task, ref idx)); }
        catch { return new MmcssScope(IntPtr.Zero); }
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero) AvRevertMmThreadCharacteristics(_handle);
    }
}
