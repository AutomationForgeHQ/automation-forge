using System.Text.Json;
using System.Text.Json.Serialization;

namespace Forge.Core;

/// <summary>
/// %LOCALAPPDATA%\AutomationForge\settings.json — the few choices a person makes
/// once. The hub and the CLI read the same file, so `forge` follows the channel
/// chosen in the hub unless told otherwise on the command line.
/// </summary>
public sealed class Settings
{
    public const string Stable = "stable";
    public const string Nightly = "nightly";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>stable — tagged releases only. nightly — whatever is newest, including nightly builds.</summary>
    [JsonPropertyName("channel")] public string Channel { get; set; } = Stable;

    /// <summary>Whether the hub looks for a newer hub when it starts.</summary>
    [JsonPropertyName("checkForUpdates")] public bool CheckForUpdates { get; set; } = true;

    /// <summary>Keep the hub in the tray when its window closes, watching for updates.</summary>
    [JsonPropertyName("runInBackground")] public bool RunInBackground { get; set; } = true;

    /// <summary>Raise a system notification when the watcher finds something new.</summary>
    [JsonPropertyName("notifyOnUpdates")] public bool NotifyOnUpdates { get; set; } = true;

    [JsonIgnore] public bool IsNightly => Channel == Nightly;

    public static string FilePath => Path.Combine(Paths.DataDir, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // An unreadable settings file is the same as none; defaults are safe.
        }
        return new Settings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Paths.DataDir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
    }
}
