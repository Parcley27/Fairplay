using System.Reflection;
using Fairplay.Audio;
using Fairplay.Control;
using Fairplay.Discovery;
using Fairplay.Util;
using Microsoft.Win32;

namespace Fairplay.App;

/// <summary>
/// The tray front end. Everything lives in one context menu (left or right click):
/// pick a speaker to start, click it again to stop; volume slider; source/latency/options.
/// </summary>
sealed class TrayApp : ApplicationContext
{
    /// <summary>Latency slider stops: fine-grained where it matters, coarser for the robust end.</summary>
    static readonly int[] LatencySteps =
        [50, 60, 70, 80, 90, 100, 110, 120, 130, 140, 150, 175, 200, 250, 300, 400, 500, 750, 1000];

    readonly NotifyIcon _tray;
    readonly ContextMenuStrip _menu;
    readonly StreamController _controller;
    readonly SynchronizationContext _ui;
    readonly TrackBar _volume;
    readonly ToolStripControlHost _volumeHost;
    readonly ToolStripLabel _volumeLabel;
    LogForm? _logForm;
    bool _updatingSlider;
    readonly System.Windows.Forms.Timer _volumeDebounce = new() { Interval = 120 };
    readonly TrackBar _latency;
    readonly ToolStripControlHost _latencyHost;
    readonly ToolStripLabel _latencyLabel;
    // Changing latency restarts the stream, so wait until the slider has settled.
    readonly System.Windows.Forms.Timer _latencyDebounce = new() { Interval = 700 };

    public const string ExitEventName = @"Local\Fairplay.Exit";
    readonly EventWaitHandle _exitSignal = new(false, EventResetMode.AutoReset, ExitEventName);

    public TrayApp(bool startHidden, bool showMenuForScreenshot = false)
    {
        var exitWatcher = new Thread(() =>
        {
            _exitSignal.WaitOne();
            _ui!.Post(async _ => await ExitAsync(), null);
        }) { IsBackground = true, Name = "exit signal" };
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _controller = new StreamController(Settings.Load());
        _controller.Changed += () => _ui.Post(_ => UpdateTray(), null);
        _controller.Notify += (title, msg, err) => _ui.Post(_ => Balloon(title, msg, err), null);

        _volume = new TrackBar
        {
            Minimum = 0,
            Maximum = 100,
            TickStyle = TickStyle.None,
            AutoSize = false,
            Height = 28,
            Width = 220,
            SmallChange = 2,
            LargeChange = 10,
        };
        _volume.ValueChanged += (_, _) =>
        {
            if (_updatingSlider) return;
            _volumeLabel!.Text = VolumeText(_volume.Value / 100.0);
            _volumeDebounce.Stop();
            _volumeDebounce.Start();
        };
        _volumeDebounce.Tick += (_, _) =>
        {
            _volumeDebounce.Stop();
            _ = ApplySliderAsync(_volume.Value / 100.0);
        };
        _volumeHost = new ToolStripControlHost(_volume) { AutoSize = false, Width = 230, Height = 30, Margin = new Padding(8, 0, 0, 0) };
        _volumeLabel = new ToolStripLabel("Volume") { ForeColor = SystemColors.GrayText };

        _latency = new TrackBar
        {
            Minimum = 0,
            Maximum = LatencySteps.Length - 1,
            TickStyle = TickStyle.None,
            AutoSize = false,
            Height = 28,
            Width = 220,
            SmallChange = 1,
            LargeChange = 3,
            BackColor = ProfessionalColors.ToolStripDropDownBackground,
        };
        _latency.ValueChanged += (_, _) =>
        {
            if (_updatingSlider) return;
            _latencyLabel!.Text = LatencyText(LatencySteps[_latency.Value]);
            _latencyDebounce.Stop();
            _latencyDebounce.Start();
        };
        _latencyDebounce.Tick += async (_, _) =>
        {
            _latencyDebounce.Stop();
            int ms = LatencySteps[_latency.Value];
            if (ms == _controller.Settings.LatencyMs) return;
            _controller.Settings.LatencyMs = ms;
            await _controller.ApplySettingsAsync();
        };
        _latencyHost = new ToolStripControlHost(_latency)
        {
            AutoSize = false, Width = 230, Height = 30, Margin = new Padding(8, 0, 0, 0),
            ToolTipText = "Lower = less delay (better for video). Raise it if you hear crackles or dropouts.",
        };
        _latencyLabel = new ToolStripLabel("Latency") { ForeColor = SystemColors.GrayText };

        _menu = new ContextMenuStrip { ShowCheckMargin = true, ShowImageMargin = false };
        _volume.BackColor = ProfessionalColors.ToolStripDropDownBackground;
        _menu.Opening += (_, e) =>
        {
            BuildMenu();
            e.Cancel = false;
        };

        _tray = new NotifyIcon
        {
            Icon = TrayIcons.Get(TrayIcons.Kind.Idle),
            Text = "Fairplay",
            Visible = true,
            ContextMenuStrip = _menu,
        };
        _tray.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                typeof(NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(_tray, null);
        };

        exitWatcher.Start();
        // Start once the message loop runs, so continuations (balloons, dialogs) stay on the UI thread.
        _ui.Post(_ => _ = AutoConnectAsync(startHidden), null);

        if (showMenuForScreenshot)
        {
            // Debug aid: pop the menu open near the tray so it can be captured without clicking.
            var timer = new System.Windows.Forms.Timer { Interval = 13000 };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                var area = Screen.PrimaryScreen!.WorkingArea;
                _menu.Show(new Point(area.Right - 700, area.Bottom - 10), ToolStripDropDownDirection.AboveRight);
            };
            timer.Start();
        }
    }

    async Task AutoConnectAsync(bool fromAutostart)
    {
        var s = _controller.Settings;
        if (!s.AutoConnect || s.LastTargetId == null)
        {
            if (!fromAutostart)
                Balloon("Fairplay is running", "Click the AirPlay icon in the tray to choose a speaker.", false);
            return;
        }
        // Give discovery a few rounds to find the speaker.
        for (int i = 0; i < 6; i++)
        {
            if (_controller.FindTarget(s.LastTargetId) is { } t)
            {
                await StartAsync(t);
                return;
            }
            await Task.Delay(TimeSpan.FromSeconds(i == 0 ? 3 : 5));
            if (i > 0) await _controller.RefreshTargetsAsync();
        }
        Balloon("Speaker not found", $"Couldn't find {s.LastTargetName ?? "your last speaker"} on the network.", true);
    }

    // ------------------------------------------------------------------ tray state

    void UpdateTray()
    {
        var c = _controller;
        _tray.Icon = TrayIcons.Get(c.State switch
        {
            StreamState.Streaming => TrayIcons.Kind.Streaming,
            StreamState.Connecting or StreamState.Reconnecting => TrayIcons.Kind.Busy,
            _ => TrayIcons.Kind.Idle,
        });
        var text = $"Fairplay — {c.Status}";
        _tray.Text = text.Length > 63 ? text[..63] : text;
        if (_menu.Visible) SyncSlider();
    }

    void Balloon(string title, string message, bool error)
    {
        _tray.BalloonTipTitle = title;
        _tray.BalloonTipText = message;
        _tray.BalloonTipIcon = error ? ToolTipIcon.Warning : ToolTipIcon.Info;
        _tray.ShowBalloonTip(4000);
    }

    // ------------------------------------------------------------------ menu

    void BuildMenu()
    {
        var c = _controller;
        var s = c.Settings;
        _menu.SuspendLayout();
        _menu.Items.Clear();

        _menu.Items.Add(new ToolStripLabel(c.Status) { Font = new Font(_menu.Font, FontStyle.Bold) });
        _menu.Items.Add(new ToolStripSeparator());

        _menu.Items.Add(new ToolStripLabel("Speakers") { ForeColor = SystemColors.GrayText });
        if (c.Targets.Count == 0)
            _menu.Items.Add(new ToolStripMenuItem(c.IsDiscovering ? "Searching…" : "No AirPlay speakers found") { Enabled = false });
        foreach (var t in c.Targets)
        {
            bool active = c.ActiveTarget?.Id == t.Id && c.State != StreamState.Idle;
            var label = t.Kind switch
            {
                TargetKind.StereoPair => $"{t.DisplayName}   (stereo pair)",
                TargetKind.PairMember => $"      {t.DisplayName} only",
                _ => $"{t.DisplayName}   ({t.Primary.FriendlyModel})",
            };
            var item = new ToolStripMenuItem(label) { Checked = active, ToolTipText = t.Description };
            if (t.Kind == TargetKind.PairMember)
                item.ToolTipText = $"Play on just this HomePod of {t.PairName} (sent as a mono mix so you hear both channels)";
            if (t.Receivers.Any(r => !r.SupportsTransientPairing && !r.IsHomePod))
                item.ToolTipText += " — may require pairing and might not work";
            var target = t;
            item.Click += async (_, _) =>
            {
                if (active) await _controller.StopAsync();
                else await StartAsync(target);
            };
            _menu.Items.Add(item);
        }
        var refresh = new ToolStripMenuItem(c.IsDiscovering ? "Searching…" : "Search again") { Enabled = !c.IsDiscovering };
        refresh.Click += async (_, _) => await _controller.RefreshTargetsAsync();
        _menu.Items.Add(refresh);

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_volumeLabel);
        _menu.Items.Add(_volumeHost);
        SyncSlider();
        _menu.Items.Add(_latencyLabel);
        _menu.Items.Add(_latencyHost);
        SyncLatencySlider();

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(BuildSourceMenu());

        var sync = new ToolStripMenuItem("Windows volume controls the speaker") { Checked = s.SyncVolume, CheckOnClick = true,
            ToolTipText = "Volume keys and the Windows slider set the speaker's own volume (the audio is sent at full level)." };
        sync.CheckedChanged += async (_, _) => { s.SyncVolume = sync.Checked; await _controller.ApplySettingsAsync(); };
        _menu.Items.Add(sync);

        var auto = new ToolStripMenuItem("Reconnect to last speaker on start") { Checked = s.AutoConnect, CheckOnClick = true };
        auto.CheckedChanged += (_, _) => { s.AutoConnect = auto.Checked; s.Save(); };
        _menu.Items.Add(auto);

        var startup = new ToolStripMenuItem("Start with Windows") { Checked = Autostart.IsEnabled, CheckOnClick = true };
        startup.CheckedChanged += (_, _) => Autostart.Set(startup.Checked);
        _menu.Items.Add(startup);

        _menu.Items.Add(new ToolStripSeparator());
        if (c.State != StreamState.Idle)
        {
            var stop = new ToolStripMenuItem("Stop streaming");
            stop.Click += async (_, _) => await _controller.StopAsync();
            _menu.Items.Add(stop);
        }
        var log = new ToolStripMenuItem("Show log…");
        log.Click += (_, _) => ShowLog();
        _menu.Items.Add(log);
        var exit = new ToolStripMenuItem("Exit");
        exit.Click += async (_, _) => await ExitAsync();
        _menu.Items.Add(exit);
        _menu.ResumeLayout();
    }

    ToolStripMenuItem BuildSourceMenu()
    {
        var s = _controller.Settings;
        var root = new ToolStripMenuItem("Audio source");
        List<RenderDeviceInfo> devices;
        try { devices = LoopbackSource.ListRenderDevices(); }
        catch (Exception e) { Log.Warn($"device list failed: {e.Message}"); devices = []; }

        var def = devices.FirstOrDefault(d => d.IsDefault);
        var all = new ToolStripMenuItem($"Everything Windows plays (default output{(def != null ? $": {def.Name}" : "")})")
        {
            Checked = s.SourceDeviceId == null,
        };
        all.Click += async (_, _) => { s.SourceDeviceId = null; await _controller.ApplySettingsAsync(); };
        root.DropDownItems.Add(all);
        root.DropDownItems.Add(new ToolStripSeparator());
        root.DropDownItems.Add(new ToolStripLabel("Only this playback device:") { ForeColor = SystemColors.GrayText });
        foreach (var d in devices)
        {
            var item = new ToolStripMenuItem(d.Name) { Checked = s.SourceDeviceId == d.Id };
            var id = d.Id;
            item.Click += async (_, _) => { s.SourceDeviceId = id; await _controller.ApplySettingsAsync(); };
            root.DropDownItems.Add(item);
        }
        if (s.SourceDeviceId != null && devices.Any(d => d.Id == s.SourceDeviceId && !d.IsDefault))
        {
            root.DropDownItems.Add(new ToolStripSeparator());
            var makeDefault = new ToolStripMenuItem("Send all apps to this device (make it the Windows default)");
            makeDefault.Click += (_, _) =>
            {
                try { WindowsAudio.SetDefaultOutput(s.SourceDeviceId!); }
                catch (Exception e) { Balloon("Couldn't change default device", e.Message, true); }
            };
            root.DropDownItems.Add(makeDefault);
        }
        if (s.SourceDeviceId is { } srcId && devices.Any(d => d.Id == srcId))
        {
            var speaker = _controller.ActiveTarget?.DisplayName ?? s.LastTargetName;
            if (s.RenamedDevices.TryGetValue(srcId, out var original))
            {
                var restore = new ToolStripMenuItem($"Restore the device's original name (“{original}”)");
                restore.Click += (_, _) => RenameSource(srcId, original, forget: true);
                root.DropDownItems.Add(restore);
            }
            else if (speaker != null)
            {
                var label = $"{speaker} (AirPlay)";
                var rename = new ToolStripMenuItem($"Show this device in Windows as “{label}”");
                rename.Click += (_, _) => RenameSource(srcId, label, forget: false);
                root.DropDownItems.Add(rename);
            }
        }
        var help = new ToolStripMenuItem("How do I send only some apps?");
        help.Click += (_, _) => MessageBox.Show(
            "Pick a playback device that has no speakers attached (or a virtual cable such as VB-CABLE) as the source above.\n\n" +
            "Then open Windows Settings → System → Sound → Volume mixer and set the Output device of each app you want on the " +
            "speaker to that device. Everything else keeps playing locally.",
            "Fairplay", MessageBoxButtons.OK, MessageBoxIcon.Information);
        root.DropDownItems.Add(new ToolStripSeparator());
        root.DropDownItems.Add(help);
        return root;
    }

    void RenameSource(string deviceId, string name, bool forget)
    {
        var s = _controller.Settings;
        try
        {
            if (!forget && WindowsAudio.GetEndpointName(deviceId) is { } original)
                s.RenamedDevices[deviceId] = original;
            WindowsAudio.RenameEndpoint(deviceId, name);
            if (forget) s.RenamedDevices.Remove(deviceId);
            s.Save();
        }
        catch (Exception e)
        {
            Balloon("Couldn't rename the device", e.Message, true);
        }
    }

    async Task StartAsync(AirPlayTarget target)
    {
        bool retry = false;
        try { await _controller.StartAsync(target); }
        catch (Fairplay.AirPlay.ReceiverCannotReachUsException e)
        {
            retry = await FixFirewallAsync();
            if (!retry) Balloon($"Couldn't connect to {target.DisplayName}", e.Message, true);
        }
        catch (Exception e) { Balloon($"Couldn't connect to {target.DisplayName}", e.Message, true); }
        if (!retry) return;
        try { await _controller.StartAsync(target); }
        catch (Exception e) { Balloon($"Couldn't connect to {target.DisplayName}", e.Message, true); }
    }

    /// <summary>
    /// The speaker must be able to send UDP to us (clock sync). If Windows Firewall is in the way,
    /// explain and offer to add a local-network-only rule. Returns true if a retry makes sense.
    /// </summary>
    async Task<bool> FixFirewallAsync()
    {
        var exe = Environment.ProcessPath!;
        // Windows' own "allow access" prompt may have just been answered while we were waiting.
        if (Firewall.Check(exe) == Firewall.State.Allowed) return true;
        var answer = MessageBox.Show(
            "Your speaker couldn't reach this PC to sync its clock, so Windows Firewall is probably blocking Fairplay.\n\n" +
            "Allow Fairplay to receive traffic from devices on your local network? Windows will ask for permission.",
            "Fairplay — firewall", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (answer != DialogResult.Yes) return false;
        var exes = new List<string> { exe };
        var cli = Path.Combine(Path.GetDirectoryName(exe)!, "fairplay-cli.exe");
        if (File.Exists(cli)) exes.Add(cli);
        return await Task.Run(() => Firewall.Repair(exes.ToArray()));
    }

    // ------------------------------------------------------------------ volume slider

    void SyncSlider()
    {
        var c = _controller;
        double fraction = c.Settings.SyncVolume && c.WindowsVolume is { } wv
            ? (wv.Muted ? 0 : wv.Scalar)
            : StreamController.VolumeToFraction(c.SpeakerVolume);
        _updatingSlider = true;
        _volume.Value = (int)Math.Round(fraction * 100);
        _updatingSlider = false;
        _volume.Enabled = c.State == StreamState.Streaming;
        _volumeLabel.Text = c.State == StreamState.Streaming
            ? VolumeText(fraction)
            : "Volume (connect to a speaker first)";
    }

    void SyncLatencySlider()
    {
        if (_latencyDebounce.Enabled) return; // a drag is still pending; don't snap it back
        int ms = _controller.Settings.LatencyMs;
        int nearest = 0;
        for (int i = 1; i < LatencySteps.Length; i++)
            if (Math.Abs(LatencySteps[i] - ms) < Math.Abs(LatencySteps[nearest] - ms)) nearest = i;
        _updatingSlider = true;
        _latency.Value = nearest;
        _updatingSlider = false;
        _latencyLabel.Text = LatencyText(ms);
    }

    // HomePods report ~70 ms between packet arrival and playback, so below ~80 ms is a gamble.
    static string LatencyText(int ms) => $"Latency  {ms} ms" + (ms < 80 ? "  (experimental)" : "");

    string VolumeText(double fraction) =>
        (_controller.Settings.SyncVolume ? "Volume (Windows + speaker)  " : "Speaker volume  ") + $"{fraction * 100:F0}%";

    async Task ApplySliderAsync(double fraction)
    {
        if (_controller.Settings.SyncVolume)
        {
            // Move the Windows slider; the sync path forwards it to the speaker.
            try { WindowsAudio.SetEndpointVolume(_controller.Settings.SourceDeviceId, (float)fraction); }
            catch (Exception e) { Log.Warn($"could not set Windows volume: {e.Message}"); }
        }
        else
        {
            await _controller.SetSpeakerVolumeAsync(fraction);
        }
    }

    // ------------------------------------------------------------------ misc

    void ShowLog()
    {
        if (_logForm is { IsDisposed: false })
        {
            _logForm.Activate();
            return;
        }
        _logForm = new LogForm();
        _logForm.Show();
    }

    async Task ExitAsync()
    {
        _tray.Visible = false;
        await _controller.DisposeAsync();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tray.Dispose();
            _menu.Dispose();
            _volumeDebounce.Dispose();
            _latencyDebounce.Dispose();
        }
        base.Dispose(disposing);
    }
}

static class Autostart
{
    const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "Fairplay";

    public static bool IsEnabled
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(Key);
            return k?.GetValue(Name) is string;
        }
    }

    public static void Set(bool enabled)
    {
        using var k = Registry.CurrentUser.CreateSubKey(Key);
        if (enabled) k.SetValue(Name, $"\"{Environment.ProcessPath}\" --autostart");
        else k.DeleteValue(Name, false);
    }
}
