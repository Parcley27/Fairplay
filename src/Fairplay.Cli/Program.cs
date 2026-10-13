using System.Globalization;
using System.Net;
using Fairplay.AirPlay;
using Fairplay.Audio;
using Fairplay.Discovery;
using Fairplay.Util;

namespace Fairplay.Cli;

static class Program
{
    static async Task<int> Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        var opts = Options.Parse(args);
        Log.MinLevel = opts.Has("verbose") ? LogLevel.Debug : LogLevel.Info;
        Log.Message += (_, line) => Console.Error.WriteLine(line);

        try
        {
            return opts.Command switch
            {
                "discover" or "list" => await Discover(),
                "devices" => Devices(),
                "info" => await Info(opts),
                "tone" => await Tone(opts),
                "stream" => await Stream(opts),
                "selftest" => SelfTest.Run(),
                "firewall" => FirewallStatus(opts),
                "run" => await Run(opts),
                "setvol" => SetWindowsVolume(opts),
                "rename" => RenameDevice(opts),
                _ => Usage(),
            };
        }
        catch (ReceiverCannotReachUsException e)
        {
            Console.Error.WriteLine($"error: {e.Message}");
            Console.Error.WriteLine("(run 'fairplay firewall --fix' to add an inbound rule; needs admin approval)");
            return 4;
        }
        catch (AirPlayException e)
        {
            Console.Error.WriteLine($"error: {e.Message}");
            return 2;
        }
    }

    static int Usage()
    {
        Console.WriteLine("""
            fairplay — stream Windows audio to AirPlay 2 speakers (HomePod, HomePod stereo pairs)

              fairplay discover                         list speakers (stereo pairs are grouped)
              fairplay devices                          list Windows playback devices you can capture
              fairplay info <speaker>                   dump the receiver's /info
              fairplay tone <speaker> [options]         play a short test chime
              fairplay stream <speaker> [options]       stream system audio until Ctrl+C
              fairplay run <speaker> [--sync-volume]    like the tray app: auto-reconnect, volume sync
              fairplay firewall [--fix]                 check / repair the Windows Firewall rule
              fairplay rename <device> <name>           rename a Windows playback device (e.g. a virtual cable)
              fairplay setvol <0..1> [--mute|--unmute]  set the default output's Windows volume
              fairplay selftest                         offline checks of crypto, plist, resampler

            options:
              --latency <ms>       playback latency (default 500)
              --volume <dB>        set speaker volume, -30..0 (default: leave as is)
              --device <id|name>   capture this playback device instead of the default output
              --seconds <n>        stop after n seconds
              --verbose            protocol-level logging

            <speaker> is a speaker or stereo-pair name: "Bedroom" plays on both halves of the pair,
            "Bed" on just that HomePod (mono mix). An IP also works.
            tone also takes --pan left|right to check which speaker is which channel.
            """);
        return 1;
    }

    static async Task<int> Discover()
    {
        var devices = await AirPlayBrowser.DiscoverDevicesAsync(TimeSpan.FromSeconds(3));
        Console.WriteLine($"{devices.Count} AirPlay device(s):");
        foreach (var d in devices)
            Console.WriteLine($"  {d.Name,-20} {d.FriendlyModel,-18} {d.Address,-16} os {d.OsVersion,-6} " +
                              $"{(d.IsGroupLeader ? "group-leader " : "")}{(d.GroupName != null ? $"group='{d.GroupName}' " : "")}" +
                              $"{(d.SupportsTransientPairing ? "transient-pairing" : "NO-transient-pairing")}");
        var targets = AirPlayBrowser.GroupTargets(devices);
        Console.WriteLine();
        Console.WriteLine("Outputs you can stream to:");
        foreach (var t in targets) Console.WriteLine($"  {t.Description}");
        return 0;
    }

    /// <summary>Same engine as the tray app (StreamController): reconnects, takeover handling, volume sync.</summary>
    static async Task<int> Run(Options o)
    {
        var settings = new Fairplay.Control.Settings
        {
            LatencyMs = (int)o.Double("latency", 300),
            SyncVolume = o.Has("sync-volume"),
            AutoConnect = false,
            Persist = false,
        };
        await using var controller = new Fairplay.Control.StreamController(settings);
        var target = await Resolve(o.Positional(0));
        var done = new TaskCompletionSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.TrySetResult(); };
        controller.Changed += () =>
        {
            if (controller.Settings.SyncVolume && controller.WindowsVolume is { } wv)
                Log.Debug($"windows volume {wv.Scalar:P0}{(wv.Muted ? " muted" : "")} → speaker {controller.SpeakerVolume:F1} dB");
        };
        await controller.StartAsync(target);
        double seconds = o.Double("seconds", 0);
        await Task.WhenAny(done.Task, seconds > 0 ? Task.Delay(TimeSpan.FromSeconds(seconds)) : Task.Delay(Timeout.Infinite));
        Log.Info($"final: state {controller.State}, speaker volume {controller.SpeakerVolume:F2} dB");
        await controller.StopAsync();
        return 0;
    }

    static int RenameDevice(Options o)
    {
        var q = o.Positional(0) ?? throw new AirPlayException("usage: rename <device> <new name>");
        var dev = LoopbackSource.ListRenderDevices().FirstOrDefault(d => d.Id == q || d.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                  ?? throw new AirPlayException($"no playback device matching '{q}'");
        var newName = o.Positional(1) ?? throw new AirPlayException("usage: rename <device> <new name>");
        Console.WriteLine($"'{WindowsAudio.GetEndpointName(dev.Id)}' → '{newName}'");
        WindowsAudio.RenameEndpoint(dev.Id, newName);
        Console.WriteLine($"now: {LoopbackSource.ListRenderDevices().First(d => d.Id == dev.Id).Name}");
        return 0;
    }

    static int SetWindowsVolume(Options o)
    {
        var v = float.Parse(o.Positional(0) ?? "0.5", CultureInfo.InvariantCulture);
        WindowsAudio.SetEndpointVolume(null, v, o.Has("mute") ? true : o.Has("unmute") ? false : null);
        Console.WriteLine($"default output volume set to {v:P0}");
        return 0;
    }

    static int FirewallStatus(Options o)
    {
        var paths = new List<string> { Environment.ProcessPath! };
        if (o.Positional(0) is { } extra) paths.Add(Path.GetFullPath(extra));
        foreach (var p in paths) Console.WriteLine($"{Firewall.Check(p),-8} {p}");
        if (o.Has("fix")) Console.WriteLine(Firewall.Repair(paths.ToArray()) ? "rules updated" : "not changed");
        return 0;
    }

    static int Devices()
    {
        using var en = new NAudio.CoreAudioApi.MMDeviceEnumerator();
        foreach (var d in LoopbackSource.ListRenderDevices())
        {
            using var dev = en.GetDevice(d.Id);
            var v = dev.AudioEndpointVolume;
            Console.WriteLine($"{(d.IsDefault ? "*" : " ")} {d.Name}\n    {d.Id}\n" +
                              $"    volume {v.MasterVolumeLevelScalar:P0} ({v.MasterVolumeLevel:F1} dB){(v.Mute ? " MUTED" : "")}, " +
                              $"volume applied in {(((int)v.HardwareSupport & 1) != 0 ? "hardware" : "software")} ({v.HardwareSupport})");
        }
        return 0;
    }

    static async Task<AirPlayTarget> Resolve(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) throw new AirPlayException("no speaker given");
        var devices = await AirPlayBrowser.DiscoverDevicesAsync(TimeSpan.FromSeconds(2.5));
        var targets = AirPlayBrowser.GroupTargets(devices);
        // Exact name ("Bedroom" = the pair, "Bed" = just that speaker), then IP, then prefix.
        var t = targets.FirstOrDefault(t => t.DisplayName.Equals(query, StringComparison.OrdinalIgnoreCase))
                ?? targets.FirstOrDefault(t => t.Receivers.Count == 1 && t.Primary.Address.ToString() == query)
                ?? targets.FirstOrDefault(t => t.DisplayName.StartsWith(query, StringComparison.OrdinalIgnoreCase));
        if (t != null) return t;
        throw new AirPlayException($"no AirPlay speaker matching '{query}' (found: {string.Join(", ", targets.Select(x => x.DisplayName))})");
    }

    static async Task<int> Info(Options o)
    {
        var t = await Resolve(o.Positional(0));
        using var c = await RtspConnection.ConnectAsync(t.Primary.Address, t.Primary.Port, CancellationToken.None);
        var r = await c.RequestAsync("GET", "/info");
        Console.WriteLine(Fairplay.Plist.BinaryPlist.Dump(r.Plist));
        return 0;
    }

    static StreamOptions StreamOptionsFrom(Options o) => new()
    {
        LatencySeconds = o.Double("latency", 500) / 1000.0,
        InitialVolumeDb = o.Has("volume") ? o.Double("volume", -20) : null,
        CorruptAudioForTesting = o.Has("corrupt"),
        DumpPath = o.Get("dump"),
    };

    static async Task<int> Tone(Options o)
    {
        var target = await Resolve(o.Positional(0));
        var pan = o.Get("pan")?.ToLowerInvariant() switch { "left" or "l" => -1, "right" or "r" => 1, _ => 0 };
        var tone = new ToneSource(toneSeconds: o.Double("tone-seconds", 0.4), dbfs: o.Double("level", -20), pan: pan);
        await using var streamer = new AirPlayStreamer(target, tone, StreamOptionsFrom(o));
        var done = new TaskCompletionSource<string>();
        streamer.Stopped += r => done.TrySetResult(r);
        await streamer.StartAsync();
        var wait = TimeSpan.FromSeconds(tone.DurationSeconds + streamer.Stats.LatencySeconds + 0.5);
        var finished = await Task.WhenAny(done.Task, Task.Delay(wait));
        PrintStats(streamer);
        if (finished == done.Task)
        {
            Console.Error.WriteLine($"stream ended early: {done.Task.Result}");
            return 3;
        }
        await streamer.StopAsync();
        return 0;
    }

    static async Task<int> Stream(Options o)
    {
        var target = await Resolve(o.Positional(0));
        string? deviceId = null;
        if (o.Get("device") is { } dq)
        {
            var dev = LoopbackSource.ListRenderDevices().FirstOrDefault(d => d.Id == dq || d.Name.Contains(dq, StringComparison.OrdinalIgnoreCase))
                      ?? throw new AirPlayException($"no playback device matching '{dq}'");
            deviceId = dev.Id;
        }
        var source = new LoopbackSource(deviceId);
        await using var streamer = new AirPlayStreamer(target, source, StreamOptionsFrom(o));
        var done = new TaskCompletionSource<string>();
        streamer.Stopped += r => done.TrySetResult(r);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.TrySetResult("stopped by user"); };
        await streamer.StartAsync();

        double seconds = o.Double("seconds", 0);
        var limit = seconds > 0 ? Task.Delay(TimeSpan.FromSeconds(seconds)) : Task.Delay(Timeout.Infinite);
        while (!done.Task.IsCompleted && !limit.IsCompleted)
        {
            await Task.WhenAny(done.Task, limit, Task.Delay(5000));
            if (!done.Task.IsCompleted)
                Log.Info($"fifo {source.FifoMilliseconds,5:F1} ms  trim {source.Trim * 1e6,7:F1} ppm  peak {source.Peak,5:F3}  " +
                         $"underruns {source.Underruns}  overflows {source.Overflows}  glitches {source.Discontinuities}  sent {streamer.Stats.PacketsSent}  rtx {streamer.Stats.RetransmitRequests}  timing {streamer.Stats.TimingRequests}");
        }
        PrintStats(streamer);
        if (done.Task.IsCompleted) Console.Error.WriteLine(done.Task.Result);
        await streamer.StopAsync();
        return 0;
    }

    static void PrintStats(AirPlayStreamer s)
    {
        var st = s.Stats;
        Console.Error.WriteLine($"stats: {st.Elapsed.TotalSeconds:F1}s, {st.PacketsSent} packets, {st.SilentPackets} padding, " +
                                $"{st.RetransmitRequests} retransmit requests ({st.PacketsResent} resent), {st.TimingRequests} timing requests");
        foreach (var line in s.ReceiverStats()) Console.Error.WriteLine($"  {line}");
    }
}

sealed class Options
{
    readonly List<string> _positional = [];
    readonly Dictionary<string, string?> _named = new(StringComparer.OrdinalIgnoreCase);
    public string Command { get; private set; } = "";

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a.StartsWith("--"))
            {
                var key = a[2..];
                string? val = i + 1 < args.Length && !args[i + 1].StartsWith("--") && key is not ("verbose" or "corrupt" or "fix" or "sync-volume" or "mute" or "unmute") ? args[++i] : null;
                o._named[key] = val;
            }
            else if (o.Command == "") o.Command = a.ToLowerInvariant();
            else o._positional.Add(a);
        }
        return o;
    }

    public string? Positional(int i) => i < _positional.Count ? _positional[i] : null;
    public bool Has(string k) => _named.ContainsKey(k);
    public string? Get(string k) => _named.TryGetValue(k, out var v) ? v : null;
    public double Double(string k, double def) => double.TryParse(Get(k), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : def;
}
