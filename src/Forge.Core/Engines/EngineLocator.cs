using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Forge.Core.Engines;

/// <summary>
/// An Unreal Engine installation the hub can install plugins into. <see cref="RegistryId"/> is the name a source
/// build is registered under in HKCU\Software\Epic Games\Unreal Engine\Builds: the GUID a .uproject's
/// EngineAssociation names. <see cref="CompatibleChangelist"/> and <see cref="BranchName"/> come from
/// Engine/Build/Build.version (a precompiled plugin loads where its BuildId equals the changelist).
/// </summary>
public sealed record EngineInstall(string Version, string FullVersion, string Path, bool IsLauncherBuild,
    string? RegistryId = null, int? CompatibleChangelist = null, string? BranchName = null)
{
    /// <summary>
    /// Engine/Plugins/Marketplace/AutomationForge, beside what Fab installs. On a launcher engine
    /// UnrealBuildTool compiles the build rules of plugins under Engine/Plugins/Marketplace when a build
    /// needs them; every other engine plugin must already be in the engine's precompiled rules. A plugin
    /// installed anywhere else in Engine/Plugins stopped every C++ build on that engine, project or
    /// BuildPlugin, with "Expecting to find a type ... named 'ForgeKeys'" (RulesCompiler.cs).
    /// </summary>
    public string PluginRoot => System.IO.Path.Combine(Path, "Engine", "Plugins", "Marketplace", Core.Paths.PluginFolder);

    /// <summary>
    /// Engine/Plugins/AutomationForge, where hubs before 0.4.3 installed. Still read, so those installs
    /// count as installed, and emptied by <see cref="Installs.Installer.MoveLegacyEngineInstalls"/>.
    /// </summary>
    public string LegacyPluginRoot => System.IO.Path.Combine(Path, "Engine", "Plugins", Core.Paths.PluginFolder);

    /// <summary>"Unreal Engine 5.8.4", or "Unreal Engine 5.8.4 (source build)".</summary>
    public string DisplayName => $"Unreal Engine {FullVersion}" + (IsLauncherBuild ? "" : " (source build)");

    public override string ToString() => $"UE {FullVersion} at {Path}" + (IsLauncherBuild ? "" : " (source build)");
}

/// <summary>Finds engines the way the Epic launcher records them, plus source builds from the registry.</summary>
public static class EngineLocator
{
    private static readonly Regex MajorMinor = new(@"^(\d+\.\d+)", RegexOptions.Compiled);

    /// <summary>
    /// FORGE_ENGINES: engine folders separated by ';'. When set, only these are found, so a test can point the
    /// Hub and the CLI at a throwaway engine folder and never see the real ones.
    /// </summary>
    public const string OverrideVariable = "FORGE_ENGINES";

    public static IReadOnlyList<EngineInstall> Find()
    {
        if (Environment.GetEnvironmentVariable(OverrideVariable) is { Length: > 0 } only)
            return only.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(FromPath).OfType<EngineInstall>().OrderByDescending(e => e.Version, VersionOrder.Instance).ToList();

        var found = new List<EngineInstall>();
        found.AddRange(FromLauncherManifest());
        var registry = FromRegistryBuilds().ToList();
        // A launcher engine that is also registered keeps its launcher entry, with the registry name added.
        foreach (var r in registry)
        {
            var i = found.FindIndex(e => SamePath(e.Path, r.Path));
            if (i >= 0) found[i] = found[i] with { RegistryId = r.RegistryId };
            else found.Add(r);
        }
        return found
            .GroupBy(e => e.Path.TrimEnd('\\', '/'), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderByDescending(e => e.Version, VersionOrder.Instance)
            .ToList();
    }

    /// <summary>Pick by "5.8", a full version, a registry GUID, or a path.</summary>
    public static EngineInstall? Resolve(string? spec, IReadOnlyList<EngineInstall>? engines = null)
    {
        engines ??= Find();
        if (string.IsNullOrWhiteSpace(spec))
            return engines.FirstOrDefault();
        if (Directory.Exists(spec))
        {
            var full = System.IO.Path.GetFullPath(spec).TrimEnd('\\', '/');
            return engines.FirstOrDefault(e => SamePath(e.Path, full)) ?? FromPath(full);
        }
        if (EngineAssociations.IsGuid(spec))
            return engines.FirstOrDefault(e => EngineAssociations.SameGuid(e.RegistryId, spec));
        return engines.FirstOrDefault(e => e.Version == spec || e.FullVersion == spec);
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<EngineInstall> FromLauncherManifest()
    {
        if (!OperatingSystem.IsWindows()) yield break;
        var file = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                                "Epic", "UnrealEngineLauncher", "LauncherInstalled.dat");
        if (!File.Exists(file)) yield break;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(File.ReadAllText(file)); }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { yield break; }
        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("InstallationList", out var list)) yield break;
            foreach (var item in list.EnumerateArray())
            {
                var app = item.TryGetProperty("AppName", out var a) ? a.GetString() ?? "" : "";
                if (!app.StartsWith("UE_", StringComparison.Ordinal)) continue; // FabPlugin_5.8, QuixelBridge_5.8 share the folder
                var location = item.GetProperty("InstallLocation").GetString() ?? "";
                var appVersion = item.TryGetProperty("AppVersion", out var v) ? v.GetString() ?? "" : "";
                var full = appVersion.Split('-')[0];                  // 5.8.1-56057345+++UE5+Release-5.8-Windows → 5.8.1
                var mm = MajorMinor.Match(full) is { Success: true } m ? m.Groups[1].Value : app[3..];
                if (!Directory.Exists(location)) continue;
                // Build.version carries the changelist; the launcher's own record carries the version people see.
                var read = FromPath(location);
                yield return new EngineInstall(mm, full.Length > 0 ? full : read?.FullVersion ?? mm, location, IsLauncherBuild: true,
                    CompatibleChangelist: read?.CompatibleChangelist, BranchName: read?.BranchName);
            }
        }
    }

    private static IEnumerable<EngineInstall> FromRegistryBuilds()
    {
        if (!OperatingSystem.IsWindows()) yield break;
        using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Epic Games\Unreal Engine\Builds");
        if (key is null) yield break;
        foreach (var name in key.GetValueNames())
        {
            if (key.GetValue(name) is not string path || !Directory.Exists(path)) continue;
            // The value name is what a .uproject's EngineAssociation names: keep it.
            if (FromPath(path) is { } e) yield return e with { IsLauncherBuild = false, RegistryId = name };
        }
    }

    /// <summary>The registry's source builds by name, for resolving a GUID association. Empty off Windows.</summary>
    public static IReadOnlyDictionary<string, string> RegisteredBuilds()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!OperatingSystem.IsWindows()) return map;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Epic Games\Unreal Engine\Builds");
            if (key is null) return map;
            foreach (var name in key.GetValueNames())
                if (key.GetValue(name) is string path) map[name] = path;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException) { }
        return map;
    }

    /// <summary>Read Engine/Build/Build.version from an engine folder.</summary>
    public static EngineInstall? FromPath(string path)
    {
        var versionFile = System.IO.Path.Combine(path, "Engine", "Build", "Build.version");
        if (!File.Exists(versionFile)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(versionFile));
            var root = doc.RootElement;
            var major = root.GetProperty("MajorVersion").GetInt32();
            var minor = root.GetProperty("MinorVersion").GetInt32();
            var patch = root.TryGetProperty("PatchVersion", out var p) ? p.GetInt32() : 0;
            var isLauncher = root.TryGetProperty("IsPromotedBuild", out var promoted) && promoted.GetInt32() == 1;
            int? changelist = root.TryGetProperty("CompatibleChangelist", out var cl) && cl.ValueKind == JsonValueKind.Number ? cl.GetInt32() : null;
            var branch = root.TryGetProperty("BranchName", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;
            return new EngineInstall($"{major}.{minor}", $"{major}.{minor}.{patch}", path, isLauncher, null, changelist, branch);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }
}

/// <summary>"5.10" after "5.9": engine versions compare as numbers, not as text.</summary>
public sealed class VersionOrder : IComparer<string>
{
    public static readonly VersionOrder Instance = new();

    public int Compare(string? x, string? y)
    {
        var a = Parts(x);
        var b = Parts(y);
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var c = (i < a.Length ? a[i] : 0).CompareTo(i < b.Length ? b[i] : 0);
            if (c != 0) return c;
        }
        return string.CompareOrdinal(x, y);
    }

    private static int[] Parts(string? v) =>
        (v ?? "").Split('.').Select(s => int.TryParse(s, out var n) ? n : 0).ToArray();
}

/// <summary>How a project's EngineAssociation resolved.</summary>
public enum AssociationKind
{
    /// <summary>"5.8": a launcher version.</summary>
    Version,
    /// <summary>"{GUID}": a source build registered in HKCU\...\Unreal Engine\Builds.</summary>
    SourceBuild,
    /// <summary>Empty: the engine is the folder the project sits in (a project inside an engine tree).</summary>
    EngineFolder,
}

/// <summary>
/// A project's engine. <see cref="Engine"/> is null when the association names an engine that is not on this
/// machine; <see cref="Version"/> is then still known for a version association ("5.7"), and unknown for a GUID.
/// </summary>
public sealed record ResolvedEngine(AssociationKind Kind, string Association, string? Version, EngineInstall? Engine)
{
    public bool IsInstalled => Engine is not null;

    /// <summary>"Unreal Engine 5.8.4", "Unreal Engine 5.7 (not installed)", "a source build that is not on this computer".</summary>
    public string Label => Engine is not null ? Engine.DisplayName
        : Kind == AssociationKind.Version && Version is not null ? $"Unreal Engine {Version} (not installed)"
        : Kind == AssociationKind.SourceBuild ? "a source build that is not on this computer"
        : "no Unreal Engine found for it";
}

/// <summary>
/// EngineAssociation in a .uproject (Unreal's FDesktopPlatformBase::GetEngineIdentifierForProject): a version
/// for a launcher install ("5.8"), the registry name of a source build ("{8C2D...}"), or empty for a project that
/// lives inside an engine folder. A GUID is never a version: before 0.5.0-beta.3 the CLI took it for one and
/// found no build.
/// </summary>
public static class EngineAssociations
{
    private static readonly Regex VersionPattern = new(@"^\d+\.\d+(\.\d+)?$", RegexOptions.Compiled);

    public static bool IsVersion(string? association) => association is not null && VersionPattern.IsMatch(association.Trim());

    public static bool IsGuid(string? association) =>
        association is not null && Guid.TryParse(association.Trim().Trim('{', '}'), out _);

    public static bool SameGuid(string? a, string? b) =>
        a is not null && b is not null
        && Guid.TryParse(a.Trim().Trim('{', '}'), out var x) && Guid.TryParse(b.Trim().Trim('{', '}'), out var y) && x == y;

    /// <param name="association">The .uproject's EngineAssociation.</param>
    /// <param name="uprojectPath">The project file, for an empty association.</param>
    /// <param name="engines">The engines found (<see cref="EngineLocator.Find"/>).</param>
    /// <param name="registry">Registered source builds by name (<see cref="EngineLocator.RegisteredBuilds"/>), or null for none.</param>
    /// <param name="fromPath">Reads an engine folder; <see cref="EngineLocator.FromPath"/> unless a test gives another.</param>
    public static ResolvedEngine Resolve(string? association, string uprojectPath, IReadOnlyList<EngineInstall> engines,
        IReadOnlyDictionary<string, string>? registry = null, Func<string, EngineInstall?>? fromPath = null)
    {
        fromPath ??= EngineLocator.FromPath;
        var a = (association ?? "").Trim();

        if (a.Length == 0)
        {
            // A project inside an engine tree: the nearest folder above it with Engine/Build/Build.version.
            var dir = Path.GetDirectoryName(Path.GetFullPath(uprojectPath));
            for (var i = 0; dir is not null && i < 6; i++, dir = Path.GetDirectoryName(dir))
            {
                var known = engines.FirstOrDefault(e => string.Equals(e.Path.TrimEnd('\\', '/'), dir.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase));
                if ((known ?? fromPath(dir)) is { } found) return new ResolvedEngine(AssociationKind.EngineFolder, a, found.Version, found);
            }
            return new ResolvedEngine(AssociationKind.EngineFolder, a, null, null);
        }

        if (IsVersion(a))
        {
            var mm = string.Join('.', a.Split('.').Take(2));
            var engine = engines.Where(e => e.Version == mm).OrderByDescending(e => e.IsLauncherBuild).FirstOrDefault();
            return new ResolvedEngine(AssociationKind.Version, a, mm, engine);
        }

        // A GUID, or any other name a source build was registered under.
        var byId = engines.FirstOrDefault(e => e.RegistryId is not null
                                               && (SameGuid(e.RegistryId, a) || string.Equals(e.RegistryId, a, StringComparison.OrdinalIgnoreCase)));
        if (byId is not null) return new ResolvedEngine(AssociationKind.SourceBuild, a, byId.Version, byId);
        if (registry is not null)
        {
            var hit = registry.FirstOrDefault(kv => SameGuid(kv.Key, a) || string.Equals(kv.Key, a, StringComparison.OrdinalIgnoreCase));
            if (hit.Value is { } path && fromPath(path) is { } e)
                return new ResolvedEngine(AssociationKind.SourceBuild, a, e.Version, e with { IsLauncherBuild = false, RegistryId = hit.Key });
        }
        return new ResolvedEngine(AssociationKind.SourceBuild, a, null, null);
    }
}
