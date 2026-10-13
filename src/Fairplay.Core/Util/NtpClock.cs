using System.Diagnostics;

namespace Fairplay.Util;

/// <summary>
/// Monotonic sender clock expressed in NTP format (seconds since 1900, 32.32 fixed point).
/// Anchored to wall-clock time once at startup and advanced by the QPC so it never jumps.
/// Everything the receiver learns about our timeline (timing replies, sync packets)
/// must come from this single clock.
/// </summary>
public static class NtpClock
{
    const ulong NtpEpochOffset = 2208988800UL; // seconds between 1900-01-01 and 1970-01-01

    static readonly long QpcAnchor;
    static readonly ulong NtpAnchor;
    static readonly double TicksToNtp;

    static NtpClock()
    {
        QpcAnchor = Stopwatch.GetTimestamp();
        var unixTicks = DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks; // 100ns units
        ulong secs = (ulong)(unixTicks / TimeSpan.TicksPerSecond) + NtpEpochOffset;
        ulong frac = (ulong)((unixTicks % TimeSpan.TicksPerSecond) * (double)(1UL << 32) / TimeSpan.TicksPerSecond);
        NtpAnchor = (secs << 32) | frac;
        TicksToNtp = 4294967296.0 / Stopwatch.Frequency;
    }

    /// <summary>Current time as 64-bit NTP timestamp.</summary>
    public static ulong Now() => FromQpc(Stopwatch.GetTimestamp());

    public static ulong FromQpc(long qpc) => NtpAnchor + (ulong)((qpc - QpcAnchor) * TicksToNtp);

    /// <summary>Seconds on the monotonic timeline (QPC based) — for pacing math.</summary>
    public static double Seconds(long qpc) => (double)(qpc - QpcAnchor) / Stopwatch.Frequency;

    public static double NowSeconds() => Seconds(Stopwatch.GetTimestamp());

    public static long QpcFromSeconds(double seconds) => QpcAnchor + (long)(seconds * Stopwatch.Frequency);
}
