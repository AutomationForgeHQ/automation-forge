using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Forge.Core.Projects;

/// <summary>Where a project was found. A project found several ways carries each.</summary>
[Flags]
public enum ProjectSource
{
    None = 0,
    /// <summary>Unreal's own list of recent projects (EditorSettings.ini, RecentlyOpenedProjectFiles), any version.</summary>
    Recent = 1,
    /// <summary>One level inside a folder Unreal or the Epic launcher created projects in (CreatedProjectPaths, Documents\Unreal Projects).</summary>
    CreatedFolder = 2,
    /// <summary>A folder the person added in Settings, scanned to a bounded depth.</summary>
    UserFolder = 4,
    /// <summary>Kovati Hub or the CLI installed into it (installed.json).</summary>
    Receipt = 8,
    /// <summary>Chosen in the Hub before ("Choose a project…").</summary>
    Remembered = 16,
}

public sealed record FoundProject(string Path, ProjectSource Sources, DateTime? LastOpened)
{
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
    public string Folder => System.IO.Path.GetDirectoryName(Path)!;
}

/// <summary>What one source gave: for `forge projects`, the Details log, and the tests.</summary>
public sealed record SourceReport(string Source, string Where, int Found, string? Note = null);

public sealed record ProjectDiscovery(IReadOnlyList<FoundProject> Projects, IReadOnlyList<SourceReport> Sources, IReadOnlyList<FolderScan> Scans);

/// <summary>Everything discovery reads. The defaults are this machine; a test points every folder somewhere else.</summary>
public sealed record ProjectDiscoveryInputs
{
    /// <summary>%LOCALAPPDATA%: UnrealEngine\&lt;version&gt;\Saved\Config\WindowsEditor\EditorSettings.ini and EpicGamesLauncher\Saved\Config.</summary>
    public string? LocalAppData { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    /// <summary>Documents, for the launcher's default "Unreal Projects" folder.</summary>
    public string? Documents { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    /// <summary>Folders the person added in Settings ("Project folders").</summary>
    public IReadOnlyList<string> UserFolders { get; init; } = [];
    /// <summary>Projects the Hub installed into: their .uproject files, or the Plugins\AutomationForge roots of the receipts.</summary>
    public IReadOnlyList<string> ReceiptRoots { get; init; } = [];
    /// <summary>Projects chosen in the Hub before.</summary>
    public IReadOnlyList<string> Remembered { get; init; } = [];
    public ScanLimits Limits { get; init; } = ScanLimits.Default;
}

/// <summary>
/// How far a folder is looked into. A folder the person adds may be a whole drive: the scan stops at the depth,
/// at the number of folders, or at the time, whichever comes first, and says which.
/// </summary>
public sealed record ScanLimits(int MaxDepth, int MaxDirectories, TimeSpan Budget)
{
    /// <summary>Two levels (the folder, its folders and theirs), 4,000 folders, 4 seconds a folder.</summary>
    public static readonly ScanLimits Default = new(2, 4000, TimeSpan.FromSeconds(4));

    /// <summary>CreatedProjectPaths and Documents\Unreal Projects: one level, as Unreal itself makes projects there.</summary>
    public static readonly ScanLimits OneLevel = new(1, 2000, TimeSpan.FromSeconds(2));
}

public sealed record FolderScan(string Folder, IReadOnlyList<string> Found, int DirectoriesVisited, bool HitDirectoryCap, bool TimedOut, bool Missing)
{
    public bool Complete => !HitDirectoryCap && !TimedOut && !Missing;

    /// <summary>"3 projects", "3 projects · stopped after 4,000 folders", "Not there".</summary>
    public string Line => Missing ? "This folder is not there."
        : $"{(Found.Count == 1 ? "1 project" : $"{Found.Count} projects")}"
          + (HitDirectoryCap ? $" · stopped after {DirectoriesVisited:N0} folders, add a folder further down" : "")
          + (TimedOut ? " · stopped after a few seconds, add a folder further down" : "");
}

/// <summary>
/// Finds Unreal projects without searching the disk: Unreal's and the Epic launcher's own records first, then the
/// folders the person chose (bounded), then the projects the Hub installed into before. Merged by path, a file
/// that is not there drops out quietly, and Unreal's own throwaway projects (under Saved\ or Intermediate\, a
/// BuildPlugin HostProject) are left out.
///
/// Confirmed on the development machine (20 November 2026): the editor's recents and CreatedProjectPaths in
/// %LOCALAPPDATA%\UnrealEngine\5.8\Saved\Config\WindowsEditor\EditorSettings.ini, and the launcher's own
/// CreatedProjectPaths in %LOCALAPPDATA%\EpicGamesLauncher\Saved\Config\WindowsEditor\GameUserSettings.ini
/// ([Launcher]). %PROGRAMDATA%\Epic holds engines only (LauncherInstalled.dat), no projects.
/// </summary>
public static class ProjectLocator
{
    public static ProjectDiscovery Discover(ProjectDiscoveryInputs inputs, CancellationToken ct = default)
    {
        var found = new Dictionary<string, (string path, ProjectSource sources, DateTime? opened)>(StringComparer.OrdinalIgnoreCase);
        var reports = new List<SourceReport>();
        var scans = new List<FolderScan>();

        void Add(string path, ProjectSource source, DateTime? opened = null)
        {
            string full;
            try { full = System.IO.Path.GetFullPath(path.Replace('/', System.IO.Path.DirectorySeparatorChar)); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return; }
            if (IsThrowaway(full) || !File.Exists(full)) return;
            var key = Normalize(full);
            if (found.TryGetValue(key, out var have))
                found[key] = (have.path, have.sources | source, Max(have.opened, opened));
            else
                found[key] = (full, source, opened);
        }

        // 1. Unreal's records, every version that has them.
        var createdFolders = new List<string>();
        if (inputs.LocalAppData is { Length: > 0 } la)
        {
            foreach (var (version, ini) in EditorSettingsFiles(la))
            {
                var text = TryRead(ini);
                if (text is null) continue;
                var section = Ini.Section(text, "/Script/UnrealEd.EditorSettings");
                var recents = section.Where(kv => kv.Key == "RecentlyOpenedProjectFiles").Select(kv => ParseRecent(kv.Value)).OfType<(string, DateTime?)>().ToList();
                var before = found.Count;
                foreach (var (p, t) in recents) Add(p, ProjectSource.Recent, t);
                var created = section.Where(kv => kv.Key == "CreatedProjectPaths").Select(kv => kv.Value.Trim().Trim('"')).Where(v => v.Length > 0).ToList();
                createdFolders.AddRange(created);
                reports.Add(new SourceReport($"Unreal {version} recent projects", ini, recents.Count,
                    $"{found.Count - before} on disk" + (created.Count > 0 ? $", {created.Count} project folder(s)" : "")));
            }

            foreach (var ini in LauncherSettingsFiles(la))
            {
                var text = TryRead(ini);
                if (text is null) continue;
                var created = Ini.Section(text, "Launcher").Where(kv => kv.Key == "CreatedProjectPaths")
                    .Select(kv => kv.Value.Trim().Trim('"')).Where(v => v.Length > 0).ToList();
                createdFolders.AddRange(created);
                reports.Add(new SourceReport("Epic launcher project folders", ini, created.Count));
            }
        }
        if (inputs.Documents is { Length: > 0 } docs) createdFolders.Add(System.IO.Path.Combine(docs, "Unreal Projects"));

        // 2. The folders projects are created in, one level deep, once each however they are spelled.
        foreach (var folder in createdFolders.Select(NormalizeFolder).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(folder)) { reports.Add(new SourceReport("Project folder (Unreal)", folder, 0, "not there")); continue; }
            var scan = ScanFolder(folder, ScanLimits.OneLevel, ct);
            foreach (var p in scan.Found) Add(p, ProjectSource.CreatedFolder);
            reports.Add(new SourceReport("Project folder (Unreal)", folder, scan.Found.Count));
        }

        // 3. The person's folders, bounded.
        foreach (var folder in inputs.UserFolders.Select(NormalizeFolder).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            var scan = ScanFolder(folder, inputs.Limits, ct);
            scans.Add(scan);
            foreach (var p in scan.Found) Add(p, ProjectSource.UserFolder);
            reports.Add(new SourceReport("Your project folder", folder, scan.Found.Count, scan.Complete ? null : scan.Line));
        }

        // 4. Where the Hub installed before, and what it was given by hand.
        var fromReceipts = 0;
        foreach (var root in inputs.ReceiptRoots.Distinct(StringComparer.OrdinalIgnoreCase))
            if (UProjectOfReceiptRoot(root) is { } u) { Add(u, ProjectSource.Receipt); fromReceipts++; }
        if (inputs.ReceiptRoots.Count > 0) reports.Add(new SourceReport("Installed into before", "installed.json", fromReceipts));
        foreach (var p in inputs.Remembered) Add(p, ProjectSource.Remembered);
        if (inputs.Remembered.Count > 0) reports.Add(new SourceReport("Chosen in Kovati Hub", "hub-state.json", inputs.Remembered.Count));

        var projects = found.Values
            .Select(v => new FoundProject(v.path, v.sources, v.opened))
            .OrderByDescending(p => p.LastOpened ?? DateTime.MinValue)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new ProjectDiscovery(projects, reports, scans);
    }

    /// <summary>Every %LOCALAPPDATA%\UnrealEngine\&lt;folder&gt; with an EditorSettings.ini: 4.27, 5.5, 5.8, … (and "Common", if it ever has one).</summary>
    public static IEnumerable<(string Version, string File)> EditorSettingsFiles(string localAppData)
    {
        var root = System.IO.Path.Combine(localAppData, "UnrealEngine");
        if (!Directory.Exists(root)) yield break;
        string[] dirs;
        try { dirs = Directory.GetDirectories(root); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }
        foreach (var dir in dirs.OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            foreach (var platform in new[] { "WindowsEditor", "Windows" })
            {
                var ini = System.IO.Path.Combine(dir, "Saved", "Config", platform, "EditorSettings.ini");
                if (File.Exists(ini)) { yield return (System.IO.Path.GetFileName(dir), ini); break; }
            }
    }

    public static IEnumerable<string> LauncherSettingsFiles(string localAppData)
    {
        foreach (var platform in new[] { "WindowsEditor", "Windows" })
        {
            var ini = System.IO.Path.Combine(localAppData, "EpicGamesLauncher", "Saved", "Config", platform, "GameUserSettings.ini");
            if (File.Exists(ini)) yield return ini;
        }
    }

    private static readonly Regex RecentName = new("ProjectName=\"(?<p>[^\"]+)\"", RegexOptions.Compiled);
    private static readonly Regex RecentTime = new(@"LastOpenTime=(?<t>\d{4}\.\d{2}\.\d{2}-\d{2}\.\d{2}\.\d{2})", RegexOptions.Compiled);

    /// <summary>
    /// `(ProjectName="C:/x/x.uproject",LastOpenTime=2026.11.18-23.41.59)` (5.x), or a bare path (4.x). Null for
    /// anything else.
    /// </summary>
    public static (string Path, DateTime? Opened)? ParseRecent(string value)
    {
        var v = value.Trim();
        if (v.StartsWith('('))
        {
            if (RecentName.Match(v) is not { Success: true } m) return null;
            DateTime? t = RecentTime.Match(v) is { Success: true } tm
                          && DateTime.TryParseExact(tm.Groups["t"].Value, "yyyy.MM.dd-HH.mm.ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
                ? dt : null;
            return (m.Groups["p"].Value, t);
        }
        v = v.Trim('"');
        return v.EndsWith(".uproject", StringComparison.OrdinalIgnoreCase) ? (v, null) : null;
    }

    /// <summary>
    /// Look into a folder for .uproject files: at most <see cref="ScanLimits.MaxDepth"/> levels down, at most
    /// <see cref="ScanLimits.MaxDirectories"/> folders and <see cref="ScanLimits.Budget"/> of time. A folder that holds a
    /// project is not looked into further, and the folders Unreal fills (Content, Saved, Intermediate, Binaries,
    /// DerivedDataCache, Plugins, Source) and hidden ones are never entered.
    /// </summary>
    public static FolderScan ScanFolder(string folder, ScanLimits limits, CancellationToken ct = default)
    {
        if (!Directory.Exists(folder)) return new FolderScan(folder, [], 0, false, false, Missing: true);
        var found = new List<string>();
        var visited = 0;
        var clock = Stopwatch.StartNew();
        var queue = new Queue<(string dir, int depth)>();
        queue.Enqueue((folder, 0));
        bool capped = false, late = false;
        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            if (visited >= limits.MaxDirectories) { capped = true; break; }
            if (clock.Elapsed > limits.Budget) { late = true; break; }
            var (dir, depth) = queue.Dequeue();
            visited++;
            string[] here;
            try { here = Directory.GetFiles(dir, "*.uproject"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            if (here.Length > 0)
            {
                foreach (var f in here) if (!IsThrowaway(f)) found.Add(System.IO.Path.GetFullPath(f));
                continue;
            }
            if (depth >= limits.MaxDepth) continue;
            IEnumerable<string> subs;
            try { subs = Directory.GetDirectories(dir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var sub in subs.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
            {
                if (Skip(sub)) continue;
                queue.Enqueue((sub, depth + 1));
            }
        }
        return new FolderScan(folder, found, visited, capped, late, Missing: false);
    }

    private static readonly HashSet<string> Unentered = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content", "Saved", "Intermediate", "Binaries", "DerivedDataCache", "Plugins", "Source", "Build",
        "node_modules", "$RECYCLE.BIN", "System Volume Information", "Windows", "Program Files", "Program Files (x86)", "ProgramData",
    };

    private static bool Skip(string dir)
    {
        var name = System.IO.Path.GetFileName(dir);
        if (name.Length == 0 || name.StartsWith('.') || Unentered.Contains(name)) return true;
        try { return (File.GetAttributes(dir) & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) != 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    /// <summary>A project Unreal or BuildPlugin made for itself: under Saved\ or Intermediate\, or a HostProject.</summary>
    public static bool IsThrowaway(string path)
    {
        var p = path.Replace('/', '\\');
        return p.Contains(@"\Saved\", StringComparison.OrdinalIgnoreCase)
               || p.Contains(@"\Intermediate\", StringComparison.OrdinalIgnoreCase)
               || p.Contains(@"\HostProject\", StringComparison.OrdinalIgnoreCase)
               || System.IO.Path.GetFileNameWithoutExtension(p).Equals("HostProject", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A receipt's target root is &lt;project&gt;\Plugins\AutomationForge; a .uproject is taken as it is.</summary>
    public static string? UProjectOfReceiptRoot(string root)
    {
        try
        {
            if (root.EndsWith(".uproject", StringComparison.OrdinalIgnoreCase)) return File.Exists(root) ? root : null;
            var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(root.TrimEnd('\\', '/')));
            if (dir is null || !Directory.Exists(dir)) return null;
            return Directory.GetFiles(dir, "*.uproject").FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    private static string? NormalizeFolder(string folder)
    {
        try { return System.IO.Path.GetFullPath(folder.Trim().Trim('"').Replace('/', System.IO.Path.DirectorySeparatorChar)).TrimEnd('\\', '/'); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    private static string Normalize(string full) => full.Replace('/', '\\').TrimEnd('\\');

    private static DateTime? Max(DateTime? a, DateTime? b) => a is null ? b : b is null ? a : a > b ? a : b;

    private static string? TryRead(string file)
    {
        try { return File.ReadAllText(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}

/// <summary>The little of Unreal's .ini format discovery needs: one section's keys and values, repeated keys kept, '+' arrays read as keys.</summary>
public static class Ini
{
    public static IReadOnlyList<KeyValuePair<string, string>> Section(string text, string section)
    {
        var list = new List<KeyValuePair<string, string>>();
        var inside = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimEnd('\r');
            if (line.Length == 0 || line[0] is ';' or '#') continue;
            if (line[0] == '[')
            {
                inside = line.TrimEnd(']').TrimStart('[').Equals(section, StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inside) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            // "+Key=" and ".Key=" add to an array; "-Key=" and "!Key=" remove, so they are not this key.
            var key = line[..eq].Trim().TrimStart('+', '.');
            list.Add(new(key, line[(eq + 1)..]));
        }
        return list;
    }
}
