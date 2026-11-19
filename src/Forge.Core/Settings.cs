using System.Text.Json;
using System.Text.Json.Nodes;
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

    /// <summary>
    /// One more folder of plugins to look at for keys and runners, beside the engines.
    ///
    /// The hub sees what it installed: plugins under an engine's Plugins/Marketplace/AutomationForge, downloaded
    /// from a release. That is right for everybody who uses it and wrong for whoever is *writing*
    /// the plugin, whose copy lives in a working tree and whose declarations therefore cannot appear
    /// here until they ship. This is the way in - point it at that tree.
    ///
    /// Empty for everyone else, and it changes nothing when it is.
    /// </summary>
    [JsonPropertyName("extraPluginRoot")] public string ExtraPluginRoot { get; set; } = "";

    [JsonIgnore] public bool IsNightly => Channel == Nightly;

    public static string FilePath => Path.Combine(Paths.DataDir, "settings.json");

    /// <summary>The values as they were read or last written, to tell what this instance changed.</summary>
    private JsonObject _base = new();
    private string? _file;

    public static Settings Load(string? file = null)
    {
        var path = file ?? FilePath;
        var s = new Settings();
        try
        {
            if (File.Exists(path))
                s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path)) ?? new Settings();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // An unreadable settings file is the same as none; defaults are safe.
        }
        s._file = file;
        s._base = s.Snapshot();
        return s;
    }

    /// <summary>
    /// Write what this instance changed, and only that. The file is shared by the CLI, by Kovati Hub v2 and by the
    /// 0.4.x Hub beside it, and each holds its own copy for as long as it runs: writing the whole copy would put back
    /// a value another one changed since. The file is read again, the fields changed here since the last read or
    /// write are set on it, fields this build does not know are kept, and it is written atomically.
    /// </summary>
    public void Save()
    {
        var path = _file ?? FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (Installs.InstallState.AcquireLock(path + ".lock"))
        {
            JsonObject disk;
            try { disk = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject() : new JsonObject(); }
            catch (Exception ex) when (ex is JsonException or IOException) { disk = new JsonObject(); }
            var mine = Snapshot();
            foreach (var (name, value) in mine)
                if (!disk.ContainsKey(name) || !JsonNode.DeepEquals(value, _base[name]))
                    disk[name] = value?.DeepClone();
            Catalogue.CatalogueClient.WriteAtomically(path, disk.ToJsonString(Json));
            _base = mine;
        }
    }

    private JsonObject Snapshot() => JsonSerializer.SerializeToNode(this, Json) as JsonObject ?? new JsonObject();
}
