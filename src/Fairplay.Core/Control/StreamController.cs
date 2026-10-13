using Fairplay.AirPlay;
using Fairplay.Audio;
using Fairplay.Discovery;
using Fairplay.Util;

namespace Fairplay.Control;

public enum StreamState { Idle, Connecting, Streaming, Reconnecting }

/// <summary>
/// Everything a front end needs: background discovery, start/stop, automatic reconnection
/// (without stealing a speaker another device has taken over), volume and volume sync.
/// All public members are safe to call from the UI thread; <see cref="Changed"/> may fire on any thread.
/// </summary>
public sealed class StreamController : IAsyncDisposable
{
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly CancellationTokenSource _life = new();
    AirPlayStreamer? _streamer;
    LoopbackSource? _source;
    CancellationTokenSource? _reconnectCts;
    List<AirPlayTarget> _targets = [];
    DateTime _lastWindowsVolumePush;
    double? _pendingSyncVolume;

    public Settings Settings { get; }
    public StreamState State { get; private set; } = StreamState.Idle;
    public AirPlayTarget? ActiveTarget { get; private set; }
    public string Status { get; private set; } = "Not streaming";
    public IReadOnlyList<AirPlayTarget> Targets => _targets;
    public bool IsDiscovering { get; private set; }

    /// <summary>Speaker volume in AirPlay dB (-30…0, -144 = muted) if known.</summary>
    public double? SpeakerVolume { get; private set; }
    public EndpointVolume? WindowsVolume => _source?.Volume;
    public LoopbackSource? Source => _source;
    public AirPlayStreamer? Streamer => _streamer;

    public event Action? Changed;
    /// <summary>User-facing notification (title, message, isError). Raised only for failures.</summary>
    public event Action<string, string, bool>? Notify;

    public StreamController(Settings settings)
    {
        Settings = settings;
        _ = DiscoveryLoop();
    }

    string? _lastLoggedStatus;

    void Raise()
    {
        if (Status != _lastLoggedStatus)
        {
            _lastLoggedStatus = Status;
            Log.Info($"[{State}] {Status}");
        }
        Changed?.Invoke();
    }

    void Announce(string title, string message, bool error) => Notify?.Invoke(title, message, error);

    async Task DiscoveryLoop()
    {
        while (!_life.IsCancellationRequested)
        {
            await RefreshTargetsAsync();
            try { await Task.Delay(State == StreamState.Idle ? TimeSpan.FromSeconds(20) : TimeSpan.FromSeconds(60), _life.Token); }
            catch (OperationCanceledException) { return; }
        }
    }

    public async Task RefreshTargetsAsync()
    {
        if (IsDiscovering) return;
        IsDiscovering = true;
        Raise();
        try
        {
            var found = await AirPlayBrowser.DiscoverTargetsAsync(TimeSpan.FromSeconds(2.5), _life.Token);
            // mDNS is lossy: keep previously seen speakers unless we've missed them twice in a row.
            var merged = found.ToList();
            foreach (var old in _targets)
            {
                if (merged.Any(t => t.Id == old.Id)) continue;
                int misses = _missed.GetValueOrDefault(old.Id) + 1;
                _missed[old.Id] = misses;
                if (misses <= 2 || old.Id == ActiveTarget?.Id) merged.Add(old);
            }
            foreach (var t in found) _missed.Remove(t.Id);
            _targets = AirPlayTarget.Sort(merged);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Warn($"discovery failed: {e.Message}");
        }
        finally
        {
            IsDiscovering = false;
            Raise();
        }
    }

    readonly Dictionary<string, int> _missed = new();

    public AirPlayTarget? FindTarget(string? id) => id == null ? null : _targets.FirstOrDefault(t => t.Id == id);

    public Task StartAsync(AirPlayTarget target) => StartCoreAsync(target, isReconnect: false, CancellationToken.None);

    async Task StartCoreAsync(AirPlayTarget target, bool isReconnect, CancellationToken reconnectToken)
    {
        await _gate.WaitAsync();
        try
        {
            // A reconnect that was queued behind a user's Stop must not resurrect the stream.
            if (isReconnect && reconnectToken.IsCancellationRequested) return;
            if (!isReconnect) CancelReconnect();
            await StopCoreAsync();
            ActiveTarget = target;
            State = isReconnect ? StreamState.Reconnecting : StreamState.Connecting;
            Status = $"Connecting to {target.DisplayName}…";
            Raise();

            var source = new LoopbackSource(Settings.SourceDeviceId) { CompensateVolume = Settings.SyncVolume };
            source.VolumeChanged += OnWindowsVolumeChanged;
            var streamer = new AirPlayStreamer(target, source, new StreamOptions { LatencySeconds = Settings.LatencyMs / 1000.0 });
            streamer.Stopped += reason => _ = OnLostAsync(target, streamer, reason);
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
                cts.CancelAfter(TimeSpan.FromSeconds(15));
                await streamer.StartAsync(cts.Token);
            }
            catch (Exception e)
            {
                await streamer.DisposeAsync();
                State = StreamState.Idle;
                Status = $"Couldn't connect to {target.DisplayName}";
                Raise();
                Log.Error($"start failed: {e.Message}");
                throw;
            }
            _source = source;
            _streamer = streamer;
            SpeakerVolume = streamer.Volume;
            State = StreamState.Streaming;
            Status = $"Streaming to {target.DisplayName}";
            Settings.LastTargetId = target.Id;
            Settings.LastTargetName = target.DisplayName;
            Settings.Save();
            if (Settings.SyncVolume && source.Volume is { } v) await PushSyncedVolumeAsync(v);
            Raise();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            CancelReconnect();
            await StopCoreAsync();
            ActiveTarget = null;
            State = StreamState.Idle;
            Status = "Not streaming";
            Raise();
        }
        finally
        {
            _gate.Release();
        }
    }

    async Task StopCoreAsync()
    {
        var s = _streamer;
        _streamer = null;
        if (_source != null) _source.VolumeChanged -= OnWindowsVolumeChanged;
        _source = null;
        if (s != null) await s.StopAsync();
    }

    void CancelReconnect()
    {
        _reconnectCts?.Cancel();
        _reconnectCts = null;
    }

    async Task OnLostAsync(AirPlayTarget target, AirPlayStreamer which, string reason)
    {
        await _gate.WaitAsync();
        try
        {
            if (_streamer != which) return; // already replaced or stopped
            _streamer = null;
            if (_source != null) _source.VolumeChanged -= OnWindowsVolumeChanged;
            _source = null;
            State = StreamState.Reconnecting;
            Status = $"Lost {target.DisplayName} — reconnecting…";
            Raise();
            _reconnectCts = new CancellationTokenSource();
            _ = ReconnectLoop(target, reason, _reconnectCts.Token);
        }
        finally
        {
            _gate.Release();
        }
    }

    async Task ReconnectLoop(AirPlayTarget target, string reason, CancellationToken ct)
    {
        int[] delays = [1, 2, 3, 4, 8, 15, 30, 30, 30];
        DateTime? busySince = null;
        foreach (var delay in delays)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(delay), ct); } catch (OperationCanceledException) { return; }

            await RefreshTargetsAsync();
            var fresh = FindTarget(target.Id) ?? target;

            // Did someone else start playing on the speaker? Then step aside instead of stealing it back.
            // (Our own dead session can linger on the receiver for a few seconds, hence the grace period.)
            if (await IsBusyWithAnotherSenderAsync(fresh.Receivers, ct))
            {
                busySince ??= DateTime.UtcNow;
                if (DateTime.UtcNow - busySince > TimeSpan.FromSeconds(12))
                {
                    await GiveUpAsync(fresh, $"{fresh.DisplayName} is now playing from another device.");
                    return;
                }
                Status = $"{target.DisplayName} is busy — waiting…";
                Raise();
                continue;
            }
            busySince = null;
            try
            {
                await StartCoreAsync(fresh, isReconnect: true, ct);
                return;
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                Log.Warn($"reconnect attempt failed: {e.Message}");
                State = StreamState.Reconnecting;
                Status = $"Lost {target.DisplayName} — retrying…";
                Raise();
            }
            if (ct.IsCancellationRequested) return;
        }
        await GiveUpAsync(target, $"Lost connection to {target.DisplayName} ({reason}).");
    }

    async Task GiveUpAsync(AirPlayTarget target, string message)
    {
        await _gate.WaitAsync();
        try
        {
            if (State != StreamState.Reconnecting) return;
            ActiveTarget = null;
            State = StreamState.Idle;
            Status = "Not streaming";
            Raise();
            Announce("Disconnected", message, true);
        }
        finally
        {
            _gate.Release();
        }
    }

    static async Task<bool> IsBusyWithAnotherSenderAsync(IEnumerable<AirPlayDevice> devices, CancellationToken ct)
    {
        var results = await Task.WhenAll(devices.Select(d => IsBusyWithAnotherSenderAsync(d, ct)));
        return results.Any(b => b);
    }

    const long SessionActive = 0x20000;
    const long TightSyncIsGroupLeader = 0x2000;

    /// <summary>
    /// Status flag bit 17 = the receiver has an active sender session. The stereo-pair member that
    /// leads tight sync (bit 13) reports that bit permanently — it holds a session with its
    /// partner — so it's ignored there; the other member still reveals a foreign sender.
    /// </summary>
    static async Task<bool> IsBusyWithAnotherSenderAsync(AirPlayDevice device, CancellationToken ct)
    {
        try
        {
            using var c = await RtspConnection.ConnectAsync(device.Address, device.Port, ct);
            var r = await c.RequestAsync("GET", "/info", ct: ct);
            if (r.PlistDict.TryGetValue("statusFlags", out var f) && f is long flags)
                return (flags & SessionActive) != 0 && (flags & TightSyncIsGroupLeader) == 0;
        }
        catch (Exception e)
        {
            Log.Debug($"busy check failed: {e.Message}");
        }
        return false;
    }

    // ------------------------------------------------------------------ volume

    /// <summary>Set the speaker volume, 0…1 (linear slider position, like iOS).</summary>
    public async Task SetSpeakerVolumeAsync(double fraction)
    {
        double db = fraction <= 0.001 ? -144 : -30 + 30 * Math.Clamp(fraction, 0, 1);
        SpeakerVolume = db;
        var s = _streamer;
        if (s != null)
        {
            try { await s.SetVolumeAsync(db); }
            catch (Exception e) { Log.Warn($"volume change failed: {e.Message}"); }
        }
        Raise();
    }

    public static double VolumeToFraction(double? db) => db is null or <= -30 ? 0 : Math.Clamp((db.Value + 30) / 30, 0, 1);

    void OnWindowsVolumeChanged(EndpointVolume v)
    {
        Raise();
        if (!Settings.SyncVolume) return;
        _ = PushSyncedVolumeAsync(v);
    }

    async Task PushSyncedVolumeAsync(EndpointVolume v)
    {
        double fraction = v.Muted ? 0 : v.Scalar;
        // Coalesce slider drags: at most ~10 updates per second, always ending on the final value.
        _pendingSyncVolume = fraction;
        var since = DateTime.UtcNow - _lastWindowsVolumePush;
        if (since < TimeSpan.FromMilliseconds(100))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100) - since);
            if (_pendingSyncVolume != fraction) return;
        }
        _lastWindowsVolumePush = DateTime.UtcNow;
        await SetSpeakerVolumeAsync(fraction);
    }

    /// <summary>Apply changed settings (source, latency, sync) to a running stream by restarting it.</summary>
    public async Task ApplySettingsAsync()
    {
        Settings.Save();
        if (ActiveTarget is { } t && State == StreamState.Streaming)
            await StartAsync(t);
        else
            Raise();
    }

    public async ValueTask DisposeAsync()
    {
        _life.Cancel();
        CancelReconnect();
        await StopCoreAsync();
    }
}
