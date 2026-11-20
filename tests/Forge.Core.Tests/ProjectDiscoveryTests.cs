using Xunit;
using Forge.Core.Engines;
using Forge.Core.Installs;
using Forge.Core.Projects;

namespace Forge.Core.Tests;

/// <summary>
/// Projects are found from Unreal's and the launcher's own records and from the person's folders, merged by path,
/// bounded, and never by searching the disk. Every folder here is a throwaway one under the temp folder.
/// </summary>
public sealed class ProjectDiscoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kovati-discovery-tests", Guid.NewGuid().ToString("N"));
    private string LocalAppData => Path.Combine(_root, "LocalAppData");
    private string Documents => Path.Combine(_root, "Documents");

    public ProjectDiscoveryTests()
    {
        Directory.CreateDirectory(LocalAppData);
        Directory.CreateDirectory(Documents);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Project(string folder, string? name = null, string association = "5.8")
    {
        name ??= Path.GetFileName(folder);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name + ".uproject");
        File.WriteAllText(path, $"{{\r\n\t\"FileVersion\": 3,\r\n\t\"EngineAssociation\": \"{association}\"\r\n}}");
        return path;
    }

    private void EditorSettings(string version, string body, string platform = "WindowsEditor")
    {
        var dir = Path.Combine(LocalAppData, "UnrealEngine", version, "Saved", "Config", platform);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "EditorSettings.ini"), body);
    }

    private ProjectDiscoveryInputs Inputs(params string[] userFolders) => new()
    {
        LocalAppData = LocalAppData, Documents = Documents, UserFolders = userFolders,
    };

    private static string Fwd(string p) => p.Replace('\\', '/');

    [Fact]
    public void Recent_projects_are_read_from_every_engine_version_and_both_formats()
    {
        var a = Project(Path.Combine(_root, "work", "Alpha"));
        var b = Project(Path.Combine(_root, "work", "Beta"));
        var old = Project(Path.Combine(_root, "old", "Legacy"));
        EditorSettings("5.8", $"""
            [/Script/UnrealEd.EditorPerformanceSettings]
            RecentlyOpenedProjectFiles=(ProjectName="{Fwd(b)}",LastOpenTime=2026.01.01-00.00.00)
            [/Script/UnrealEd.EditorSettings]
            RecentlyOpenedProjectFiles=(ProjectName="{Fwd(a)}",LastOpenTime=2026.11.18-23.41.59)
            RecentlyOpenedProjectFiles=(ProjectName="{Fwd(b)}",LastOpenTime=2026.11.15-23.25.46)
            """);
        EditorSettings("5.5", $"""
            [/Script/UnrealEd.EditorSettings]
            +RecentlyOpenedProjectFiles=(ProjectName="{Fwd(b)}",LastOpenTime=2026.11.19-08.00.00)
            """);
        // Unreal 4 kept a bare path, and its folder may be Saved\Config\Windows.
        EditorSettings("4.27", $"""
            [/Script/UnrealEd.EditorSettings]
            RecentlyOpenedProjectFiles={old}
            """, platform: "Windows");

        var d = ProjectLocator.Discover(Inputs());

        Assert.Equal(["Beta", "Alpha", "Legacy"], d.Projects.Select(p => p.Name));       // newest opened first
        Assert.Equal(new DateTime(2026, 11, 19, 8, 0, 0), d.Projects[0].LastOpened);      // the newest of its two entries
        Assert.All(d.Projects, p => Assert.True(p.Sources.HasFlag(ProjectSource.Recent)));
        Assert.Equal(3, d.Sources.Count(s => s.Source.Contains("recent projects")));
    }

    [Fact]
    public void Missing_projects_and_Unreals_own_throwaway_projects_drop_out_quietly()
    {
        var real = Project(Path.Combine(_root, "Real"));
        var host = Project(Path.Combine(_root, "Real", "Saved", "TerrainForge", "PackageCore", "HostProject"));
        var gone = Path.Combine(_root, "Gone", "Gone.uproject");
        EditorSettings("5.8", $"""
            [/Script/UnrealEd.EditorSettings]
            RecentlyOpenedProjectFiles=(ProjectName="{Fwd(real)}",LastOpenTime=2026.11.18-23.41.59)
            RecentlyOpenedProjectFiles=(ProjectName="{Fwd(host)}",LastOpenTime=2026.11.18-23.00.00)
            RecentlyOpenedProjectFiles=(ProjectName="{Fwd(gone)}",LastOpenTime=2026.11.17-23.00.00)
            RecentlyOpenedProjectFiles=garbage
            """);

        var d = ProjectLocator.Discover(Inputs(Path.Combine(_root, "NotAFolder")) with { Remembered = [gone] });

        Assert.Equal(["Real"], d.Projects.Select(p => p.Name));
        Assert.True(d.Scans.Single().Missing);
        Assert.Equal("This folder is not there.", d.Scans.Single().Line);
    }

    [Fact]
    public void Created_project_folders_are_looked_into_one_level_and_named_once_however_they_are_spelled()
    {
        var folder = Path.Combine(_root, "Projects");
        Project(Path.Combine(folder, "One"));
        Project(Path.Combine(folder, "Two"));
        Project(Path.Combine(folder, "Deep", "Three"));                    // two levels: not a created-folder project
        Project(Path.Combine(Documents, "Unreal Projects", "FromLauncher")); // the launcher's default folder
        EditorSettings("5.8", $"""
            [/Script/UnrealEd.EditorSettings]
            CreatedProjectPaths={folder}
            CreatedProjectPaths={Fwd(folder)}
            """);
        var launcher = Path.Combine(LocalAppData, "EpicGamesLauncher", "Saved", "Config", "WindowsEditor");
        Directory.CreateDirectory(launcher);
        File.WriteAllText(Path.Combine(launcher, "GameUserSettings.ini"), $"[Launcher]\r\nCreatedProjectPaths={Fwd(folder)}\r\n[Portal.OSS]\r\nCreatedProjectPaths=C:/NotThisOne\r\n");

        var d = ProjectLocator.Discover(Inputs());

        Assert.Equal(["FromLauncher", "One", "Two"], d.Projects.Select(p => p.Name));
        Assert.All(d.Projects, p => Assert.Equal(ProjectSource.CreatedFolder, p.Sources));
        Assert.Single(d.Sources, s => s.Source == "Project folder (Unreal)" && s.Where == folder);   // three spellings, one scan
        Assert.Single(d.Sources, s => s.Source == "Epic launcher project folders" && s.Found == 1);
    }

    [Fact]
    public void One_project_found_four_ways_is_one_row_with_all_four()
    {
        var folder = Path.Combine(_root, "Mine");
        var p = Project(Path.Combine(folder, "Game"));
        EditorSettings("5.8", $"""
            [/Script/UnrealEd.EditorSettings]
            RecentlyOpenedProjectFiles=(ProjectName="{Fwd(p).ToUpperInvariant()}",LastOpenTime=2026.11.18-23.41.59)
            CreatedProjectPaths={folder}
            """);
        var receiptRoot = Path.Combine(folder, "Game", "Plugins", "AutomationForge");

        var d = ProjectLocator.Discover(Inputs(folder) with { ReceiptRoots = [receiptRoot], Remembered = [p] });

        var only = Assert.Single(d.Projects);
        Assert.Equal(ProjectSource.Recent | ProjectSource.CreatedFolder | ProjectSource.UserFolder | ProjectSource.Receipt | ProjectSource.Remembered, only.Sources);
    }

    [Fact]
    public void A_users_folder_is_scanned_to_its_depth_and_not_into_a_projects_own_folders()
    {
        var folder = Path.Combine(_root, "Drive");
        Project(Path.Combine(folder, "A"));                                 // level 1
        Project(Path.Combine(folder, "Clients", "B"));                      // level 2
        Project(Path.Combine(folder, "Clients", "Deeper", "C"));            // level 3: beyond the default
        Project(Path.Combine(folder, "A", "Plugins", "Inner"));             // inside a project: never looked at
        Project(Path.Combine(folder, ".hidden", "D"));                      // hidden folder: skipped

        var scan = ProjectLocator.ScanFolder(folder, ScanLimits.Default);
        Assert.Equal(["A", "B"], scan.Found.Select(Path.GetFileNameWithoutExtension).Order());
        Assert.True(scan.Complete);

        var deeper = ProjectLocator.ScanFolder(folder, ScanLimits.Default with { MaxDepth = 3 });
        Assert.Contains(deeper.Found, f => f.EndsWith("C.uproject"));
    }

    [Fact]
    public void A_huge_folder_stops_at_the_folder_cap_and_at_the_time_budget_and_says_so()
    {
        var folder = Path.Combine(_root, "Huge");
        for (var i = 0; i < 40; i++) Directory.CreateDirectory(Path.Combine(folder, $"dir{i:00}", "sub"));
        Project(Path.Combine(folder, "zz", "Last"));

        var capped = ProjectLocator.ScanFolder(folder, new ScanLimits(2, 10, TimeSpan.FromSeconds(30)));
        Assert.True(capped.HitDirectoryCap);
        Assert.Equal(10, capped.DirectoriesVisited);
        Assert.Contains("stopped after 10 folders", capped.Line);

        var late = ProjectLocator.ScanFolder(folder, new ScanLimits(2, 10_000, TimeSpan.Zero));
        Assert.True(late.TimedOut);
        Assert.False(late.Complete);

        var whole = ProjectLocator.ScanFolder(folder, new ScanLimits(2, 10_000, TimeSpan.FromSeconds(30)));
        Assert.True(whole.Complete);
        Assert.Single(whole.Found);
    }

    [Fact]
    public void The_ini_reader_keeps_repeated_keys_and_ignores_removals()
    {
        var section = Ini.Section("[A]\r\nK=1\r\n+K=2\r\n-K=3\r\n; K=4\r\n[B]\r\nK=5\r\n", "A");
        Assert.Equal(["1", "2"], section.Where(kv => kv.Key == "K").Select(kv => kv.Value));
    }

    [Theory]
    [InlineData("(ProjectName=\"C:/a/b.uproject\",LastOpenTime=2026.11.18-23.41.59)", "C:/a/b.uproject", true)]
    [InlineData("(ProjectName=\"C:/a/b.uproject\")", "C:/a/b.uproject", false)]
    [InlineData("C:/old/Game.uproject", "C:/old/Game.uproject", false)]
    public void A_recent_entry_is_parsed(string value, string path, bool hasTime)
    {
        var r = ProjectLocator.ParseRecent(value);
        Assert.NotNull(r);
        Assert.Equal(path, r.Value.Path);
        Assert.Equal(hasTime, r.Value.Opened is not null);
    }

    [Fact]
    public void The_cache_round_trips_and_forgets_projects_that_are_gone()
    {
        var a = Project(Path.Combine(_root, "Cached", "A"));
        var b = Project(Path.Combine(_root, "Cached", "B"));
        var d = new ProjectDiscovery([new FoundProject(a, ProjectSource.Recent | ProjectSource.Receipt, new DateTime(2026, 11, 18, 23, 41, 59)), new FoundProject(b, ProjectSource.UserFolder, null)], [], []);
        var cache = ProjectService.ToCache(d, DateTime.Now);
        File.Delete(b);
        var back = ProjectService.FromCache(cache);
        var only = Assert.Single(back);
        Assert.Equal(ProjectSource.Recent | ProjectSource.Receipt, only.Sources);
        Assert.Equal(new DateTime(2026, 11, 18, 23, 41, 59), only.LastOpened);
    }

    [Fact]
    public void The_Hub_state_keeps_the_project_folders_and_fields_it_does_not_know()
    {
        var file = Path.Combine(_root, "hub-state.json");
        File.WriteAllText(file, """{ "schemaVersion": 1, "projectFolders": ["D:\\Projects"], "fromANewerHub": { "x": 1 } }""");
        var s = HubState.Load(file);
        Assert.Equal(["D:\\Projects"], s.ProjectFolders);
        s.ProjectFolders.Add("E:\\More");
        s.Save(file);
        var text = File.ReadAllText(file);
        Assert.Contains("fromANewerHub", text);
        Assert.Equal(2, HubState.Load(file).ProjectFolders.Count);
    }

    [Fact]
    public void A_receipt_root_leads_to_its_project()
    {
        var p = Project(Path.Combine(_root, "Receipt", "Game"));
        Assert.Equal(p, ProjectLocator.UProjectOfReceiptRoot(Path.Combine(_root, "Receipt", "Game", "Plugins", "AutomationForge")));
        Assert.Null(ProjectLocator.UProjectOfReceiptRoot(Path.Combine(_root, "Nowhere", "Plugins", "AutomationForge")));
    }

    // ── the editor check ─────────────────────────────────────────────────

    [Fact]
    public void An_editor_with_the_project_on_its_command_line_has_it_open()
    {
        var p = Project(Path.Combine(_root, "Open", "Game"));
        EditorProcess Editor(string? cmd) => new(1, @"C:\Program Files\Epic Games\UE_5.8\Engine\Binaries\Win64\UnrealEditor.exe", cmd);

        Assert.Equal(EditorOnProject.No, EditorProcesses.Check(p, []));
        Assert.Equal(EditorOnProject.Yes, EditorProcesses.Check(p, [Editor($"\"UnrealEditor.exe\" \"{Fwd(p)}\" -skipcompile")]));
        Assert.Equal(EditorOnProject.Yes, EditorProcesses.Check(p, [Editor($"UnrealEditor.exe -project={p.ToUpperInvariant()}")]));
        Assert.Equal(EditorOnProject.No, EditorProcesses.Check(p, [Editor("\"UnrealEditor.exe\" \"C:/Other/Other.uproject\"")]));
        Assert.Equal(EditorOnProject.Unknown, EditorProcesses.Check(p, [Editor(null)]));
        Assert.Equal(EditorOnProject.Yes, EditorProcesses.Check(p, [Editor(null), Editor($"x \"{p}\"")]));
        Assert.True(EditorProcesses.RunsFrom(@"C:\Program Files\Epic Games\UE_5.8\", [Editor("")]));
    }

    // ── a project's tools ────────────────────────────────────────────────

    [Fact]
    public void A_projects_tools_are_read_from_its_folders_its_engine_and_its_uproject()
    {
        var engineDir = Path.Combine(_root, "UE_5.8");
        Directory.CreateDirectory(Path.Combine(engineDir, "Engine", "Build"));
        File.WriteAllText(Path.Combine(engineDir, "Engine", "Build", "Build.version"), """{ "MajorVersion": 5, "MinorVersion": 8, "PatchVersion": 4, "IsPromotedBuild": 1 }""");
        var engine = EngineLocator.FromPath(engineDir)!;
        void Plugin(string dir, string id, string version, bool? byDefault = null)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, id + ".uplugin"),
                $$"""{ "VersionName": "{{version}}"{{(byDefault is { } b ? $", \"EnabledByDefault\": {b.ToString().ToLowerInvariant()}" : "")}} }""");
        }
        Plugin(Path.Combine(engine.PluginRoot, "AutomationForge"), "AutomationForge", "0.1.1");
        Plugin(Path.Combine(engine.PluginRoot, "MotionForge"), "MotionForge", "0.4.1");
        Plugin(Path.Combine(engine.PluginRoot, "SpeechForge"), "SpeechForge", "0.2.3");

        var folder = Path.Combine(_root, "Game");
        var uproject = Project(folder);
        File.WriteAllText(uproject, """{ "FileVersion": 3, "EngineAssociation": "5.8", "Modules": [ { "Name": "Game" } ], "Plugins": [ { "Name": "MontageForge", "Enabled": false }, { "Name": "SpeechForge", "Enabled": true }, { "Name": "Gone", "Enabled": true }, { "Name": "NotKovati", "Enabled": true } ] }""");
        Plugin(Path.Combine(folder, "Plugins", "AutomationForge", "MotionForge"), "MotionForge", "0.4.2");     // the Hub's, over the engine's
        Plugin(Path.Combine(folder, "Plugins", "AutomationForge", "MontageForge"), "MontageForge", "0.1.3");
        Plugin(Path.Combine(folder, "Plugins", "Forge", "SurfaceForge"), "SurfaceForge", "0.1.2", byDefault: false); // its own copy
        Plugin(Path.Combine(folder, "Plugins", "Forge", "SurfaceForge", "Source", "Inner"), "SkinForge", "9.9.9"); // inside a plugin: not read

        var ids = new HashSet<string>(["AutomationForge", "MotionForge", "MontageForge", "SpeechForge", "SurfaceForge", "SkinForge", "Gone"], StringComparer.OrdinalIgnoreCase);
        var deps = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase) { ["MotionForge"] = ["AutomationForge"] };
        var receipts = new[] { new InstalledPlugin { Plugin = "MotionForge", TargetKind = "project", TargetRoot = Path.Combine(folder, "Plugins", "AutomationForge") } };

        var r = ProjectInspector.Inspect(new FoundProject(uproject, ProjectSource.Recent, null), [engine], ids, deps, receipts);

        Assert.True(r.IsCodeProject);
        Assert.Same(engine, r.Engine.Engine);
        var by = r.Plugins.ToDictionary(p => p.Id);
        Assert.Equal(["AutomationForge", "Gone", "MontageForge", "MotionForge", "SpeechForge", "SurfaceForge"], by.Keys.Order());

        Assert.True(by["MotionForge"].Overrides);
        Assert.Equal("0.4.2", by["MotionForge"].Loaded!.Version);
        Assert.Equal((true, EnabledReason.ProjectDefault), (by["MotionForge"].Enabled, by["MotionForge"].Reason));
        Assert.Equal((false, EnabledReason.Explicit), (by["MontageForge"].Enabled, by["MontageForge"].Reason));
        Assert.Equal((true, EnabledReason.Explicit), (by["SpeechForge"].Enabled, by["SpeechForge"].Reason));
        Assert.True(by["SpeechForge"].InEngine && !by["SpeechForge"].InProject);
        Assert.Equal((false, EnabledReason.ProjectDefault), (by["SurfaceForge"].Enabled, by["SurfaceForge"].Reason));
        Assert.False(by["SurfaceForge"].ProjectCopy!.ManagedByHub);
        Assert.Equal((true, EnabledReason.NeededByEnabled, "MotionForge"), (by["AutomationForge"].Enabled, by["AutomationForge"].Reason, by["AutomationForge"].NeededBy));
        Assert.Equal(EnabledReason.Missing, by["Gone"].Reason);

        // What the project has from elsewhere: the engine's copies it has no copy of, and its own copies.
        var elsewhere = ProjectService.ProvidedElsewhere(r);
        Assert.Equal(["AutomationForge", "SpeechForge", "SurfaceForge"], elsewhere.Keys.Order());
    }
}
