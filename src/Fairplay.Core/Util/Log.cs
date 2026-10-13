using System.Diagnostics;

namespace Fairplay.Util;

public enum LogLevel { Debug, Info, Warn, Error }

/// <summary>Tiny process-wide logger. Sinks subscribe to <see cref="Message"/>.</summary>
public static class Log
{
    public static LogLevel MinLevel { get; set; } = LogLevel.Info;

    public static event Action<LogLevel, string>? Message;

    static readonly Stopwatch Uptime = Stopwatch.StartNew();

    public static void Debug(string msg) => Write(LogLevel.Debug, msg);
    public static void Info(string msg) => Write(LogLevel.Info, msg);
    public static void Warn(string msg) => Write(LogLevel.Warn, msg);
    public static void Error(string msg) => Write(LogLevel.Error, msg);

    public static bool IsEnabled(LogLevel level) => level >= MinLevel;

    static void Write(LogLevel level, string msg)
    {
        if (level < MinLevel) return;
        Message?.Invoke(level, $"{Uptime.Elapsed.TotalSeconds,8:F3} {Tag(level)} {msg}");
    }

    static string Tag(LogLevel l) => l switch
    {
        LogLevel.Debug => "DBG",
        LogLevel.Info => "INF",
        LogLevel.Warn => "WRN",
        _ => "ERR",
    };

    public static string Hex(ReadOnlySpan<byte> data, int max = 64)
    {
        var s = Convert.ToHexString(data.Length > max ? data[..max] : data).ToLowerInvariant();
        return data.Length > max ? s + $"...(+{data.Length - max})" : s;
    }
}
