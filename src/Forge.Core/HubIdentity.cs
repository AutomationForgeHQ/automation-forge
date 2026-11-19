namespace Forge.Core;

/// <summary>
/// Who this Hub is to Windows and to GitHub, in one place. Kovati Hub v2 ships as a beta that installs and runs beside
/// the Automation Forge Hub 0.4.x, so every name the two could share is its own here: the installer's AppId, the
/// program file, the instance mutex and its events, the Start-with-Windows value, the release tags and installer it
/// updates from, and the few files in the shared data folder that only this Hub writes.
///
/// What stays shared on purpose: the data folder (%LOCALAPPDATA%\AutomationForge) with its receipts, sign-in,
/// settings and caches, and the engines. Both Hubs install the same plugins into the same places and read the same
/// receipts; <see cref="Installs.InstallState"/> and <see cref="Settings"/> re-read the file before they write it so
/// neither overwrites the other.
///
/// When v2 replaces 0.4.x for everybody, this class is what changes.
/// </summary>
public static class HubIdentity
{
    public const bool IsBeta = true;

    /// <summary>The name in the Start menu, in Apps and features, on the tray icon and on its notifications.</summary>
    public const string DisplayName = "Kovati Hub (beta)";

    public const string BetaLine = "Beta. The previous Hub keeps working beside this one.";

    /// <summary>Must match AppGuid in installer/AutomationForge.iss. 0.4.x is {4F0B9C7E-2A6D-4B1F-9E3C-7D5A1F0C8B21}.</summary>
    public const string AppId = "{E13BC642-3BBA-4D78-88D9-8D3767887CAE}";

    /// <summary>The installed program file (tools/publish.ps1 renames the apphost; the assembly stays AutomationForgeHub).</summary>
    public const string ExeName = "KovatiHub.exe";

    /// <summary>Base of the single-instance mutex and its Show and Quit events. 0.4.x uses Local\AutomationForgeHub.</summary>
    public const string InstanceBase = @"Local\KovatiHub";

    /// <summary>The value under HKCU\...\Run. 0.4.x uses "AutomationForge".</summary>
    public const string AutostartValue = "KovatiHub";

    /// <summary>
    /// This Hub's releases are tagged kovati-hub-v&lt;semver&gt; in the automation-forge repository. A 0.4.x Hub reads
    /// the same list and skips them: its SemVer.TryParse refuses a tag that does not start with a digit or a v.
    /// </summary>
    public const string ReleaseTagPrefix = "kovati-hub-v";

    /// <summary>The installer asset a release must carry for this Hub to update from it.</summary>
    public const string InstallerAsset = "KovatiHub-Setup.exe";

    /// <summary>The ETag cache of the release list, apart from 0.4.x's hub-releases.json.</summary>
    public const string ReleasesCacheName = "kovati-hub-releases";

    /// <summary>What has been announced, apart from 0.4.x's notified.json (which it rewrites whole).</summary>
    public const string NotifiedFile = "kovati-hub-notified.json";

    /// <summary>The elevated helper's log, apart from 0.4.x's elevated.log.</summary>
    public const string ElevatedLog = "kovati-hub-elevated.log";

    /// <summary>Prefix of a downloaded installer in the download folder.</summary>
    public const string DownloadPrefix = "KovatiHub-Setup-";
}
