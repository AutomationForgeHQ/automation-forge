using System.Text.Json;
using System.Text.Json.Serialization;

namespace Forge.Core.Installs;

/// <summary>
/// Where a plugin goes: an engine's plugin root, or a project's. LegacyRoot is where an older hub put
/// the same target's plugins (an engine's Engine/Plugins/AutomationForge); its receipts still belong here.
/// </summary>
public sealed record InstallTarget(string Kind, string Root, string Label, string? LegacyRoot = null)
{
    public static InstallTarget Engine(Engines.EngineInstall e) => new("engine", e.PluginRoot, $"UE {e.FullVersion}", e.LegacyPluginRoot);

    /// <summary>A receipt's root belongs to this target: the current root, or the legacy one.</summary>
    public bool Holds(string root) =>
        string.Equals(root, Root, StringComparison.OrdinalIgnoreCase)
        || (LegacyRoot is not null && string.Equals(root, LegacyRoot, StringComparison.OrdinalIgnoreCase));

    /// <summary>The plugin's folder under the legacy root, or null when the target has none.</summary>
    public string? LegacyPluginDir(string plugin) => LegacyRoot is null ? null : Path.Combine(LegacyRoot, plugin);

    /// <summary>
    /// A project by its .uproject or the folder holding one. A path that is not
    /// there at all is the common typo, so it is named as such rather than
    /// reaching Directory.GetFiles and surfacing as a stack trace.
    /// </summary>
    public static InstallTarget Project(string uprojectOrDir)
    {
        var full = Path.GetFullPath(uprojectOrDir);
        if (!File.Exists(full) && !Directory.Exists(full))
            throw new FileNotFoundException($"No project at {full}. Give the .uproject file, or the folder holding it.");
        var dir = File.Exists(full) ? Path.GetDirectoryName(full)! : full;
        var uproject = Directory.GetFiles(dir, "*.uproject").FirstOrDefault()
                       ?? throw new FileNotFoundException($"No .uproject in {dir}");
        return new("project", Path.Combine(dir, "Plugins", Core.Paths.PluginFolder), Path.GetFileNameWithoutExtension(uproject));
    }

    public string PluginDir(string plugin) => Path.Combine(Root, plugin);
}

/// <summary>One installed plugin, as the hub remembers it. The folder on disk is the truth; this is the receipt.</summary>
public sealed class InstalledPlugin
{
    [JsonPropertyName("plugin")] public string Plugin { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("engine")] public string Engine { get; set; } = "";
    [JsonPropertyName("channel")] public string Channel { get; set; } = "stable";
    [JsonPropertyName("sha256")] public string? Sha256 { get; set; }
    [JsonPropertyName("targetKind")] public string TargetKind { get; set; } = "";
    [JsonPropertyName("targetRoot")] public string TargetRoot { get; set; } = "";
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("installedAt")] public string InstalledAt { get; set; } = "";
}

/// <summary>%LOCALAPPDATA%\AutomationForge\installed.json — every install the hub made, on every target.</summary>
public sealed class InstallState
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _file;

    public List<InstalledPlugin> Items { get; private set; } = [];

    public InstallState(string? file = null)
    {
        _file = file ?? System.IO.Path.Combine(Core.Paths.DataDir, "installed.json");
        Reload();
    }

    /// <summary>Re-read the receipts — after another process (the elevated CLI) has written them.</summary>
    public void Reload()
    {
        Items = File.Exists(_file)
            ? JsonSerializer.Deserialize<List<InstalledPlugin>>(File.ReadAllText(_file)) ?? []
            : [];
        // A receipt for a folder that no longer exists is stale; drop it quietly.
        Items.RemoveAll(i => !Directory.Exists(i.Path));
    }

    public InstalledPlugin? Find(InstallTarget target, string plugin) =>
        Items.FirstOrDefault(i => target.Holds(i.TargetRoot)
                               && string.Equals(i.Plugin, plugin, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<InstalledPlugin> On(InstallTarget target) =>
        Items.Where(i => target.Holds(i.TargetRoot));

    /// <summary>A plugin folder moved from one root to another: its receipt follows it.</summary>
    public void Moved(string fromRoot, string toRoot, string plugin, string path)
    {
        foreach (var i in Items.Where(i => string.Equals(i.TargetRoot, fromRoot, StringComparison.OrdinalIgnoreCase)
                                         && string.Equals(i.Plugin, plugin, StringComparison.OrdinalIgnoreCase)))
        {
            i.TargetRoot = toRoot;
            i.Path = path;
        }
        Save();
    }

    public void Record(InstalledPlugin item)
    {
        Items.RemoveAll(i => string.Equals(i.TargetRoot, item.TargetRoot, StringComparison.OrdinalIgnoreCase)
                          && string.Equals(i.Plugin, item.Plugin, StringComparison.OrdinalIgnoreCase));
        Items.Add(item);
        Save();
    }

    public void Forget(InstallTarget target, string plugin)
    {
        Items.RemoveAll(i => target.Holds(i.TargetRoot)
                          && string.Equals(i.Plugin, plugin, StringComparison.OrdinalIgnoreCase));
        Save();
    }

    private void Save()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_file)!);
        File.WriteAllText(_file, JsonSerializer.Serialize(Items, Json));
    }
}
