using System.ComponentModel;
using System.Diagnostics;

namespace Fairplay.Util;

/// <summary>
/// AirPlay receivers open connections *to* the sender (NTP timing requests over UDP, the event
/// channel is fine since we dial it). Windows Firewall drops those unless the executable has an
/// inbound allow rule — and if someone dismissed the first-run prompt, Windows even adds an
/// explicit block rule. This inspects and repairs that.
/// </summary>
public static class Firewall
{
    public enum State { Allowed, Missing, Blocked, Unknown }

    const int Inbound = 1, ActionAllow = 1, ActionBlock = 0, Tcp = 6, Udp = 17, AnyProtocol = 256;

    public static State Check(string exePath)
    {
        try
        {
            var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (type == null) return State.Unknown;
            dynamic policy = Activator.CreateInstance(type)!;
            int profiles = policy.CurrentProfileTypes;
            try
            {
                bool anyEnabled = false;
                foreach (int p in new[] { 1, 2, 4 })
                    if ((profiles & p) != 0 && (bool)policy.FirewallEnabled[p]) anyEnabled = true;
                if (!anyEnabled) return State.Allowed;
            }
            catch { /* indexed property unsupported: fall through to the rule scan */ }
            bool allow = false, block = false;
            foreach (dynamic rule in policy.Rules)
            {
                string? app = rule.ApplicationName;
                if (app == null || !string.Equals(Path.GetFullPath(app), Path.GetFullPath(exePath), StringComparison.OrdinalIgnoreCase)) continue;
                if (!(bool)rule.Enabled || (int)rule.Direction != Inbound || ((int)rule.Profiles & profiles) == 0) continue;
                int proto = rule.Protocol;
                if (proto != Udp && proto != AnyProtocol) continue;
                if ((int)rule.Action == ActionBlock) block = true;
                else if ((int)rule.Action == ActionAllow) allow = true;
            }
            return block ? State.Blocked : allow ? State.Allowed : State.Missing;
        }
        catch (Exception e)
        {
            Log.Debug($"firewall check failed: {e.Message}");
            return State.Unknown;
        }
    }

    /// <summary>
    /// Replace any rules for these executables with "allow inbound from the local subnet".
    /// Shows a UAC prompt; returns false if the user declined.
    /// </summary>
    public static bool Repair(params string[] exePaths)
    {
        var cmds = new List<string>();
        foreach (var p in exePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cmds.Add($"netsh advfirewall firewall delete rule name=all dir=in program=\"{p}\"");
            foreach (var proto in new[] { "UDP", "TCP" })
                cmds.Add($"netsh advfirewall firewall add rule name=\"Fairplay (AirPlay {proto})\" dir=in action=allow " +
                         $"program=\"{p}\" protocol={proto} profile=any remoteip=localsubnet enable=yes");
        }
        var psi = new ProcessStartInfo("cmd.exe", "/c " + string.Join(" & ", cmds))
        {
            Verb = "runas",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        try
        {
            using var p = Process.Start(psi);
            p?.WaitForExit(30000);
            Log.Info($"firewall rules updated (exit {p?.ExitCode})");
            return p?.ExitCode == 0;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            Log.Warn("firewall change declined at the UAC prompt");
            return false;
        }
    }
}
