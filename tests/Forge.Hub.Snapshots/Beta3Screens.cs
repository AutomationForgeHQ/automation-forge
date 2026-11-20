using System.Text.Json;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Forge.Hub.ViewModels;
using Forge.Hub.ViewModels.Pages;

namespace Forge.Hub.Snapshots;

/// <summary>
/// 0.5.0-beta.3: engines and projects, from fixed data. Two fake engines (5.8 with builds, 5.7 without), Unreal's
/// records as a throwaway folder (FORGE_UNREAL_RECORDS), three projects it lists, one in a folder of the person's,
/// and one the Hub installed into. Nothing of this machine's engines, projects or sign-in is read or written.
///
///     dotnet run --project tests/Forge.Hub.Snapshots -- &lt;out dir&gt; beta3
/// </summary>
internal static partial class Screens
{
    public static void RunBeta3(string outDir, string[] only)
    {
        _out = outDir;
        _only = only;
        _scratch = Path.Combine(Path.GetTempPath(), "kovati-hub-snapshots-b3");
        if (Directory.Exists(_scratch)) Directory.Delete(_scratch, true);
        Directory.CreateDirectory(_scratch);
        var repo = FindRepo();
        Environment.SetEnvironmentVariable("FORGE_MANIFEST_URL", Path.Combine(repo, "manifest.json"));
        Environment.SetEnvironmentVariable("FORGE_RELEASES_API", "off");
        Environment.SetEnvironmentVariable("FORGE_CATALOGUE_URL", "off");
        Environment.SetEnvironmentVariable("FORGE_SITE_URL", "http://127.0.0.1:9");
        Environment.SetEnvironmentVariable("FORGE_API_URL", "http://127.0.0.1:9/api");
        Environment.SetEnvironmentVariable("FORGE_ACCOUNT_URL", "http://127.0.0.1:9");
        Environment.SetEnvironmentVariable("FORGE_UNREAL_RECORDS", "off");
        Beta3();
        Console.WriteLine($"done: {outDir}");
    }

    private static void Beta3()
    {
        var b3 = Path.Combine(_scratch, "beta3");
        var records = Path.Combine(b3, "records");
        var projects = Path.Combine(b3, "Projects");
        var clients = Path.Combine(b3, "Clients");
        string Uproject(string dir, string body)
        {
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, Path.GetFileName(dir) + ".uproject");
            File.WriteAllText(path, body.Replace("\n", "\r\n"));
            return path;
        }
        static void Plugin(string dir, string id, string version)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, id + ".uplugin"), $$"""{ "FileVersion": 3, "VersionName": "{{version}}", "Installed": true }""");
        }

        var myGame = Uproject(Path.Combine(projects, "MyGame"), "{\n\t\"FileVersion\": 3,\n\t\"EngineAssociation\": \"5.8\",\n\t\"Plugins\": [\n\t\t{\n\t\t\t\"Name\": \"MotionForge\",\n\t\t\t\"Enabled\": true\n\t\t}\n\t]\n}");
        var studio = Uproject(Path.Combine(projects, "Studio"), "{\n\t\"FileVersion\": 3,\n\t\"EngineAssociation\": \"5.8\",\n\t\"Modules\": [\n\t\t{\n\t\t\t\"Name\": \"Studio\",\n\t\t\t\"Type\": \"Runtime\"\n\t\t}\n\t],\n\t\"Plugins\": [\n\t\t{\n\t\t\t\"Name\": \"MontageForge\",\n\t\t\t\"Enabled\": true\n\t\t}\n\t]\n}");
        Directory.CreateDirectory(Path.Combine(projects, "Studio", ".git"));
        Plugin(Path.Combine(projects, "Studio", "Plugins", "AutomationForge", "MontageForge"), "MontageForge", "0.1.2");
        var archive = Uproject(Path.Combine(b3, "Old", "Archive"), "{\n\t\"FileVersion\": 3,\n\t\"EngineAssociation\": \"5.6\"\n}");
        Uproject(Path.Combine(clients, "Acme", "ClientDemo"), "{\n\t\"FileVersion\": 3,\n\t\"EngineAssociation\": \"5.8\"\n}");

        var ini = Path.Combine(records, "UnrealEngine", "5.8", "Saved", "Config", "WindowsEditor");
        Directory.CreateDirectory(ini);
        string F(string p) => p.Replace('\\', '/');
        File.WriteAllText(Path.Combine(ini, "EditorSettings.ini"), $"""
            [/Script/UnrealEd.EditorSettings]
            CreatedProjectPaths={projects}
            RecentlyOpenedProjectFiles=(ProjectName="{F(studio)}",LastOpenTime=2026.11.19-22.10.00)
            RecentlyOpenedProjectFiles=(ProjectName="{F(myGame)}",LastOpenTime=2026.11.18-09.30.00)
            RecentlyOpenedProjectFiles=(ProjectName="{F(archive)}",LastOpenTime=2026.03.02-14.00.00)
            RecentlyOpenedProjectFiles=(ProjectName="{F(Path.Combine(b3, "Gone", "Gone.uproject"))}",LastOpenTime=2026.02.01-10.00.00)
            """);
        Environment.SetEnvironmentVariable("FORGE_UNREAL_RECORDS", records);

        var ue57 = Path.Combine(b3, "UE_5.7");
        Directory.CreateDirectory(Path.Combine(ue57, "Engine", "Build"));
        File.WriteAllText(Path.Combine(ue57, "Engine", "Build", "Build.version"), """{ "MajorVersion": 5, "MinorVersion": 7, "PatchVersion": 4, "IsPromotedBuild": 1 }""");

        var (window, vm) = Open("beta3", receipts:
        [
            ("AutomationForgeHub", "0.2.5"), ("AutomationForge", "0.1.1"), ("MotionForge", "0.4.1"), ("MotionForgeKimodo", "0.5.1"),
        ], extraEngine: ue57, prepare: (root, data) =>
        {
            foreach (var (id, v) in new[] { ("AutomationForgeHub", "0.2.5"), ("AutomationForge", "0.1.1"), ("MotionForge", "0.4.1"), ("MotionForgeKimodo", "0.5.1") })
                Plugin(Path.Combine(root, id), id, v);
            var receipts = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(File.ReadAllText(Path.Combine(data, "installed.json")))!;
            var studioRoot = Path.Combine(projects, "Studio", "Plugins", "AutomationForge");
            receipts.Add(new() { ["plugin"] = "MontageForge", ["version"] = "0.1.2", ["engine"] = "5.8", ["channel"] = "stable", ["targetKind"] = "project",
                ["targetRoot"] = studioRoot, ["path"] = Path.Combine(studioRoot, "MontageForge"), ["installedAt"] = "2026-11-19T10:00:00Z" });
            File.WriteAllText(Path.Combine(data, "installed.json"), JsonSerializer.Serialize(receipts));
            File.WriteAllText(Path.Combine(data, "hub-state.json"), JsonSerializer.Serialize(new { schemaVersion = 1, agentAccess = false, projectFolders = new[] { clients } }));
        });
        vm.PreviewAccount("Bojan Andrejek", "bojan@kovati.dev", ["MotionForge", "MontageForge"]);
        for (var i = 0; i < 40 && (vm.ProjectsScanning || vm.ProjectReports.Count == 0); i++) Snap.Pump(250);
        Console.WriteLine($"  targets: {string.Join(" | ", vm.Targets.Select(t => t.MenuLabel))}");
        Console.WriteLine($"  projects: {string.Join(", ", vm.ProjectReports.Select(r => $"{r.Name} ({r.Engine.Label}, {r.Plugins.Count} tools)"))}");

        vm.ShowProjects();
        Snap.Pump(800);
        Shot(window, "hub_b3_projects");
        ScrollPage(window, 520);
        Shot(window, "hub_b3_projects_scrolled");

        // Turning MotionForge off in MyGame from the page: the one entry changes in the .uproject.
        var page = (ProjectsPageViewModel)vm.CurrentPage!;
        var row = page.Cards.Single(c => c.Name == "MyGame").Rows.Single(r => r.Id == "MotionForge");
        var before = File.ReadAllText(myGame);
        row.IsOn = false;
        for (var i = 0; i < 20; i++) { Snap.Pump(200); if (vm.ShowConfirm) { Shot(window, "hub_b3_confirm_live"); vm.ConfirmYesCommand.Execute(null); } }
        var after = File.ReadAllText(myGame);
        Console.WriteLine($"  MyGame.uproject after turning MotionForge off: {(before.Replace("\"Enabled\": true", "\"Enabled\": false") == after ? "exactly the one token changed" : "CHANGED MORE: " + after)}");
        ScrollPage(window, 0);
        Shot(window, "hub_b3_projects_after_toggle");

        // Settings, with the person's project folder and what it found.
        vm.ToggleSettingsCommand.Execute(null);
        Snap.Pump(400);
        var drawer = window.GetVisualDescendants().OfType<ScrollViewer>()
            .Where(s => s.IsEffectivelyVisible && s.Bounds.Width is > 300 and < 460).OrderByDescending(s => s.Bounds.Height).FirstOrDefault();
        if (drawer is not null) { drawer.Offset = new Avalonia.Vector(0, 760); Snap.Pump(300); }
        Shot(window, "hub_b3_settings_project_folders");
        vm.ClosePanels();

        // The footer's target menu.
        var footer = window.GetVisualDescendants().OfType<ComboBox>().FirstOrDefault(c => c.Classes.Contains("footer") && c.IsEffectivelyVisible);
        if (footer is not null)
        {
            footer.IsDropDownOpen = true;
            Snap.Pump(500);
            Shot(window, "hub_b3_target_menu");
            footer.IsDropDownOpen = false;
            Snap.Pump(200);
        }

        // A project as the target: the track page says where it installs, and plans around what the engine has.
        vm.SelectedTarget = vm.Targets.First(t => t.Project?.Name == "Studio");
        Snap.Pump(300);
        vm.GoHome();
        var track = OpenTrack(vm, "animation");
        var plan = Core.Catalogue.TrackPlanner.Plan(track.Track, Core.Catalogue.TrackPlanner.DefaultSelection(track.Track, vm.Inputs()!), vm.Inputs()!);
        Console.WriteLine($"  into Studio: {track.Summary}, button '{track.ButtonText}', installs {string.Join(", ", plan.Ids)}; here already {string.Join(", ", plan.Items.Where(i => i.Action == Core.Catalogue.PlanAction.Current).Select(i => i.Id))}");
        Console.WriteLine($"  notes: {string.Join(" | ", track.Notes)}");
        Shot(window, "hub_b3_track_into_project");
        vm.GoHome();
        vm.OpenProduct("montageforge");
        Snap.Pump(700);
        Shot(window, "hub_b3_product_into_project");

        // Unreal Engine 5.7, with no Kovati builds: listed, marked, nothing installable.
        vm.SelectedTarget = vm.Targets.First(t => !t.IsProject && t.Engine.Version == "5.7");
        Snap.Pump(300);
        vm.GoHome();
        track = OpenTrack(vm, "animation");
        Console.WriteLine($"  on 5.7: button '{track.ButtonText}' enabled={track.ButtonEnabled}; notes {string.Join(" | ", track.Notes)}; row line '{track.Rows.FirstOrDefault()?.ExtraLine}'");
        Shot(window, "hub_b3_track_ue57");
        vm.GoHome();
        vm.OpenProduct("motionforge");
        Snap.Pump(700);
        var pp = (ProductPageViewModel)vm.CurrentPage!;
        Console.WriteLine($"  product on 5.7: '{pp.ButtonText}' enabled={pp.ButtonEnabled} · {pp.ButtonLine}");
        Shot(window, "hub_b3_product_not_built_57");

        // The question asked when an editor runs whose command line cannot be read.
        _ = vm.ConfirmAsync("Is Studio open in Unreal?",
            "Unreal Editor is running, and Kovati Hub cannot tell which project it has open. If it has Studio open, close it first: Unreal keeps its own copy of the project and its tools.",
            "It is closed, go ahead");
        Snap.Pump(300);
        Shot(window, "hub_b3_confirm_editor");
        vm.ConfirmNoCommand.Execute(null);
        window.Close();
    }
}
