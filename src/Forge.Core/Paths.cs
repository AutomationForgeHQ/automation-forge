namespace Forge.Core;

/// <summary>Where the hub keeps its own state. Never inside an engine or a project.</summary>
public static class Paths
{
    /// <summary>%LOCALAPPDATA%\AutomationForge, or FORGE_DATA_DIR — a test against the emulators must not touch the real sign-in.</summary>
    public static string DataDir =>
        Environment.GetEnvironmentVariable("FORGE_DATA_DIR") is { Length: > 0 } d
            ? d
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutomationForge");

    public static string DownloadDir => Path.Combine(DataDir, "downloads");

    /// <summary>The folder our plugins are installed into, inside an engine or a project. Readable names, one place.</summary>
    public const string PluginFolder = "AutomationForge";
}
