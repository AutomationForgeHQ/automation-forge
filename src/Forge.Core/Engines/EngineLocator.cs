using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Forge.Core.Engines;

/// <summary>An Unreal Engine installation the hub can install plugins into.</summary>
public sealed record EngineInstall(string Version, string FullVersion, string Path, bool IsLauncherBuild)
{
    /// <summary>Engine/Plugins/AutomationForge — the same convention Fab uses with Engine/Plugins/Marketplace.</summary>
    public string PluginRoot => System.IO.Path.Combine(Path, "Engine", "Plugins", Core.Paths.PluginFolder);

    public override string ToString() => $"UE {FullVersion} at {Path}" + (IsLauncherBuild ? "" : " (source build)");
}

/// <summary>Finds engines the way the Epic launcher records them, plus source builds from the registry.</summary>
public static class EngineLocator
{
    private static readonly Regex MajorMinor = new(@"^(\d+\.\d+)", RegexOptions.Compiled);

    public static IReadOnlyList<EngineInstall> Find()
    {
        var found = new List<EngineInstall>();
        found.AddRange(FromLauncherManifest());
        found.AddRange(FromRegistryBuilds());
        return found
            .GroupBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderByDescending(e => e.Version)
            .ToList();
    }

    /// <summary>Pick by "5.8", a full version, or a path.</summary>
    public static EngineInstall? Resolve(string? spec, IReadOnlyList<EngineInstall>? engines = null)
    {
        engines ??= Find();
        if (string.IsNullOrWhiteSpace(spec))
            return engines.FirstOrDefault();
        if (Directory.Exists(spec))
        {
            var full = Path.GetFullPath(spec).TrimEnd('\\', '/');
            return engines.FirstOrDefault(e => string.Equals(e.Path.TrimEnd('\\', '/'), full, StringComparison.OrdinalIgnoreCase))
                   ?? FromPath(full);
        }
        return engines.FirstOrDefault(e => e.Version == spec || e.FullVersion == spec);
    }

    private static IEnumerable<EngineInstall> FromLauncherManifest()
    {
        if (!OperatingSystem.IsWindows()) yield break;
        var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                                "Epic", "UnrealEngineLauncher", "LauncherInstalled.dat");
        if (!File.Exists(file)) yield break;

        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        if (!doc.RootElement.TryGetProperty("InstallationList", out var list)) yield break;
        foreach (var item in list.EnumerateArray())
        {
            var app = item.TryGetProperty("AppName", out var a) ? a.GetString() ?? "" : "";
            if (!app.StartsWith("UE_", StringComparison.Ordinal)) continue; // FabPlugin_5.8, QuixelBridge_5.8 share the folder
            var location = item.GetProperty("InstallLocation").GetString() ?? "";
            var appVersion = item.TryGetProperty("AppVersion", out var v) ? v.GetString() ?? "" : "";
            var full = appVersion.Split('-')[0];                  // 5.8.1-56057345+++UE5+Release-5.8-Windows → 5.8.1
            var mm = MajorMinor.Match(full) is { Success: true } m ? m.Groups[1].Value : app[3..];
            if (Directory.Exists(location))
                yield return new EngineInstall(mm, full, location, IsLauncherBuild: true);
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
            if (FromPath(path) is { } e) yield return e with { IsLauncherBuild = false };
        }
    }

    /// <summary>Read Engine/Build/Build.version from an engine folder.</summary>
    public static EngineInstall? FromPath(string path)
    {
        var versionFile = Path.Combine(path, "Engine", "Build", "Build.version");
        if (!File.Exists(versionFile)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(versionFile));
        var root = doc.RootElement;
        var major = root.GetProperty("MajorVersion").GetInt32();
        var minor = root.GetProperty("MinorVersion").GetInt32();
        var patch = root.TryGetProperty("PatchVersion", out var p) ? p.GetInt32() : 0;
        var isLauncher = root.TryGetProperty("IsPromotedBuild", out var promoted) && promoted.GetInt32() == 1;
        return new EngineInstall($"{major}.{minor}", $"{major}.{minor}.{patch}", path, isLauncher);
    }
}
