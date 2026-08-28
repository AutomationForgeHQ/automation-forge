using System.Text.Json;
using System.Text.Json.Serialization;

namespace Forge.Core.Installs;

/// <summary>Where a plugin goes: an engine's plugin root, or a project's.</summary>
public sealed record InstallTarget(string Kind, string Root, string Label)
{
    public static InstallTarget Engine(Engines.EngineInstall e) => new("engine", e.PluginRoot, $"UE {e.FullVersion}");

    public static InstallTarget Project(string uprojectOrDir)
    {
        var dir = File.Exists(uprojectOrDir) ? Path.GetDirectoryName(Path.GetFullPath(uprojectOrDir))! : Path.GetFullPath(uprojectOrDir);
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
        Items.FirstOrDefault(i => string.Equals(i.TargetRoot, target.Root, StringComparison.OrdinalIgnoreCase)
                               && string.Equals(i.Plugin, plugin, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<InstalledPlugin> On(InstallTarget target) =>
        Items.Where(i => string.Equals(i.TargetRoot, target.Root, StringComparison.OrdinalIgnoreCase));

    public void Record(InstalledPlugin item)
    {
        Items.RemoveAll(i => string.Equals(i.TargetRoot, item.TargetRoot, StringComparison.OrdinalIgnoreCase)
                          && string.Equals(i.Plugin, item.Plugin, StringComparison.OrdinalIgnoreCase));
        Items.Add(item);
        Save();
    }

    public void Forget(InstallTarget target, string plugin)
    {
        Items.RemoveAll(i => string.Equals(i.TargetRoot, target.Root, StringComparison.OrdinalIgnoreCase)
                          && string.Equals(i.Plugin, plugin, StringComparison.OrdinalIgnoreCase));
        Save();
    }

    private void Save()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_file)!);
        File.WriteAllText(_file, JsonSerializer.Serialize(Items, Json));
    }
}
