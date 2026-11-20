using System.Text.Json;
using System.Text.Json.Serialization;

namespace Forge.Core;

/// <summary>
/// %LOCALAPPDATA%\AutomationForge\hub-state.json (6.4): the choices Kovati Hub v2 remembers. Kept apart from
/// settings.json and installed.json on purpose: an older Hub or CLI rewrites those two from its own classes and
/// would silently drop fields it does not know. The window and the command line read it through this class.
///
/// The file never decides what is installed: the receipts and the folders on disk do. A missing or unreadable
/// file is the same as an empty one.
/// </summary>
public sealed class HubState
{
    public const int CurrentSchema = 1;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = CurrentSchema;

    /// <summary>"Use with AI agents". Null until the person has chosen, shown as off.</summary>
    [JsonPropertyName("agentAccess")] public bool? AgentAccess { get; set; }

    [JsonPropertyName("showLabs")] public bool ShowLabs { get; set; }

    /// <summary>The install target last used: an engine's plugin root.</summary>
    [JsonPropertyName("lastTarget")] public string? LastTarget { get; set; }

    [JsonPropertyName("tracks")] public List<TrackRecord> Tracks { get; set; } = [];

    [JsonPropertyName("projects")] public List<ProjectRecord> Projects { get; set; } = [];

    /// <summary>Component id to the version it is held at (8.3).</summary>
    [JsonPropertyName("pins")] public Dictionary<string, string> Pins { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Settings, "Project folders" (0.5.0-beta.3): folders the person added, scanned for projects to a bounded depth.</summary>
    [JsonPropertyName("projectFolders")] public List<string> ProjectFolders { get; set; } = [];

    /// <summary>The last project discovery, shown at once on the next start while a fresh one runs.</summary>
    [JsonPropertyName("projectCache")] public ProjectCache? ProjectCache { get; set; }

    /// <summary>Fields a newer Hub wrote that this one does not know: kept, and written back as they were.</summary>
    [JsonExtensionData] public Dictionary<string, System.Text.Json.JsonElement>? Unknown { get; set; }

    /// <summary>Whether the file was on disk when it was read. False on the first start of v2: the migration of 6.3 runs then.</summary>
    [JsonIgnore] public bool Existed { get; private set; }

    [JsonIgnore] public bool AgentsOn => AgentAccess == true;

    public static string FilePath => Path.Combine(Paths.DataDir, "hub-state.json");

    public static HubState Load(string? file = null)
    {
        var path = file ?? FilePath;
        try
        {
            if (File.Exists(path))
            {
                var s = JsonSerializer.Deserialize<HubState>(File.ReadAllText(path), Json) ?? new HubState();
                s.Pins = new Dictionary<string, string>(s.Pins, StringComparer.OrdinalIgnoreCase);
                foreach (var t in s.Tracks) t.Engines = new Dictionary<string, List<string>>(t.Engines, StringComparer.OrdinalIgnoreCase);
                s.Existed = true;
                return s;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { }
        return new HubState();
    }

    public void Save(string? file = null)
    {
        var path = file ?? FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
        Existed = true;
    }

    /// <summary>
    /// People who come from an older Hub (6.3), on the first start of v2: agent access is on when anything
    /// installed is an agent component, otherwise it stays unset. No track is recorded and nothing is
    /// installed, removed or changed. Returns true when it ran (and saved).
    /// </summary>
    public bool MigrateIfNew(IEnumerable<string> installedComponents, Func<string, bool> isAgentComponent, string? file = null)
    {
        if (Existed) return false;
        if (installedComponents.Any(isAgentComponent)) AgentAccess = true;
        Save(file);
        return true;
    }

    public TrackRecord? Track(string track, string target) =>
        Tracks.FirstOrDefault(t => string.Equals(t.Track, track, StringComparison.OrdinalIgnoreCase)
                                 && string.Equals(t.Target, target, StringComparison.OrdinalIgnoreCase));

    /// <summary>Remember what was installed through a track page, per track and target.</summary>
    public void RecordTrack(string track, string target, IEnumerable<string> capabilities, IReadOnlyDictionary<string, IReadOnlyCollection<string>> engines)
    {
        var record = Track(track, target);
        if (record is null)
        {
            record = new TrackRecord { Track = track, Target = target };
            Tracks.Add(record);
        }
        record.InstalledAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        record.Capabilities = capabilities.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        record.Engines = engines.ToDictionary(kv => kv.Key, kv => kv.Value.ToList(), StringComparer.OrdinalIgnoreCase);
    }
}

public sealed class TrackRecord
{
    [JsonPropertyName("track")] public string Track { get; set; } = "";
    [JsonPropertyName("target")] public string Target { get; set; } = "";
    [JsonPropertyName("installedAt")] public string InstalledAt { get; set; } = "";
    [JsonPropertyName("capabilities")] public List<string> Capabilities { get; set; } = [];
    [JsonPropertyName("engines")] public Dictionary<string, List<string>> Engines { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    [JsonPropertyName("checklistHidden")] public bool ChecklistHidden { get; set; }
    [JsonPropertyName("firstResultOpened")] public bool FirstResultOpened { get; set; }
}

public sealed class ProjectRecord
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("addedAt")] public string AddedAt { get; set; } = "";
}

/// <summary>The projects found last time, and what each of the person's folders gave.</summary>
public sealed class ProjectCache
{
    [JsonPropertyName("scannedAt")] public string ScannedAt { get; set; } = "";
    [JsonPropertyName("projects")] public List<CachedProject> Projects { get; set; } = [];
    [JsonPropertyName("folders")] public List<CachedFolder> Folders { get; set; } = [];
}

public sealed class CachedProject
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    /// <summary>recent, created-folder, user-folder, receipt, remembered.</summary>
    [JsonPropertyName("sources")] public List<string> Sources { get; set; } = [];
    [JsonPropertyName("lastOpened")] public string? LastOpened { get; set; }
}

public sealed class CachedFolder
{
    [JsonPropertyName("folder")] public string Folder { get; set; } = "";
    [JsonPropertyName("found")] public int Found { get; set; }
    [JsonPropertyName("line")] public string Line { get; set; } = "";
    [JsonPropertyName("complete")] public bool Complete { get; set; }
}
