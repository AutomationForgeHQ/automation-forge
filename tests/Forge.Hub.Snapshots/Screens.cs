using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Forge.Core;
using Forge.Hub.ViewModels;
using Forge.Hub.ViewModels.Pages;
using Forge.Hub.Views;

namespace Forge.Hub.Snapshots;

/// <summary>
/// Every screen and state of the Hub, from fixed data, at 1180 x 760. Each scenario gets its own throwaway data
/// folder and a fake engine folder (FORGE_DATA_DIR, FORGE_ENGINES), the repository's manifest as a file
/// (FORGE_MANIFEST_URL), no GitHub (FORGE_RELEASES_API=off) and no account service. Nothing on this machine's
/// real engines or sign-in is read or written.
/// </summary>
internal static class Screens
{
    private static string _scratch = "";
    private static string _out = "";
    private static string[] _only = [];

    public static void Run(string outDir, string[] only)
    {
        _out = outDir;
        _only = only;
        _scratch = Path.Combine(Path.GetTempPath(), "kovati-hub-snapshots");
        if (Directory.Exists(_scratch)) Directory.Delete(_scratch, true);
        Directory.CreateDirectory(_scratch);

        var repo = FindRepo();
        Environment.SetEnvironmentVariable("FORGE_MANIFEST_URL", Path.Combine(repo, "manifest.json"));
        Environment.SetEnvironmentVariable("FORGE_RELEASES_API", "off");
        Environment.SetEnvironmentVariable("FORGE_API_URL", "http://127.0.0.1:9/api");
        Environment.SetEnvironmentVariable("FORGE_ACCOUNT_URL", "http://127.0.0.1:9");

        FirstRun();
        NoEngine();
        SignedIn();
        Returning();
        Console.WriteLine($"done: {outDir}");
    }

    // ── scenarios ────────────────────────────────────────────────────────

    private static void FirstRun()
    {
        var (window, vm) = Open("firstrun", receipts: []);
        Shot(window, "hub_home_first_run");

        if (Want("hub_home_first_run_hover"))
        {
            window.MouseMove(new Point(32 + 264 + 20 + 132, 56 + 300));
            Snap.Pump(1200);
            Shot(window, "hub_home_first_run_hover");
            window.MouseMove(new Point(5, 740));
            Snap.Pump(200);
        }

        var track = OpenTrack(vm, "animation");
        Shot(window, "hub_track_animation_signed_out");

        track.ToggleComponentsCommand.Execute(null);
        Snap.Pump(300);
        ScrollPanel(window, 520);
        Shot(window, "hub_track_animation_components");
        track.ToggleComponentsCommand.Execute(null);
        ScrollPanel(window, 0);

        track.Rows.Single(r => r.Capability.Id == "gameplay").IsTicked = false;
        track.Rows.Single(r => r.Capability.Id == "motion").Engines.Single(e => e.Slug == "motionforge-uthana").IsTicked = false;
        Snap.Pump(200);
        Console.WriteLine($"  unticked summary: {track.Summary}");
        Shot(window, "hub_track_animation_unticked");

        ScrollLeft(window, 1000);
        Shot(window, "hub_track_animation_scrolled");

        foreach (var slug in new[] { "characters", "worldbuilding", "storytelling" })
        {
            vm.GoHome();
            OpenTrack(vm, slug);
            Shot(window, $"hub_track_{slug}");
            if (slug == "worldbuilding") { ScrollLeft(window, 520); Shot(window, "hub_track_worldbuilding_scrolled"); }
        }

        vm.ShowLibrary();
        Snap.Pump(400);
        Shot(window, "hub_library_components_empty_target");

        vm.ShowRunnersPage();
        Snap.Pump(300);
        Shot(window, "hub_runners_none");

        vm.GoHome();
        vm.ToggleSettingsCommand.Execute(null);
        Snap.Pump(300);
        Shot(window, "hub_drawer_settings");
        vm.ClosePanels();
        vm.ShowKeysCommand.Execute(null);
        Snap.Pump(300);
        Shot(window, "hub_drawer_keys");
        vm.ClosePanels();

        window.Close();
    }

    private static void NoEngine()
    {
        var (window, vm) = Open("noengine", receipts: [], engine: false);
        Shot(window, "hub_home_no_engine");
        OpenTrack(vm, "animation");
        Shot(window, "hub_track_animation_no_engine");
        window.Close();
    }

    private static void SignedIn()
    {
        var (window, vm) = Open("signedin", receipts: []);
        vm.PreviewAccount("Bojan Andrejek", "bojan@kovati.dev", ["MotionForge", "MontageForge"]);
        Snap.Pump(300);
        Shot(window, "hub_home_signed_in");

        var track = OpenTrack(vm, "animation");
        Shot(window, "hub_track_animation_signed_in");

        track.AgentsOn = true;
        Snap.Pump(200);
        Console.WriteLine($"  agents summary: {track.Summary}; hub-state agentAccess={HubState.Load().AgentAccess}");
        Shot(window, "hub_track_animation_agents_on");

        // Acceptance 8: a second track's panel shows the switch on.
        vm.GoHome();
        var story = OpenTrack(vm, "storytelling");
        Console.WriteLine($"  storytelling switch after turning it on in Animation: {story.AgentsOn}");
        Shot(window, "hub_track_storytelling_agents_on");

        // The install panel while installing, and when done with one failure (4.5), fed the events the
        // installer would send. Nothing is downloaded.
        vm.GoHome();
        track = OpenTrack(vm, "animation");
        track.AgentsOn = false;
        var plan = Core.Catalogue.TrackPlanner.Plan(track.Track,
            Core.Catalogue.TrackPlanner.DefaultSelection(track.Track, vm.Inputs()!), vm.Inputs()!);
        var items = plan.ToChange.ToList();
        track.StartInstalling(items);
        track.OnEvent(new InstallEvent(items[0].Id, InstallEventKind.Started));
        track.OnEvent(new InstallEvent(items[0].Id, InstallEventKind.Done));
        track.OnEvent(new InstallEvent(items[1].Id, InstallEventKind.Done));
        track.OnEvent(new InstallEvent(items[2].Id, InstallEventKind.Done));
        track.OnEvent(new InstallEvent(items[3].Id, InstallEventKind.Started));
        track.OnEvent(new InstallEvent(items[3].Id, InstallEventKind.Progress, 0.42));
        Snap.Pump(300);
        Shot(window, "hub_track_animation_installing");

        track.OnEvent(new InstallEvent(items[3].Id, InstallEventKind.Done));
        track.OnEvent(new InstallEvent(items[4].Id, InstallEventKind.Started));
        track.OnEvent(new InstallEvent(items[4].Id, InstallEventKind.Failed,
            Message: InstallErrors.Describe("MontageForge is in use — Unreal Editor has it loaded.", "MontageForge", "")));
        track.ShowDone(items);
        Snap.Pump(300);
        Shot(window, "hub_track_animation_done_one_failed");

        vm.OpenAccountCommand.Execute(null);
        Snap.Pump(300);
        Shot(window, "hub_drawer_account");
        window.Close();
    }

    private static void Returning()
    {
        // An older Hub's profile: MotionForge one version behind, Kimodo, Montage and a toolset installed.
        var (window, vm) = Open("returning", receipts:
        [
            ("AutomationForgeHub", "0.2.5"), ("MotionForge", "0.4.0"), ("MotionForgeKimodo", "0.5.1"),
            ("MontageForge", "0.1.3"), ("MotionForgeToolset", "0.3.1"), ("SpeechForge", "0.2.3"),
        ]);
        Console.WriteLine($"  migration: agentAccess={HubState.Load().AgentAccess} (a toolset was installed)");
        Shot(window, "hub_home_returning");

        var track = OpenTrack(vm, "animation");
        Console.WriteLine($"  returning animation: {track.Summary}, button '{track.ButtonText}', switch {track.AgentsOn}");
        Shot(window, "hub_track_animation_returning");

        vm.ShowLibrary();
        Snap.Pump(500);
        Shot(window, "hub_library_components");
        if (vm.CurrentPage is ComponentsPageViewModel cp)
        {
            cp.Filter = "Motion";
            Snap.Pump(300);
            Shot(window, "hub_library_components_filtered");
        }
        window.Close();
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private static bool Want(string name) => _only.Length == 0 || _only.Any(o => name.Contains(o, StringComparison.OrdinalIgnoreCase));

    private static void Shot(Window window, string name)
    {
        if (!Want(name)) return;
        Snap.Window(window, Path.Combine(_out, name + ".png"));
    }

    private static TrackPageViewModel OpenTrack(MainViewModel vm, string slug)
    {
        vm.OpenTrack(slug);
        Snap.Pump(600);
        return (TrackPageViewModel)vm.CurrentPage!;
    }

    private static void ScrollLeft(Window window, double y)
    {
        var sv = window.GetVisualDescendants().OfType<ScrollViewer>()
            .Where(s => s.IsEffectivelyVisible && s.FindAncestorOfType<Views.Pages.TrackPageView>() is not null)
            .OrderByDescending(s => s.Bounds.Width).FirstOrDefault();
        if (sv is null) return;
        sv.Offset = new Vector(0, y);
        Snap.Pump(400);
    }

    private static void ScrollPanel(Window window, double y)
    {
        var sv = window.GetVisualDescendants().OfType<ScrollViewer>()
            .Where(s => s.IsEffectivelyVisible && s.FindAncestorOfType<Views.Pages.TrackPageView>() is not null)
            .OrderBy(s => s.Bounds.Width).FirstOrDefault();
        if (sv is null) return;
        sv.Offset = new Vector(0, y);
        Snap.Pump(300);
    }

    private static (MainWindow window, MainViewModel vm) Open(string scenario, (string id, string version)[] receipts, bool engine = true)
    {
        var data = Path.Combine(_scratch, scenario, "data");
        var engineDir = Path.Combine(_scratch, scenario, "UE_5.8");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "settings.json"), """{ "channel": "stable", "checkForUpdates": false, "runInBackground": false, "notifyOnUpdates": false }""");
        // The account service's catalogue answer of 19 November 2026 (prices, payments simulated), as its cache.
        File.WriteAllText(Path.Combine(data, "account-catalogue.json"), """
            { "payments": "simulated", "plugins": [
              { "id": "MeshForgeGarment", "price": { "amount": 4999, "currency": "usd" }, "forSale": true, "released": true },
              { "id": "PerformanceForge", "price": { "amount": 7999, "currency": "usd" }, "forSale": true, "released": true },
              { "id": "SpeechForgeDeepL", "price": { "amount": 4999, "currency": "usd" }, "forSale": true, "released": true },
              { "id": "TerrainForge", "price": { "amount": 6900, "currency": "usd" }, "forSale": true, "released": true } ] }
            """);
        Environment.SetEnvironmentVariable("FORGE_DATA_DIR", data);

        if (engine)
        {
            Directory.CreateDirectory(Path.Combine(engineDir, "Engine", "Build"));
            File.WriteAllText(Path.Combine(engineDir, "Engine", "Build", "Build.version"),
                """{ "MajorVersion": 5, "MinorVersion": 8, "PatchVersion": 1, "IsPromotedBuild": 1 }""");
            Environment.SetEnvironmentVariable("FORGE_ENGINES", engineDir);
        }
        else Environment.SetEnvironmentVariable("FORGE_ENGINES", Path.Combine(_scratch, scenario, "no-engine-here"));

        var root = Path.Combine(engineDir, "Engine", "Plugins", "Marketplace", "AutomationForge");
        var items = receipts.Select(r =>
        {
            var dir = Path.Combine(root, r.id);
            Directory.CreateDirectory(dir);
            return new { plugin = r.id, version = r.version, engine = "5.8", channel = "stable", targetKind = "engine", targetRoot = root, path = dir, installedAt = "2026-11-01T10:00:00Z" };
        }).ToList();
        File.WriteAllText(Path.Combine(data, "installed.json"), JsonSerializer.Serialize(items));

        var settings = Settings.Load();
        var vm = new MainViewModel(settings, null);
        var window = new MainWindow { DataContext = vm, Width = 1180, Height = 760 };
        window.Show();
        Snap.Pump(1500);
        return (window, vm);
    }

    private static string FindRepo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Forge.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Run from inside the repository.");
    }
}
