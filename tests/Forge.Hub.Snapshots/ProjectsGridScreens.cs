using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Forge.Hub.ViewModels;
using Forge.Hub.ViewModels.Pages;

namespace Forge.Hub.Snapshots;

/// <summary>
/// 0.5.0-beta.4, the Projects page as a grid: a fake machine with 32 projects (Unreal Engine 5.8 with builds, 5.7
/// installed without any, 5.6 not installed), pictures for some (the project's own, Unreal's auto screenshot, one
/// broken file), Kovati tools in some, one update, one read-only .uproject, one the install target. Nothing of this
/// machine's engines, projects or sign-in is read or written.
///
///     dotnet run --project tests/Forge.Hub.Snapshots -- &lt;out dir&gt; projects
/// </summary>
internal static partial class Screens
{
    public static void RunProjects(string outDir, string[] only)
    {
        _out = outDir;
        _only = only;
        _scratch = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kovati-hub-snapshots-projects");
        if (Directory.Exists(_scratch))
        {
            // The read-only .uproject of the last run would stop the delete.
            foreach (var f in Directory.EnumerateFiles(_scratch, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_scratch, true);
        }
        Directory.CreateDirectory(_scratch);
        var repo = FindRepo();
        Environment.SetEnvironmentVariable("FORGE_MANIFEST_URL", System.IO.Path.Combine(repo, "manifest.json"));
        Environment.SetEnvironmentVariable("FORGE_RELEASES_API", "off");
        Environment.SetEnvironmentVariable("FORGE_CATALOGUE_URL", "off");
        Environment.SetEnvironmentVariable("FORGE_SITE_URL", "http://127.0.0.1:9");
        Environment.SetEnvironmentVariable("FORGE_API_URL", "http://127.0.0.1:9/api");
        Environment.SetEnvironmentVariable("FORGE_ACCOUNT_URL", "http://127.0.0.1:9");
        Environment.SetEnvironmentVariable("FORGE_UNREAL_RECORDS", "off");
        ProjectsGrid();
        Console.WriteLine($"done: {outDir}");
    }

    /// <summary>name, engine, opened days ago (null: never recorded), picture kind, tools as id@version (Plugins\AutomationForge), extras.</summary>
    private sealed record FakeProject(string Name, string Engine, int? DaysAgo, string? Picture = null, string[]? Tools = null,
        bool Code = false, bool Git = false, bool ReadOnly = false, string[]? Entries = null, string? OwnCopy = null);

    private static readonly FakeProject[] Fleet =
    [
        new("Colony_NP24", "5.8", 0, "own", ["AutomationForgeHub@0.2.5", "AutomationForge@0.1.1", "MotionForge@0.4.1", "MotionForgeKimodo@0.5.1", "MeshForge@0.3.2", "SpeechForge@0.2.3"], Code: true, Git: true),
        new("MyGame", "5.8", 1, "own", ["AutomationForge@0.1.1", "MotionForge@0.4.0"]),
        new("Studio", "5.8", 2, null, ["MontageForge@0.1.3"], Code: true, Git: true),
        new("CinematicTests", "5.8", 3, "own", ["FaceForge@0.2.3", "SpeechForge@0.2.3"]),
        new("ShooterJam", "5.8", 4, "auto", OwnCopy: "MeshWeightRemap@0.1.3"),
        new("VehicleSandbox", "5.8", 5, null, Entries: ["TerrainForge"]),
        new("Lyra_Starter", "5.8", 6, "own", ["MotionForge@0.4.1"], Code: true, ReadOnly: true),
        new("ArchViz_Penthouse", "5.8", 8, "own"),
        new("CitySample", "5.8", 9, "broken"),
        new("MetaHumanLab", "5.8", 11, null),
        new("StackOBot", "5.8", 12, "auto"),
        new("RPG_Village", "5.8", 15, null),
        new("DesertRacer", "5.8", 18, "own"),
        new("SpaceHangar", "5.8", 20, null),
        new("UI_Playground", "5.8", 22, null),
        new("NaniteTests", "5.8", 25, null),
        new("PCG_Biomes", "5.8", 27, "own"),
        new("MotionMatchingDemo", "5.8", 30, null),
        new("GASPrototype", "5.8", null, null, Code: true),
        new("PuzzleBox", "5.8", null, null),
        new("NightMarket", "5.8", null, null),
        new("A_Very_Long_Project_Name_For_The_Client_Showreel_2026", "5.8", 40, null),
        new("WaterWorld", "5.7", 45, "own"),
        new("TopDownTD", "5.7", 50, null),
        new("HorrorCorridor", "5.7", 60, null),
        new("AudioTests", "5.7", 70, null),
        new("VR_Training", "5.7", null, null),
        new("PrototypeFPS", "5.7", 90, null),
        new("Archive", "5.6", 200, null),
        new("OldFarm", "5.6", 260, null),
        new("ClientDemo", "5.8", 33, null),
        new("ClientPitch", "5.8", 35, null),
    ];

    private static void ProjectsGrid()
    {
        var root = System.IO.Path.Combine(_scratch, "machine");
        var records = System.IO.Path.Combine(root, "records");
        var projects = System.IO.Path.Combine(root, "Projects");
        var clients = System.IO.Path.Combine(root, "Clients");
        var now = new DateTime(2026, 11, 20, 9, 0, 0);
        var recent = new List<string>();
        var paths = new Dictionary<string, string>();

        var seed = 1;
        foreach (var p in Fleet)
        {
            var dir = System.IO.Path.Combine(p.Name.StartsWith("Client", StringComparison.Ordinal) ? System.IO.Path.Combine(clients, "Acme") : projects, p.Name);
            Directory.CreateDirectory(dir);
            var plugins = (p.Entries ?? []).Select(e => $"{{ \"Name\": \"{e}\", \"Enabled\": true }}")
                .Concat((p.Tools ?? []).Take(1).Select(t => $"{{ \"Name\": \"{t.Split('@')[0]}\", \"Enabled\": true }}"));
            var modules = p.Code ? $",\r\n\t\"Modules\": [ {{ \"Name\": \"{p.Name}\", \"Type\": \"Runtime\" }} ]" : "";
            var uproject = System.IO.Path.Combine(dir, p.Name + ".uproject");
            File.WriteAllText(uproject, $"{{\r\n\t\"FileVersion\": 3,\r\n\t\"EngineAssociation\": \"{p.Engine}\"{modules},\r\n\t\"Plugins\": [ {string.Join(", ", plugins)} ]\r\n}}");
            if (p.Git) Directory.CreateDirectory(System.IO.Path.Combine(dir, ".git"));
            foreach (var t in p.Tools ?? [])
            {
                var (id, v) = (t.Split('@')[0], t.Split('@')[1]);
                FakePlugin(System.IO.Path.Combine(dir, "Plugins", "AutomationForge", id), id, v);
            }
            if (p.OwnCopy is { } own) FakePlugin(System.IO.Path.Combine(dir, "Plugins", "Tools", own.Split('@')[0]), own.Split('@')[0], own.Split('@')[1]);
            switch (p.Picture)
            {
                case "own": FakeShot(System.IO.Path.Combine(dir, p.Name + ".png"), seed++); break;
                case "auto": FakeShot(System.IO.Path.Combine(dir, "Saved", "AutoScreenshot.png"), seed++); break;
                case "broken": File.WriteAllBytes(System.IO.Path.Combine(dir, p.Name + ".png"), [0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4, 5, 6, 7, 8]); break;
            }
            if (p.ReadOnly) File.SetAttributes(uproject, FileAttributes.ReadOnly);
            if (p.DaysAgo is { } d)
                recent.Add($"RecentlyOpenedProjectFiles=(ProjectName=\"{uproject.Replace('\\', '/')}\",LastOpenTime={now.AddDays(-d).AddHours(-seed):yyyy.MM.dd-HH.mm.ss})");
            paths[p.Name] = uproject;
        }

        var ini = System.IO.Path.Combine(records, "UnrealEngine", "5.8", "Saved", "Config", "WindowsEditor");
        Directory.CreateDirectory(ini);
        File.WriteAllText(System.IO.Path.Combine(ini, "EditorSettings.ini"),
            $"[/Script/UnrealEd.EditorSettings]\r\nCreatedProjectPaths={projects}\r\n{string.Join("\r\n", recent)}\r\n");
        Environment.SetEnvironmentVariable("FORGE_UNREAL_RECORDS", records);

        var ue57 = System.IO.Path.Combine(root, "UE_5.7");
        Directory.CreateDirectory(System.IO.Path.Combine(ue57, "Engine", "Build"));
        File.WriteAllText(System.IO.Path.Combine(ue57, "Engine", "Build", "Build.version"), """{ "MajorVersion": 5, "MinorVersion": 7, "PatchVersion": 4, "IsPromotedBuild": 1 }""");

        var (window, vm) = Open("projects", receipts: [], extraEngine: ue57, prepare: (_, data) =>
            File.WriteAllText(System.IO.Path.Combine(data, "hub-state.json"),
                System.Text.Json.JsonSerializer.Serialize(new { schemaVersion = 1, agentAccess = false, projectFolders = new[] { clients } })));
        vm.PreviewAccount("Bojan Andrejek", "bojan@kovati.dev", ["MotionForge", "MontageForge"]);
        for (var i = 0; i < 60 && (vm.ProjectsScanning || vm.ProjectReports.Count < Fleet.Length); i++) Snap.Pump(250);
        Console.WriteLine($"  projects found: {vm.ProjectReports.Count} of {Fleet.Length}");
        vm.SelectedTarget = vm.Targets.First(t => t.Project?.Name == "Colony_NP24");
        Snap.Pump(300);

        void Size(int w, int h) { window.Width = w; window.Height = h; Snap.Pump(500); }

        Size(1440, 900);
        vm.ShowProjects();
        Snap.Pump(1200);
        var page = (ProjectsPageViewModel)vm.CurrentPage!;
        Console.WriteLine($"  summary: {page.SummaryLine} · chips {string.Join(" | ", page.Filters.Select(f => f.Label))}");
        Console.WriteLine($"  order (recent): {string.Join(", ", page.Tiles.Take(8).Select(t => t.Name))} …");
        Shot(window, "hub_projects_grid_1440");
        ScrollPage(window, 560);
        Shot(window, "hub_projects_grid_1440_scrolled");
        ScrollPage(window, 5000);
        Shot(window, "hub_projects_grid_1440_end");
        ScrollPage(window, 0);

        Size(1100, 800);
        Shot(window, "hub_projects_grid_1100");
        Size(1920, 1080);
        Shot(window, "hub_projects_grid_1920");
        Size(1440, 900);

        // The search, as typed: name or folder.
        page.SearchText = "client";
        Snap.Pump(400);
        Console.WriteLine($"  search 'client': {page.SummaryLine} · {string.Join(", ", page.Tiles.Select(t => t.Name))}");
        Shot(window, "hub_projects_search");
        page.SearchText = "xyz";
        Snap.Pump(400);
        Console.WriteLine($"  search 'xyz': {page.SummaryLine} · empty={page.IsFilteredEmpty} · '{page.NoMatchLine}'");
        Shot(window, "hub_projects_filter_empty");
        page.ClearFilterCommand.Execute(null);
        page.Filter = ProjectFilter.WithKovati;
        page.SortChoice = "Name";
        Snap.Pump(400);
        Console.WriteLine($"  with Kovati tools, by name: {page.SummaryLine} · {string.Join(", ", page.Tiles.Select(t => $"{t.Name} ({t.ToolsCountLine}{t.UpdatesPart})"))}");
        Shot(window, "hub_projects_filter_kovati");
        page.Filter = ProjectFilter.Updates;
        Snap.Pump(300);
        Console.WriteLine($"  updates: {string.Join(", ", page.Tiles.Select(t => t.Name))}");
        page.Filter = ProjectFilter.All;
        page.SortChoice = "Engine version";
        Snap.Pump(300);
        Console.WriteLine($"  by engine: {string.Join(", ", page.Tiles.Select(t => $"{t.Name} {t.EngineBadge}"))}");
        page.SortChoice = "Recently opened";
        Snap.Pump(300);

        // Open a tile, after scrolling: the grid's place, the search and the order must come back with "← Projects".
        page.SearchText = "a";
        Snap.Pump(300);
        ScrollPage(window, 300);
        var gridScroll = window.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Name == "GridScroll");
        var before = gridScroll.Offset;
        var colony = page.Tiles.First(t => t.Name == "Colony_NP24");
        colony.OpenCommand.Execute(null);
        Snap.Pump(900);
        Console.WriteLine($"  detail {page.Selected?.Name}: {page.Selected?.Rows.Count} in it, {page.Selected?.Missing.Count} not: {string.Join(", ", page.Selected?.Missing.Select(m => $"{m.Name} [{m.ActionText}{(m.CanAct ? "" : ", off")}]") ?? [])}");
        Shot(window, "hub_projects_detail");
        ScrollPage(window, 600);
        Shot(window, "hub_projects_detail_scrolled");
        ScrollPage(window, 5000);
        Shot(window, "hub_projects_detail_end");

        // A change written from the detail: the page is rebuilt from disk and stays on the same project.
        var colonyPath = paths["Colony_NP24"];
        var row = page.Selected!.Rows.First(r => r.Id == "MeshForge");
        row.IsOn = false;
        for (var i = 0; i < 20; i++) { Snap.Pump(200); if (vm.ShowConfirm) vm.ConfirmYesCommand.Execute(null); }
        Console.WriteLine($"  after turning MeshForge off: still on {page.Selected?.Name} (detail={page.IsDetail}); entry in .uproject: {File.ReadAllText(colonyPath).Contains("\"MeshForge\"", StringComparison.Ordinal)}; row now '{page.Selected?.Rows.First(r => r.Id == "MeshForge").StateLine}'");
        ScrollPage(window, 0);
        Shot(window, "hub_projects_detail_after_toggle");
        Size(1100, 800);
        Shot(window, "hub_projects_detail_1100");
        ScrollPage(window, 640);
        Shot(window, "hub_projects_detail_1100_scrolled");
        ScrollPage(window, 0);
        Size(1440, 900);

        // Back, as Esc and the mouse's back button do it.
        var stepped = page.BackInside();
        Snap.Pump(700);
        Console.WriteLine($"  back: stepped={stepped} grid={page.IsGrid} search='{page.SearchText}' scroll {before.Y:0} -> {gridScroll.Offset.Y:0}");
        Shot(window, "hub_projects_back_to_grid");
        page.SearchText = "";
        Snap.Pump(300);

        // An update waiting, a read-only one, a 5.7 one and one whose engine is not here.
        foreach (var (name, file) in new[] { ("MyGame", "hub_projects_detail_update"), ("Lyra_Starter", "hub_projects_detail_readonly"), ("WaterWorld", "hub_projects_detail_ue57"), ("Archive", "hub_projects_detail_not_installed") })
        {
            page.OpenProject(page.Cards.First(c => c.Name == name));
            Snap.Pump(700);
            Console.WriteLine($"  {name}: notes [{string.Join(" | ", page.Selected!.Notes)}] blocked '{page.Selected.InstallBlockedLine}' missing line '{page.Selected.MissingLine}' rows {page.Selected.Missing.Count}");
            Shot(window, file);
            page.BackInside();
            Snap.Pump(300);
        }

        // The tile's menu.
        var more = window.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.IsEffectivelyVisible && Equals(b.Content, "…"));
        if (more?.Flyout is { } flyout)
        {
            flyout.ShowAt(more);
            Snap.Pump(500);
            Shot(window, "hub_projects_tile_menu");
            flyout.Hide();
            Snap.Pump(200);
        }

        // No projects at all.
        vm.ProjectReports.Clear();
        page.Refresh();
        Snap.Pump(400);
        Shot(window, "hub_projects_none");
        window.Close();
    }

    private static void FakePlugin(string dir, string id, string version)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(System.IO.Path.Combine(dir, id + ".uplugin"), $$"""{ "FileVersion": 3, "VersionName": "{{version}}", "Installed": true }""");
    }

    /// <summary>A small made-up "screenshot": a sky, a sun, a skyline, in one hue per project.</summary>
    private static void FakeShot(string file, int seed)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
        var rnd = new Random(seed * 7919);
        var hue = seed * 53 % 360;
        Color Hsv(double h, double s, double v) => new HsvColor(1, h % 360, s, v).ToRgb();
        var canvas = new Canvas
        {
            Width = 640, Height = 360,
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Hsv(hue, 0.55, 0.18), 0), new GradientStop(Hsv(hue + 25, 0.6, 0.62), 0.72), new GradientStop(Hsv(hue + 40, 0.5, 0.85), 1) },
            },
        };
        var sun = new Ellipse { Width = 90, Height = 90, Fill = new SolidColorBrush(Hsv(hue + 40, 0.35, 1)) };
        Canvas.SetLeft(sun, 80 + rnd.Next(420)); Canvas.SetTop(sun, 110 + rnd.Next(60));
        canvas.Children.Add(sun);
        for (var layer = 0; layer < 2; layer++)
        {
            var x = -20.0;
            while (x < 660)
            {
                var w = 30 + rnd.Next(70);
                var h = (layer == 0 ? 90 : 40) + rnd.Next(layer == 0 ? 140 : 90);
                var b = new Rectangle { Width = w, Height = h, Fill = new SolidColorBrush(Hsv(hue + 200, 0.25, layer == 0 ? 0.16 : 0.07)) };
                Canvas.SetLeft(b, x); Canvas.SetTop(b, 360 - h);
                canvas.Children.Add(b);
                x += w + rnd.Next(12);
            }
        }
        var size = new Size(640, 360);
        canvas.Measure(size);
        canvas.Arrange(new Rect(size));
        using var rtb = new RenderTargetBitmap(new PixelSize(640, 360), new Vector(96, 96));
        rtb.Render(canvas);
        rtb.Save(file);
    }
}
