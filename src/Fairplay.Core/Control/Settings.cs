using System.Text.Json;
using Fairplay.Util;

namespace Fairplay.Control;

public sealed class Settings
{
    /// <summary><see cref="Discovery.AirPlayTarget.Id"/> of the last speaker used.</summary>
    public string? LastTargetId { get; set; }
    public string? LastTargetName { get; set; }

    /// <summary>Playback endpoint to capture; null follows the Windows default output.</summary>
    public string? SourceDeviceId { get; set; }

    public int LatencyMs { get; set; } = 120;

    /// <summary>Windows volume slider / mute drives the speaker's volume.</summary>
    public bool SyncVolume { get; set; }

    /// <summary>Original endpoint names of devices we renamed after a speaker, so they can be restored.</summary>
    public Dictionary<string, string> RenamedDevices { get; set; } = new();

    /// <summary>Reconnect to the last speaker when the app starts.</summary>
    public bool AutoConnect { get; set; } = true;

    /// <summary>False for throwaway settings (e.g. the CLI) so they never overwrite the tray app's.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Persist { get; init; } = true;

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Fairplay");
    static string FilePath => Path.Combine(Folder, "settings.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch (Exception e)
        {
            Log.Warn($"settings unreadable, using defaults: {e.Message}");
        }
        return new Settings();
    }

    public void Save()
    {
        if (!Persist) return;
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception e)
        {
            Log.Warn($"could not save settings: {e.Message}");
        }
    }
}
