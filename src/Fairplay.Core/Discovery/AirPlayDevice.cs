using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Fairplay.Util;

namespace Fairplay.Discovery;

/// <summary>One AirPlay receiver as advertised over Bonjour (_airplay._tcp).</summary>
public sealed class AirPlayDevice
{
    public required string Name { get; init; }
    public required string HostName { get; init; }
    public required IPAddress Address { get; init; }
    public required int Port { get; init; }
    public required IReadOnlyDictionary<string, string> Txt { get; init; }

    string? T(string key) => Txt.TryGetValue(key, out var v) ? v : null;

    public string DeviceId => T("deviceid") ?? Name;
    public string Model => T("model") ?? "";
    public string? GroupId => T("gid");
    public string? GroupName => T("gpn");
    public bool IsGroupLeader => T("igl") == "1";
    public string? ParentGroupId => T("pgid");
    public string? TightSyncId => T("tsid");
    public string OsVersion => T("osvers") ?? "";
    public string? PublicKey => T("pk");

    public ulong Features
    {
        get
        {
            var f = T("features");
            if (f == null) return 0;
            var parts = f.Split(',');
            ulong lo = ParseHex(parts[0]);
            ulong hi = parts.Length > 1 ? ParseHex(parts[1]) : 0;
            return (hi << 32) | lo;
        }
    }

    public uint StatusFlags => (uint)ParseHex(T("flags") ?? "0");

    // Feature bits (see https://openairplay.github.io/airplay-spec/features.html and Apple's own naming).
    public bool SupportsAirPlayAudio => (Features & (1UL << 9)) != 0;
    public bool SupportsBufferedAudio => (Features & (1UL << 40)) != 0;
    public bool SupportsCoreUtilsPairing => (Features & (1UL << 38)) != 0;
    public bool SupportsSystemPairing => (Features & (1UL << 43)) != 0;
    public bool SupportsHkPairingAndAccessControl => (Features & (1UL << 46)) != 0;
    public bool SupportsTransientPairing => (Features & (1UL << 48)) != 0;
    public bool RequiresPassword => (StatusFlags & 0x80) != 0 || (StatusFlags & 0x8) != 0;

    public bool IsHomePod => Model.StartsWith("AudioAccessory", StringComparison.Ordinal);

    public string FriendlyModel => Model switch
    {
        "AudioAccessory1,1" or "AudioAccessory1,2" => "HomePod",
        "AudioAccessory5,1" => "HomePod mini",
        "AudioAccessory6,1" => "HomePod (2nd gen)",
        var m when m.StartsWith("AppleTV") => "Apple TV",
        var m when m.StartsWith("AirPort") => "AirPort Express",
        var m => m,
    };

    static ulong ParseHex(string s)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return ulong.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    public override string ToString() => $"{Name} ({FriendlyModel}) {Address}:{Port}";
}

public enum TargetKind
{
    /// <summary>A standalone speaker.</summary>
    Single,
    /// <summary>Both halves of a stereo pair; each plays its own channel.</summary>
    StereoPair,
    /// <summary>One speaker of a stereo pair on its own.</summary>
    PairMember,
}

/// <summary>
/// What the user picks as an output.
///
/// A stereo pair (two HomePods sharing a tight-sync id) must be fed by streaming to <em>both</em>
/// speakers: the group leader does not relay a third-party realtime stream to its partner, but
/// each paired HomePod renders its own channel of whatever it receives. Streaming to one member
/// alone is also allowed; that target gets a mono mix so nothing is lost if the speaker only
/// renders its side.
/// </summary>
public sealed class AirPlayTarget
{
    public required string DisplayName { get; init; }
    public required TargetKind Kind { get; init; }

    /// <summary>The devices we open sessions with (two for a pair, otherwise one).</summary>
    public required IReadOnlyList<AirPlayDevice> Receivers { get; init; }

    /// <summary>For a <see cref="TargetKind.PairMember"/>: the pair it belongs to.</summary>
    public string? PairName { get; init; }

    public bool IsStereoPair => Kind == TargetKind.StereoPair;

    /// <summary>Send L = R = (L + R) / 2.</summary>
    public bool MonoDownmix => Kind == TargetKind.PairMember;

    public AirPlayDevice Primary => Receivers[0];

    /// <summary>Stable identity for settings (survives IP changes).</summary>
    public string Id => Kind == TargetKind.StereoPair
        ? "group:" + (Primary.TightSyncId ?? Primary.GroupId ?? Primary.DeviceId)
        : "device:" + Primary.DeviceId;

    public string Description => Kind switch
    {
        TargetKind.StereoPair => $"{DisplayName} — stereo pair ({string.Join(" + ", Receivers.Select(m => m.Name))})",
        TargetKind.PairMember => $"{DisplayName} — just this speaker of {PairName} (mono)",
        _ => $"{DisplayName} — {Primary.FriendlyModel}",
    };

    public override string ToString() => Description;

    /// <summary>Menu order: by name, with a pair's single-speaker entries directly beneath the pair.</summary>
    public static List<AirPlayTarget> Sort(IEnumerable<AirPlayTarget> targets) => targets
        .OrderBy(t => t.Kind == TargetKind.PairMember ? t.PairName : t.DisplayName, StringComparer.OrdinalIgnoreCase)
        .ThenBy(t => t.Kind == TargetKind.PairMember ? 1 : 0)
        .ThenBy(t => t.DisplayName, StringComparer.OrdinalIgnoreCase)
        .ToList();
}

public static class AirPlayBrowser
{
    const string Service = "_airplay._tcp.local.";

    public static async Task<List<AirPlayDevice>> DiscoverDevicesAsync(TimeSpan duration, CancellationToken ct = default)
    {
        var records = await Mdns.QueryAsync([(Service, Mdns.TypePtr)], duration, ct);
        var devices = Assemble(records);

        // Anything we saw a PTR for but no SRV/TXT/A for: ask explicitly.
        var instances = records.Where(r => r.Type == Mdns.TypePtr && Same(r.Name, Service)).Select(r => r.Target!).Distinct().ToList();
        var missing = instances.Where(i => !devices.Any(d => Same(d.Name + "." + Service, i))).ToList();
        if (missing.Count > 0)
        {
            var qs = missing.SelectMany(i => new[] { (i, Mdns.TypeSrv), (i, Mdns.TypeTxt) }).ToList();
            var more = await Mdns.QueryAsync(qs, TimeSpan.FromSeconds(1.5), ct);
            records.AddRange(more);
            var hosts = more.Where(r => r.Type == Mdns.TypeSrv).Select(r => r.Target!).Distinct().ToList();
            if (hosts.Count > 0)
                records.AddRange(await Mdns.QueryAsync(hosts.Select(h => (h, Mdns.TypeA)), TimeSpan.FromSeconds(1), ct));
            devices = Assemble(records);
        }
        return devices;
    }

    static bool Same(string a, string b) => string.Equals(a.TrimEnd('.'), b.TrimEnd('.'), StringComparison.OrdinalIgnoreCase);

    static List<AirPlayDevice> Assemble(List<DnsRecord> records)
    {
        var result = new Dictionary<string, AirPlayDevice>(StringComparer.OrdinalIgnoreCase);
        var instances = records.Where(r => r.Type == Mdns.TypePtr && Same(r.Name, Service)).Select(r => r.Target!)
            .Concat(records.Where(r => r.Type == Mdns.TypeSrv && r.Name.EndsWith(Service, StringComparison.OrdinalIgnoreCase)).Select(r => r.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var inst in instances)
        {
            var srv = records.LastOrDefault(r => r.Type == Mdns.TypeSrv && Same(r.Name, inst));
            var txt = records.LastOrDefault(r => r.Type == Mdns.TypeTxt && Same(r.Name, inst));
            if (srv == null || txt == null) continue;
            var addr = records.LastOrDefault(r => r.Type == Mdns.TypeA && Same(r.Name, srv.Target!))?.Address;
            if (addr == null) continue;
            var name = inst[..^(Service.Length + 1)];
            if (name.EndsWith('.')) name = name[..^1];
            name = name.Replace("\\032", " ").Replace("\\.", ".");
            result[inst] = new AirPlayDevice
            {
                Name = name,
                HostName = srv.Target!,
                Address = addr,
                Port = srv.Port,
                Txt = Mdns.ParseTxt(txt.Rdata!),
            };
        }
        return result.Values.OrderBy(d => d.Name).ToList();
    }

    /// <summary>
    /// One target per standalone speaker; for each stereo pair, the pair itself followed by each
    /// of its members on their own.
    /// </summary>
    public static List<AirPlayTarget> GroupTargets(IEnumerable<AirPlayDevice> devices)
    {
        var list = devices.Where(d => d.SupportsAirPlayAudio || d.IsHomePod).ToList();
        var targets = new List<AirPlayTarget>();
        var used = new HashSet<AirPlayDevice>();

        foreach (var grp in list.Where(d => !string.IsNullOrEmpty(d.TightSyncId)).GroupBy(d => d.TightSyncId!))
        {
            var members = grp.OrderBy(d => d.Name).ToList();
            if (members.Count < 2) continue;
            var name = members.Select(m => m.GroupName).FirstOrDefault(n => !string.IsNullOrEmpty(n)) ?? string.Join(" + ", members.Select(m => m.Name));
            targets.Add(new AirPlayTarget { DisplayName = name, Kind = TargetKind.StereoPair, Receivers = members });
            foreach (var m in members)
            {
                targets.Add(new AirPlayTarget { DisplayName = m.Name, Kind = TargetKind.PairMember, Receivers = [m], PairName = name });
                used.Add(m);
            }
        }

        foreach (var d in list.Where(d => !used.Contains(d)))
            targets.Add(new AirPlayTarget { DisplayName = d.Name, Kind = TargetKind.Single, Receivers = [d] });

        return AirPlayTarget.Sort(targets);
    }

    public static async Task<List<AirPlayTarget>> DiscoverTargetsAsync(TimeSpan duration, CancellationToken ct = default)
        => GroupTargets(await DiscoverDevicesAsync(duration, ct));

    /// <summary>Find the local IPv4 address the OS would use to reach <paramref name="remote"/>.</summary>
    public static IPAddress LocalAddressFor(IPAddress remote)
    {
        using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        s.Connect(new IPEndPoint(remote, 9));
        return ((IPEndPoint)s.LocalEndPoint!).Address;
    }
}
