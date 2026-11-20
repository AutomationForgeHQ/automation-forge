using System.Text.Json;
using Forge.Core.Engines;
using Forge.Core.Installs;

namespace Forge.Core.Projects;

/// <summary>A copy of a plugin on disk: where, which version, and what its descriptor says about being on by default.</summary>
public sealed record PluginCopy(string Id, string Folder, string? Version, bool? EnabledByDefault, bool ManagedByHub);

/// <summary>Why a plugin is on or off in a project, in the words the Projects page uses.</summary>
public enum EnabledReason
{
    /// <summary>The .uproject turns it on or off by name.</summary>
    Explicit,
    /// <summary>Not named; a plugin inside the project is on unless its descriptor says otherwise.</summary>
    ProjectDefault,
    /// <summary>Not named; a plugin in the engine is off unless its descriptor says EnabledByDefault.</summary>
    EngineDefault,
    /// <summary>Not named, off by default, but a plugin that is on depends on it, so Unreal loads it too.</summary>
    NeededByEnabled,
    /// <summary>Named in the .uproject, but no copy is in the project or its engine.</summary>
    Missing,
}

/// <summary>One Kovati tool as one project sees it.</summary>
public sealed record ProjectPluginState
{
    public required string Id { get; init; }
    /// <summary>A copy inside the project (Plugins\AutomationForge from the Hub, or anywhere else under Plugins).</summary>
    public PluginCopy? ProjectCopy { get; init; }
    /// <summary>The copy in the project's engine (Engine\Plugins\Marketplace\AutomationForge).</summary>
    public PluginCopy? EngineCopy { get; init; }
    /// <summary>The .uproject entry's Enabled, or null when it does not name the plugin.</summary>
    public bool? Entry { get; init; }
    public bool Enabled { get; init; }
    public EnabledReason Reason { get; init; }
    /// <summary>For <see cref="EnabledReason.NeededByEnabled"/>: the plugin that needs it.</summary>
    public string? NeededBy { get; init; }

    /// <summary>The copy Unreal loads: the project's own when there is one.</summary>
    public PluginCopy? Loaded => ProjectCopy ?? EngineCopy;
    public bool InProject => ProjectCopy is not null;
    public bool InEngine => EngineCopy is not null;
    public bool Overrides => ProjectCopy is not null && EngineCopy is not null;
}

/// <summary>Everything the Projects page and `forge projects` say about one project.</summary>
public sealed record ProjectReport
{
    public required FoundProject Found { get; init; }
    public required ResolvedEngine Engine { get; init; }
    public bool IsCodeProject { get; init; }
    public bool IsReadOnly { get; init; }
    /// <summary>"Git", "Perforce", "Plastic SCM", "Subversion", or null.</summary>
    public string? SourceControl { get; init; }
    public IReadOnlyList<ProjectPluginState> Plugins { get; init; } = [];
    public string? Error { get; init; }

    public string Path => Found.Path;
    public string Name => Found.Name;
    /// <summary>Where Kovati Hub installs into this project.</summary>
    public string PluginRoot => System.IO.Path.Combine(Found.Folder, "Plugins", Paths.PluginFolder);
}

/// <summary>
/// Reads one project: its .uproject, the Kovati plugins inside it, and the ones its engine has. Only ids the release
/// manifest knows count as Kovati plugins. Reads only; nothing is written.
/// </summary>
public static class ProjectInspector
{
    /// <param name="project">A found project.</param>
    /// <param name="engines">The engines on this machine.</param>
    /// <param name="kovatiIds">Every component id in the manifest.</param>
    /// <param name="dependencies">Component id to the ids it depends on (the manifest's).</param>
    /// <param name="receipts">installed.json: which project folders the Hub put there.</param>
    public static ProjectReport Inspect(FoundProject project, IReadOnlyList<EngineInstall> engines, IReadOnlySet<string> kovatiIds,
        IReadOnlyDictionary<string, IReadOnlyList<string>> dependencies, IEnumerable<InstalledPlugin> receipts,
        IReadOnlyDictionary<string, string>? registry = null)
    {
        ProjectDescriptor descriptor;
        try { descriptor = new ProjectDescriptor(project.Path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or InvalidOperationException)
        {
            return new ProjectReport
            {
                Found = project, Engine = new ResolvedEngine(AssociationKind.Version, "", null, null),
                Error = $"Could not read {System.IO.Path.GetFileName(project.Path)}: {ex.Message}",
            };
        }

        var engine = EngineAssociations.Resolve(descriptor.EngineAssociation, project.Path, engines, registry);
        var managedRoot = System.IO.Path.Combine(project.Folder, "Plugins", Paths.PluginFolder);
        var managedByReceipt = new HashSet<string>(receipts
            .Where(r => string.Equals(r.TargetRoot.TrimEnd('\\', '/'), managedRoot, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Plugin), StringComparer.OrdinalIgnoreCase);

        var inProject = ProjectCopies(System.IO.Path.Combine(project.Folder, "Plugins"), managedRoot, kovatiIds, managedByReceipt);
        var inEngine = engine.Engine is { } e
            ? EngineCopies(e, kovatiIds)
            : new Dictionary<string, PluginCopy>(StringComparer.OrdinalIgnoreCase);
        var entries = descriptor.Plugins
            .Where(p => kovatiIds.Contains(p.Name))
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last().Enabled, StringComparer.OrdinalIgnoreCase);

        var ids = inProject.Keys.Concat(inEngine.Keys).Concat(entries.Keys).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        var states = ids.Select(id =>
        {
            inProject.TryGetValue(id, out var pc);
            inEngine.TryGetValue(id, out var ec);
            var hasEntry = entries.TryGetValue(id, out var entry);
            var (on, reason) = hasEntry && (pc ?? ec) is null ? (false, EnabledReason.Missing)
                : hasEntry && entry is { } explicitOn ? (explicitOn, EnabledReason.Explicit)
                : pc is not null ? (pc.EnabledByDefault ?? true, EnabledReason.ProjectDefault)
                : (ec?.EnabledByDefault ?? false, EnabledReason.EngineDefault);
            return new ProjectPluginState { Id = id, ProjectCopy = pc, EngineCopy = ec, Entry = hasEntry ? entry : null, Enabled = on, Reason = reason };
        }).ToList();

        // A plugin that is off by default but needed by one that is on: Unreal loads it anyway.
        bool changed;
        do
        {
            changed = false;
            for (var i = 0; i < states.Count; i++)
            {
                var s = states[i];
                if (s.Enabled || s.Reason is EnabledReason.Explicit or EnabledReason.Missing || s.Loaded is null) continue;
                var by = states.FirstOrDefault(o => o.Enabled && dependencies.TryGetValue(o.Id, out var deps) && deps.Contains(s.Id, StringComparer.OrdinalIgnoreCase));
                if (by is null) continue;
                states[i] = s with { Enabled = true, Reason = EnabledReason.NeededByEnabled, NeededBy = by.Id };
                changed = true;
            }
        } while (changed);

        return new ProjectReport
        {
            Found = project,
            Engine = engine,
            IsCodeProject = descriptor.IsCodeProject,
            IsReadOnly = descriptor.IsReadOnly,
            SourceControl = SourceControlOf(project.Folder),
            Plugins = states,
        };
    }

    /// <summary>
    /// Kovati plugins under the project's Plugins folder, at most five levels down, without entering the folders a
    /// plugin fills. Plugins\AutomationForge is Kovati Hub's; any other copy is the project's own (a source tree)
    /// and is never changed by the Hub.
    /// </summary>
    public static Dictionary<string, PluginCopy> ProjectCopies(string pluginsFolder, string managedRoot, IReadOnlySet<string> kovatiIds, IReadOnlySet<string> managedByReceipt)
    {
        var map = new Dictionary<string, PluginCopy>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(pluginsFolder)) return map;
        var queue = new Queue<(string dir, int depth)>();
        queue.Enqueue((pluginsFolder, 0));
        var visited = 0;
        while (queue.Count > 0 && visited < 3000)
        {
            var (dir, depth) = queue.Dequeue();
            visited++;
            string[] descriptors;
            try { descriptors = Directory.GetFiles(dir, "*.uplugin"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            if (descriptors.Length > 0)
            {
                foreach (var d in descriptors)
                {
                    var id = System.IO.Path.GetFileNameWithoutExtension(d);
                    if (!kovatiIds.Contains(id) || map.ContainsKey(id)) continue;
                    var managed = string.Equals(System.IO.Path.GetDirectoryName(dir)?.TrimEnd('\\', '/'), managedRoot.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)
                                  || managedByReceipt.Contains(id) && dir.StartsWith(managedRoot, StringComparison.OrdinalIgnoreCase);
                    map[id] = Read(id, dir, d, managed);
                }
                continue; // a plugin's own folders hold no other plugin worth reading
            }
            if (depth >= 5) continue;
            string[] subs;
            try { subs = Directory.GetDirectories(dir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var s in subs)
            {
                var name = System.IO.Path.GetFileName(s);
                if (name.StartsWith('.') || name is "Binaries" or "Intermediate" or "Source" or "Content" or "Resources" or "Saved" or "ThirdParty" or "Config") continue;
                queue.Enqueue((s, depth + 1));
            }
        }
        return map;
    }

    /// <summary>The engine's Kovati plugins: Marketplace\AutomationForge, and the folder older Hubs used.</summary>
    public static Dictionary<string, PluginCopy> EngineCopies(EngineInstall engine, IReadOnlySet<string> kovatiIds)
    {
        var map = new Dictionary<string, PluginCopy>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in new[] { engine.PluginRoot, engine.LegacyPluginRoot })
        {
            if (!Directory.Exists(root)) continue;
            string[] dirs;
            try { dirs = Directory.GetDirectories(root); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var dir in dirs)
            {
                var id = System.IO.Path.GetFileName(dir);
                var d = System.IO.Path.Combine(dir, id + ".uplugin");
                if (!kovatiIds.Contains(id) || map.ContainsKey(id) || !File.Exists(d)) continue;
                map[id] = Read(id, dir, d, managed: true);
            }
        }
        return map;
    }

    private static PluginCopy Read(string id, string dir, string descriptor, bool managed)
    {
        string? version = null;
        bool? byDefault = null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(descriptor), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (doc.RootElement.TryGetProperty("VersionName", out var v) && v.ValueKind == JsonValueKind.String) version = v.GetString();
            if (doc.RootElement.TryGetProperty("EnabledByDefault", out var b) && b.ValueKind is JsonValueKind.True or JsonValueKind.False) byDefault = b.GetBoolean();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return new PluginCopy(id, dir, version, byDefault, managed);
    }

    /// <summary>The project's source control, by its marker in the project folder or above it. No command is run.</summary>
    public static string? SourceControlOf(string folder)
    {
        try
        {
            for (var dir = new DirectoryInfo(folder); dir is not null; dir = dir.Parent)
            {
                if (Directory.Exists(System.IO.Path.Combine(dir.FullName, ".git")) || File.Exists(System.IO.Path.Combine(dir.FullName, ".git"))) return "Git";
                if (File.Exists(System.IO.Path.Combine(dir.FullName, ".p4config")) || File.Exists(System.IO.Path.Combine(dir.FullName, ".p4ignore")) || File.Exists(System.IO.Path.Combine(dir.FullName, "p4config.txt"))) return "Perforce";
                if (Directory.Exists(System.IO.Path.Combine(dir.FullName, ".plastic"))) return "Plastic SCM";
                if (Directory.Exists(System.IO.Path.Combine(dir.FullName, ".svn"))) return "Subversion";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        return null;
    }
}
