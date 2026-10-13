using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Fairplay.Crypto;
using Fairplay.Discovery;
using Fairplay.Util;

namespace Fairplay.AirPlay;

public class AirPlayException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The receiver never reached our timing port — almost always Windows Firewall.</summary>
public sealed class ReceiverCannotReachUsException(string message) : AirPlayException(message);

/// <summary>Who we claim to be in SETUP. The receiver shows <see cref="Name"/> in the Home app.</summary>
public sealed class SenderIdentity
{
    public string Name { get; init; } = Environment.MachineName;
    public string DeviceId { get; init; } = StableMac();
    public string Model { get; init; } = "Windows1,1";
    public string OsName { get; init; } = "Windows";
    public string OsVersion { get; init; } = Environment.OSVersion.Version.ToString();

    /// <summary>A locally-administered MAC-style id that stays the same across runs on this PC.</summary>
    static string StableMac()
    {
        var seed = Encoding.UTF8.GetBytes(Environment.MachineName + "/" + Environment.UserName + "/fairplay");
        var h = SHA256.HashData(seed);
        h[0] = (byte)((h[0] | 0x02) & 0xFE);
        return string.Join(":", h.Take(6).Select(b => b.ToString("X2")));
    }
}

/// <summary>Result of the stream SETUP: where to send audio and control traffic.</summary>
public sealed record StreamEndpoints(int DataPort, int ControlPort, int ArrivalToRenderLatencyMs);

/// <summary>
/// One AirPlay 2 sender session against one receiver: transient HomeKit pairing, encrypted RTSP,
/// SETUP (session + realtime audio stream), RECORD, volume, feedback keep-alive, TEARDOWN.
/// Audio itself is sent by <see cref="RtpAudioChannel"/>.
/// </summary>
public sealed class AirPlaySession : IAsyncDisposable
{
    readonly AirPlayDevice _device;
    readonly SenderIdentity _identity;
    RtspConnection? _rtsp;
    EventChannel? _events;
    byte[]? _sharedSecret;
    readonly uint _sessionId = (uint)RandomNumberGenerator.GetInt32(int.MaxValue);
    readonly string _sessionUuid = Guid.NewGuid().ToString().ToUpperInvariant();
    CancellationTokenSource? _feedbackCts;

    public AirPlayDevice Device => _device;
    public IPAddress LocalAddress => _rtsp?.LocalAddress ?? IPAddress.Any;
    public uint SessionId => _sessionId;
    public double? ReceiverVolume { get; private set; }

    /// <summary>Raised once if the receiver drops us (connection closed, feedback failing, takeover).</summary>
    public event Action<string>? Lost;
    public event Action<string, Dictionary<string, object?>>? ReceiverEvent;

    int _lostRaised;

    public AirPlaySession(AirPlayDevice device, SenderIdentity? identity = null)
    {
        _device = device;
        _identity = identity ?? new SenderIdentity();
    }

    string Uri => $"rtsp://{_rtsp!.LocalAddress}/{_sessionId}";

    public async Task ConnectAsync(CancellationToken ct)
    {
        Log.Info($"connecting to {_device}");
        _rtsp = await RtspConnection.ConnectAsync(_device.Address, _device.Port, ct);

        var info = await _rtsp.RequestAsync("GET", "/info", ct: ct);
        if (info.Status == 200 && info.PlistDict.TryGetValue("initialVolume", out var iv) && iv is double vol)
            ReceiverVolume = vol;

        await PairTransientAsync(ct);
    }

    /// <summary>
    /// HomeKit "transient" pair-setup (M1–M4 only) with the fixed PIN 3939. Works whenever the
    /// speaker's AirPlay access is "Everyone" or "Anyone on the same network". The SRP session
    /// key then seeds every channel key.
    /// </summary>
    async Task PairTransientAsync(CancellationToken ct)
    {
        var hdr = new Dictionary<string, string> { ["X-Apple-HKP"] = "4", ["Connection"] = "keep-alive" };
        const string ct8 = "application/octet-stream";

        for (int attempt = 1; ; attempt++)
        {
            var m1 = Tlv8.Encode((Tlv8.Method, [0x00]), (Tlv8.State, [0x01]), (Tlv8.Flags, [Tlv8.FlagTransient]));
            var r2 = await _rtsp!.RequestAsync("POST", "/pair-setup", m1, ct8, hdr, "HTTP/1.1", ct);
            var m2 = Tlv8.Decode(r2.Body);
            if (r2.Status != 200 || m2.ContainsKey(Tlv8.Error))
                throw new AirPlayException(PairingError(r2, m2, "M2"));

            var srp = new SrpClient("Pair-Setup", "3939");
            srp.Process(m2[Tlv8.Salt], m2[Tlv8.PublicKey]);

            var m3 = Tlv8.Encode((Tlv8.State, [0x03]), (Tlv8.PublicKey, srp.PublicKey), (Tlv8.Proof, srp.Proof));
            var r4 = await _rtsp.RequestAsync("POST", "/pair-setup", m3, ct8, hdr, "HTTP/1.1", ct);
            var m4 = Tlv8.Decode(r4.Body);
            if (r4.Status == 200 && !m4.ContainsKey(Tlv8.Error) && m4.TryGetValue(Tlv8.Proof, out var serverProof))
            {
                if (!srp.VerifyServerProof(serverProof))
                    throw new AirPlayException("pairing failed: receiver proof did not verify");
                _sharedSecret = srp.SessionKey;
                break;
            }
            // A leading-zero encoding mismatch of A/B can (rarely) break the proof; a fresh key fixes it.
            if (attempt >= 3) throw new AirPlayException(PairingError(r4, m4, "M4"));
            Log.Warn($"pair-setup M4 rejected (attempt {attempt}); retrying with a new key");
        }

        _rtsp.EnableEncryption(
            HapKeys.Derive(_sharedSecret, "Control-Salt", "Control-Write-Encryption-Key"),
            HapKeys.Derive(_sharedSecret, "Control-Salt", "Control-Read-Encryption-Key"));
        Log.Info("paired (transient) — control channel encrypted");
    }

    static string PairingError(RtspMessage r, Dictionary<byte, byte[]> tlv, string stage)
    {
        if (r.Status == 470 || r.Status == 401 || r.Status == 403)
            return $"the speaker refused pairing ({r.Status}). In the Home app set Home Settings → Speakers & TV → \"Allow Speaker & TV Access\" to \"Everyone\" or \"Anyone on the Same Network\".";
        if (tlv.TryGetValue(Tlv8.Error, out var e) && e.Length > 0)
            return e[0] switch
            {
                0x02 => $"pairing failed at {stage}: authentication error (the speaker may require a password)",
                0x03 => $"pairing failed at {stage}: speaker asked us to back off — try again in a moment",
                0x06 => $"pairing failed at {stage}: speaker is busy with another pairing",
                _ => $"pairing failed at {stage}: TLV error {e[0]}",
            };
        return $"pairing failed at {stage}: HTTP {r.Status}";
    }

    /// <summary>First SETUP: session-level parameters, our NTP timing port; opens the event channel.</summary>
    public async Task SetupSessionAsync(int timingPort, CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["deviceID"] = _identity.DeviceId,
            ["sessionUUID"] = _sessionUuid,
            ["timingPort"] = timingPort,
            ["timingProtocol"] = "NTP",
            ["isMultiSelectAirPlay"] = true,
            ["groupContainsGroupLeader"] = false,
            ["macAddress"] = _identity.DeviceId,
            ["model"] = _identity.Model,
            ["name"] = _identity.Name,
            ["osBuildVersion"] = _identity.OsVersion,
            ["osName"] = _identity.OsName,
            ["osVersion"] = _identity.OsVersion,
            ["senderSupportsRelay"] = false,
            ["sourceVersion"] = "690.7.1",
            ["statsCollectionEnabled"] = false,
        };
        var r = await _rtsp!.RequestPlistAsync("SETUP", Uri, body, ct: ct);
        if (r.Status != 200) throw new AirPlayException($"SETUP (session) failed: {r.StartLine}");
        var d = r.PlistDict;
        int eventPort = d.TryGetValue("eventPort", out var ep) ? Convert.ToInt32(ep) : 0;
        if (eventPort > 0)
        {
            _events = await EventChannel.ConnectAsync(_device.Address, eventPort, _sharedSecret!, ct);
            _events.CommandReceived += OnReceiverEvent;
            _events.Closed += () => RaiseLost("receiver closed the event channel");
        }
    }

    /// <summary>Second SETUP: one realtime (type 96) PCM stream encrypted with <paramref name="streamKey"/>.</summary>
    public async Task<StreamEndpoints> SetupAudioStreamAsync(int controlPort, byte[] streamKey, int latencyMinFrames, int latencyMaxFrames, CancellationToken ct)
    {
        var stream = new Dictionary<string, object?>
        {
            ["audioFormat"] = 0x800,          // PCM, 44100 Hz, 16-bit, stereo
            ["audioMode"] = "default",
            ["controlPort"] = controlPort,
            ["ct"] = 1,                       // compression type 1 = LPCM
            ["isMedia"] = true,
            ["latencyMax"] = latencyMaxFrames,
            ["latencyMin"] = latencyMinFrames,
            ["shk"] = streamKey,
            ["spf"] = AudioFormat.FramesPerPacket,
            ["sr"] = AudioFormat.SampleRate,
            ["type"] = 96,
            ["supportsDynamicStreamID"] = false,
            ["streamConnectionID"] = (long)_sessionId,
        };
        var body = new Dictionary<string, object?> { ["streams"] = new List<object?> { stream } };
        var r = await _rtsp!.RequestPlistAsync("SETUP", Uri, body, ct: ct);
        if (r.Status != 200) throw new AirPlayException($"SETUP (stream) failed: {r.StartLine}");
        if (r.PlistDict.TryGetValue("streams", out var s) && s is List<object?> { Count: > 0 } list && list[0] is Dictionary<string, object?> sd)
        {
            int data = Convert.ToInt32(sd["dataPort"]);
            int control = Convert.ToInt32(sd["controlPort"]);
            int a2r = sd.TryGetValue("arrivalToRenderLatencyMs", out var lat) ? Convert.ToInt32(lat) : 0;
            return new StreamEndpoints(data, control, a2r);
        }
        throw new AirPlayException("SETUP (stream) response had no stream description");
    }

    public async Task RecordAsync(ushort seq, uint rtpTime, CancellationToken ct)
    {
        var hdr = new Dictionary<string, string>
        {
            ["Range"] = "npt=0-",
            ["Session"] = "1",
            ["RTP-Info"] = $"seq={seq};rtptime={rtpTime}",
        };
        var r = await _rtsp!.RequestAsync("RECORD", Uri, headers: hdr, ct: ct);
        if (r.Status != 200) throw new AirPlayException($"RECORD failed: {r.StartLine}");
        StartFeedback();
    }

    /// <summary>AirPlay volume in dB: -30 (quietest) … 0 (loudest); -144 mutes.</summary>
    public async Task SetVolumeAsync(double db, CancellationToken ct = default)
    {
        db = db <= -144 ? -144 : Math.Clamp(db, -30, 0);
        var body = Encoding.ASCII.GetBytes($"volume: {db.ToString("F6", CultureInfo.InvariantCulture)}\r\n");
        var r = await _rtsp!.RequestAsync("SET_PARAMETER", Uri, body, "text/parameters", ct: ct);
        if (r.Status != 200) Log.Warn($"volume change rejected: {r.StartLine}");
        else ReceiverVolume = db;
    }

    public async Task SetProgressAsync(uint start, uint now, uint end, CancellationToken ct = default)
    {
        var body = Encoding.ASCII.GetBytes($"progress: {start}/{now}/{end}\r\n");
        await _rtsp!.RequestAsync("SET_PARAMETER", Uri, body, "text/parameters", ct: ct);
    }

    void StartFeedback()
    {
        _feedbackCts = new CancellationTokenSource();
        var token = _feedbackCts.Token;
        _ = Task.Run(async () =>
        {
            int failures = 0;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), token);
                    var r = await _rtsp!.RequestAsync("POST", "/feedback", ct: token);
                    failures = r.Status == 200 ? 0 : failures + 1;
                }
                catch (OperationCanceledException) { return; }
                catch (Exception e)
                {
                    failures++;
                    Log.Debug($"feedback failed: {e.Message}");
                    if (e is IOException || e is ObjectDisposedException || failures >= 3)
                    {
                        RaiseLost($"receiver stopped responding ({e.Message})");
                        return;
                    }
                }
            }
        });
    }

    void OnReceiverEvent(string type, Dictionary<string, object?> body)
    {
        if (type == "updateInfo") Log.Debug("receiver pushed updateInfo: " + Fairplay.Plist.BinaryPlist.Dump(body.GetValueOrDefault("value")).Replace('\n', ' '));
        else Log.Info($"receiver event: {type}");
        ReceiverEvent?.Invoke(type, body);
    }

    void RaiseLost(string reason)
    {
        if (Interlocked.Exchange(ref _lostRaised, 1) == 0)
        {
            Log.Warn($"session lost: {reason}");
            Lost?.Invoke(reason);
        }
    }

    public async Task TeardownAsync()
    {
        _feedbackCts?.Cancel();
        if (_rtsp is { IsConnected: true })
        {
            Interlocked.Exchange(ref _lostRaised, 1); // a deliberate stop is not a loss
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _rtsp.RequestAsync("TEARDOWN", Uri, ct: cts.Token);
            }
            catch (Exception e) { Log.Debug($"teardown: {e.Message}"); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await TeardownAsync();
        _events?.Dispose();
        _rtsp?.Dispose();
    }
}
