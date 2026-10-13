using Fairplay.Util;

namespace Fairplay.App;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Contains("--exit"))
        {
            // Ask a running instance to tear down its stream and quit.
            if (EventWaitHandle.TryOpenExisting(TrayApp.ExitEventName, out var ev)) using (ev) ev.Set();
            return;
        }

        using var single = new Mutex(true, @"Local\Fairplay.Tray", out bool first);
        if (!first) return; // already running in the tray

        Log.MinLevel = args.Contains("--verbose") ? LogLevel.Debug : LogLevel.Info;
        LogBuffer.Attach();

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Error($"UI error: {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error($"fatal: {e.ExceptionObject}");
        Application.Run(new TrayApp(startHidden: args.Contains("--autostart"), showMenuForScreenshot: args.Contains("--show-menu")));
    }
}

/// <summary>Keeps recent log lines in memory (for the log window) and mirrors them to a file.</summary>
static class LogBuffer
{
    const int Max = 2000;
    static readonly LinkedList<string> Lines = new();
    static StreamWriter? _file;
    public static event Action<string>? Line;

    public static void Attach()
    {
        try
        {
            Directory.CreateDirectory(Control.Settings.Folder);
            var path = Path.Combine(Control.Settings.Folder, "fairplay.log");
            if (File.Exists(path) && new FileInfo(path).Length > 2_000_000) File.Delete(path);
            _file = new StreamWriter(path, append: true) { AutoFlush = true };
            _file.WriteLine($"---- {DateTime.Now:yyyy-MM-dd HH:mm:ss} Fairplay starting");
        }
        catch { }
        Log.Message += (_, line) =>
        {
            lock (Lines)
            {
                Lines.AddLast(line);
                if (Lines.Count > Max) Lines.RemoveFirst();
                try { _file?.WriteLine($"{DateTime.Now:HH:mm:ss} {line}"); } catch { }
            }
            Line?.Invoke(line);
        };
    }

    public static string[] Snapshot()
    {
        lock (Lines) return Lines.ToArray();
    }
}
