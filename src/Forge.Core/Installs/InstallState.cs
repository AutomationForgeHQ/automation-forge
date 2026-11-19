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
        Items = ReadFromDisk();
        // A receipt for a folder that no longer exists is stale; drop it quietly.
        Items.RemoveAll(i => !Directory.Exists(i.Path));
    }

    /// <summary>
    /// The file as it is now. Another writer (the CLI, the elevated helper, the 0.4.x Hub beside this one) may be
    /// halfway through writing it; a read that fails is tried again a few times before it counts.
    /// </summary>
    private List<InstalledPlugin> ReadFromDisk()
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return File.Exists(_file)
                    ? JsonSerializer.Deserialize<List<InstalledPlugin>>(File.ReadAllText(_file)) ?? []
                    : [];
            }
            catch (Exception ex) when (attempt < 5 && ex is IOException or JsonException or UnauthorizedAccessException)
            {
                Thread.Sleep(60);
            }
        }
    }

    public InstalledPlugin? Find(InstallTarget target, string plugin) =>
        Items.FirstOrDefault(i => target.Holds(i.TargetRoot)
                               && string.Equals(i.Plugin, plugin, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<InstalledPlugin> On(InstallTarget target) =>
        Items.Where(i => target.Holds(i.TargetRoot));

    /// <summary>A plugin folder moved from one root to another: its receipt follows it.</summary>
    public void Moved(string fromRoot, string toRoot, string plugin, string path) => Change(items =>
    {
        foreach (var i in items.Where(i => string.Equals(i.TargetRoot, fromRoot, StringComparison.OrdinalIgnoreCase)
                                         && string.Equals(i.Plugin, plugin, StringComparison.OrdinalIgnoreCase)))
        {
            i.TargetRoot = toRoot;
            i.Path = path;
        }
    });

    public void Record(InstalledPlugin item) => Change(items =>
    {
        items.RemoveAll(i => string.Equals(i.TargetRoot, item.TargetRoot, StringComparison.OrdinalIgnoreCase)
                          && string.Equals(i.Plugin, item.Plugin, StringComparison.OrdinalIgnoreCase));
        items.Add(item);
    });

    public void Forget(InstallTarget target, string plugin) => Change(items =>
        items.RemoveAll(i => target.Holds(i.TargetRoot)
                          && string.Equals(i.Plugin, plugin, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// One change to the receipts, made to the file as it is now and not to the copy this instance read earlier:
    /// two Hubs share installed.json (the beta beside 0.4.x), and a whole-file write from a stale copy would drop
    /// whatever the other one recorded in between. Under a lock file for writers that know it, the receipts are read
    /// again, changed, and written atomically (a temporary file, then a move) for readers that do not.
    /// </summary>
    private void Change(Action<List<InstalledPlugin>> change)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_file)!);
        using (AcquireLock(_file + ".lock"))
        {
            var items = ReadFromDisk();
            change(items);
            items.RemoveAll(i => !Directory.Exists(i.Path));
            Catalogue.CatalogueClient.WriteAtomically(_file, JsonSerializer.Serialize(items, Json));
            Items = items;
        }
    }

    /// <summary>
    /// An exclusive lock file, waited for up to five seconds. A lock that cannot be had (a writer that hangs, a
    /// folder that refuses the file) does not stop the change: the re-read already closes nearly all of the window.
    /// </summary>
    internal static IDisposable? AcquireLock(string path, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            // A held lock is a sharing violation; one being deleted as its holder lets go reads as access denied.
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (Environment.TickCount64 >= deadline) return null;
                Thread.Sleep(40);
            }
        }
    }
}
