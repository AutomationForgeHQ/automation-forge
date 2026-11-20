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

    /// <summary>
    /// The signed licence the paid plugins read: %LOCALAPPDATA%\AutomationForge\licence.json. A stable contract
    /// (HUB_V2_NOTES.md, "The licence file"); under FORGE_DATA_DIR it is in that folder instead.
    /// </summary>
    public static string LicenceFile => Path.Combine(DataDir, Licence.LicenceStore.FileName);

    /// <summary>The folder our plugins are installed into, inside an engine or a project. Readable names, one place.</summary>
    public const string PluginFolder = "AutomationForge";
}
