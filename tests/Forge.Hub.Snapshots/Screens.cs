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
        // Fixed data: the bundled catalogue only, and no picture from any site (slice 5 fetches both otherwise).
        Environment.SetEnvironmentVariable("FORGE_CATALOGUE_URL", "off");
        Environment.SetEnvironmentVariable("FORGE_SITE_URL", "http://127.0.0.1:9");
        Environment.SetEnvironmentVariable("FORGE_API_URL", "http://127.0.0.1:9/api");
        Environment.SetEnvironmentVariable("FORGE_ACCOUNT_URL", "http://127.0.0.1:9");

        FirstRun();
        NoEngine();
        SignedIn();
        Returning();
        Setup();
        Updates();
        Browse();
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

    /// <summary>Slice 2: a track installed through its page, its checklist ticking itself, and the full returning Home.</summary>
    private static void Setup()
    {
        var project = Path.Combine(_scratch, "setup", "MyGame", "MyGame.uproject");
        var (window, vm) = Open("setup", receipts:
        [
            ("AutomationForgeHub", "0.2.5"), ("MotionForge", "0.4.1"), ("MotionForgeKimodo", "0.5.1"),
            ("MotionForgeUthana", "0.2.1"), ("MontageForge", "0.1.3"), ("SpeechForge", "0.2.2"),
        ], prepare: (root, data) =>
        {
            Directory.CreateDirectory(Path.Combine(root, "MotionForgeKimodo", "Config"));
            File.WriteAllText(Path.Combine(root, "MotionForgeKimodo", "Config", "ForgeMachine.json"), """
                { "keys": [
                    { "id": "HuggingFace", "scope": "general", "displayName": "Hugging Face token", "vaultEntry": "KovatiHubSnapshot/HuggingFace", "optional": false },
                    { "id": "Runpod", "scope": "general", "displayName": "Runpod", "vaultEntry": "KovatiHubSnapshot/Runpod", "optional": true } ],
                  "runners": [ { "id": "Kimodo", "displayName": "Kimodo", "compose": "Runner/docker-compose.yml", "composeProject": "kovati-snapshot-kimodo", "service": "runner" } ] }
                """);
            Directory.CreateDirectory(Path.Combine(root, "MotionForgeUthana", "Config"));
            File.WriteAllText(Path.Combine(root, "MotionForgeUthana", "Config", "ForgeMachine.json"), """
                { "keys": [ { "id": "MotionForge.Uthana", "scope": "plugin", "displayName": "Uthana", "vaultEntry": "KovatiHubSnapshot/Uthana" } ] }
                """);
            File.WriteAllText(Path.Combine(data, "hub-state.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = 1, agentAccess = false, showLabs = false, lastTarget = root,
                tracks = new[] { new { track = "animation", target = root, installedAt = "2026-11-19T02:14:00Z", capabilities = new[] { "motion", "gameplay" },
                    engines = new Dictionary<string, string[]> { ["motion"] = ["motionforge-kimodo", "motionforge-uthana"] }, checklistHidden = false, firstResultOpened = false } },
                projects = Array.Empty<object>(), pins = new Dictionary<string, string>(),
            }));
            Directory.CreateDirectory(Path.GetDirectoryName(project)!);
            File.WriteAllText(project, """{ "FileVersion": 3, "EngineAssociation": "5.8", "Plugins": [ { "Name": "ModelingToolsEditorMode", "Enabled": true } ] }""");
        });
        var facts = new FakeFacts();
        vm.FactsOverride = facts;
        vm.PreviewAccount("Bojan Andrejek", "bojan@kovati.dev", ["MotionForge", "MontageForge", "PerformanceForge"]);
        vm.GoHome();
        Snap.Pump(1500);
        Shot(window, "hub_home_returning_full");

        var track = OpenTrack(vm, "animation");
        Snap.Pump(1500);
        Console.WriteLine($"  checklist: {track.ChecklistCount}, mode {track.Mode}");
        Shot(window, "hub_track_animation_checklist");

        track.UseProjectCommand.Execute(project);
        Snap.Pump(1200);
        var enabled = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(project))!["Plugins"]!.AsArray()
            .Select(n => n!["Name"]!.GetValue<string>()).ToList();
        Console.WriteLine($"  project now enables: {string.Join(", ", enabled)}");

        facts.Keys.Add("HuggingFace");
        Snap.Pump(6000); // the 5-second self-check
        Console.WriteLine($"  after the token: {track.ChecklistCount}");
        Shot(window, "hub_track_animation_checklist_progress");

        track.HideChecklistCommand.Execute(null);
        Snap.Pump(500);
        Console.WriteLine($"  hidden: link '{track.ChecklistLink}' visible={track.HasChecklistLink}");
        Shot(window, "hub_track_animation_checklist_hidden");

        vm.SetAgentAccess(true);
        vm.AgentAccessSetting = true;
        vm.ToggleSettingsCommand.Execute(null);
        Snap.Pump(400);
        Console.WriteLine($"  settings: {vm.AddAgentAccessLine} -> {string.Join(", ", vm.MissingAgentAccess().Select(x => x.Id))}");
        Shot(window, "hub_drawer_settings_agents");
        window.Close();
    }

    private sealed class FakeFacts : Core.Catalogue.IMachineFacts
    {
        private readonly LiveMachineFacts _live = new();
        public HashSet<string> Keys { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool KeySet(Core.Machine.DeclaredKey key) => Keys.Contains(key.Id);
        public Core.Machine.DockerState Docker() => Core.Machine.DockerState.NotInstalled;
        public Core.Machine.ContainerState Runner(Core.Machine.DeclaredRunner runner) => Core.Machine.ContainerState.Missing;
        public bool? ProjectEnabled(string uproject, string plugin) => _live.ProjectEnabled(uproject, plugin);
    }

    /// <summary>Slice 3: MotionForge, its Kimodo engine and its agent access one version behind; a pinned and a Labs product.</summary>
    private static void Updates()
    {
        var (window, vm) = Open("updates", receipts:
        [
            ("AutomationForgeHub", "0.2.5"), ("MotionForge", "0.4.0"), ("MotionForgeKimodo", "0.5.0"), ("MotionForgeToolset", "0.3.0"),
            ("FaceForge", "0.2.2"), ("FaceForgeMetaHuman", "0.2.2"), ("MeshForge", "0.3.2"), ("MeshForgeTrellis", "0.0.2"),
            ("SpeechForge", "0.2.3"), ("SpeechForgeElevenLabs", "0.1.2"),
        ], prepare: (root, data) => File.WriteAllText(Path.Combine(data, "hub-state.json"),
            """{ "schemaVersion": 1, "agentAccess": true, "pins": { "FaceForge": "0.2.2", "FaceForgeMetaHuman": "0.2.2" } }"""));
        Console.WriteLine($"  pill: '{vm.PillLabel}' visible={vm.HasPill}");
        vm.ShowLibrary();
        Snap.Pump(600);
        Shot(window, "hub_library_updates");
        if (vm.CurrentPage is LibraryPageViewModel lib)
        {
            Console.WriteLine($"  update all: {lib.UpdateAllTip}");
            var row = lib.Groups.SelectMany(g => g.Rows).First();
            Console.WriteLine($"  row: {row.Title} · {row.PartsLine}; notes for {row.Notes.Count} parts, {row.Notes.Sum(n => n.Entries.Count)} versions");
            row.NotesOpen = true;
            Snap.Pump(500);
            Shot(window, "hub_library_updates_notes");
            lib.Tab = LibraryTab.Installed;
            Snap.Pump(500);
            Shot(window, "hub_library_installed");
            lib.Tab = LibraryTab.Account;
            Snap.Pump(300);
            Shot(window, "hub_library_account_signed_out");
        }
        window.Close();
    }

    /// <summary>Slice 4: Browse, product pages, search and Labs, checked against the slice's acceptance.</summary>
    private static void Browse()
    {
        // The interface shots are not in the installer's set: for local work they come from the website folder.
        var site = @"C:\UNREAL\AutomationForge-website\v2\public";
        if (Directory.Exists(site)) Environment.SetEnvironmentVariable("FORGE_MEDIA_URL", site);
        var (window, vm) = Open("browse", receipts: [("AutomationForgeHub", "0.2.5"), ("MotionForge", "0.4.1"), ("MotionForgeKimodo", "0.5.1"), ("MotionForgeToolset", "0.3.1")]);
        vm.ShowBrowse();
        Snap.Pump(800);
        var browse = (BrowsePageViewModel)vm.CurrentPage!;
        Console.WriteLine($"  forges: {browse.Forges.Count} ({string.Join(", ", browse.Forges.Select(f => f.Name))})");
        Console.WriteLine($"  categories: {string.Join(" · ", browse.Categories.Select(c => c.Label))}");
        Shot(window, "hub_browse");
        ScrollPage(window, 640);
        Shot(window, "hub_browse_tools_shelf");

        browse.Category = "Add-on";
        Snap.Pump(500);
        Console.WriteLine($"  add-on groups: {string.Join(" · ", browse.Groups.Select(g => $"{g.Label} {g.Posters.Count}"))}");
        var trellisOff = browse.Groups.SelectMany(g => g.Posters).Any(p => p.Slug == "meshforge-trellis");
        ScrollPage(window, 640);
        Shot(window, "hub_browse_addons");

        browse.ShowLabs = true;
        Snap.Pump(500);
        var trellisOn = browse.Groups.SelectMany(g => g.Posters).Any(p => p.Slug == "meshforge-trellis");
        Console.WriteLine($"  TRELLIS on the Add-ons shelf: Labs off {trellisOff}, Labs on {trellisOn}");
        ScrollPage(window, 0);
        Shot(window, "hub_browse_labs_on");
        var quality = browse.Groups.SelectMany(g => g.Posters).Concat(browse.Shelf).Any(p => p.Slug == "motionforge-quality");
        Console.WriteLine($"  MotionForge Quality on a shelf: {quality}");
        browse.ShowLabs = false;

        browse.Category = "Utility";
        Snap.Pump(300);
        ScrollPage(window, 640);
        Shot(window, "hub_browse_utilities");

        // Search.
        foreach (var (q, name) in new[] { ("motion", "hub_search_motion"), ("trellis", "hub_search_labs"), ("MotionForgeQualityToolset", "hub_search_component"), ("zzz", "hub_search_nothing") })
        {
            vm.SearchText = q;
            Snap.Pump(400);
            Console.WriteLine($"  search '{q}': {string.Join(" | ", vm.SearchSections.Select(s => $"{s.Label}: {string.Join(", ", s.Items.Select(i => i.Title + (i.Chips.Any(c => c.IsLabs) ? " [LABS]" : "")))}"))}");
            Shot(window, name);
        }
        vm.SearchText = "Quality";
        Snap.Pump(200);
        Console.WriteLine($"  search 'Quality' finds MotionForge Quality: {vm.SearchSections.SelectMany(s => s.Items).Any(i => i.Title == "MotionForge Quality")}");
        vm.ClearSearch();

        // Every product opens a page; the blocks it shows are the ones it has content for.
        var failures = new List<string>();
        foreach (var p in vm.Catalogue!.Products)
        {
            try
            {
                vm.OpenProduct(p.Slug);
                Snap.Pump(60);
                if (vm.CurrentPage is not ProductPageViewModel pp || pp.Product.Slug != p.Slug) failures.Add(p.Slug);
                else if (pp.HasInside && pp.Inside!.Items.Count == 0 || pp.HasAdds && pp.Adds.Count == 0) failures.Add(p.Slug + " (empty block)");
            }
            catch (Exception ex) { failures.Add($"{p.Slug}: {ex.Message}"); }
            vm.GoBack();
        }
        Console.WriteLine($"  product pages: {vm.Catalogue.Products.Count} opened, problems: {(failures.Count == 0 ? "none" : string.Join(", ", failures))}");

        foreach (var slug in new[] { "motionforge", "terrainforge", "meshweightremap", "meshforge-trellis", "np-voiceover", "faceforge" })
        {
            vm.GoHome();
            vm.OpenProduct(slug);
            Snap.Pump(900);
            Shot(window, $"hub_product_{slug}");
            if (slug is "motionforge" or "faceforge")
            {
                ScrollPage(window, 700);
                Shot(window, $"hub_product_{slug}_blocks");
            }
        }
        vm.GoHome();
        vm.OpenProduct("motionforge");
        Snap.Pump(600);
        var mf = (ProductPageViewModel)vm.CurrentPage!;
        if (mf.Shots.Count > 0)
        {
            mf.Shots[0].OpenCommand.Execute(null);
            Snap.Pump(900);
            Shot(window, "hub_product_viewer");
            mf.CloseViewerCommand.Execute(null);
        }
        mf.AskRemoveCommand.Execute(null);
        Snap.Pump(300);
        Console.WriteLine($"  remove: {mf.RemoveTitle} {mf.RemoveLine}");
        Shot(window, "hub_product_remove_dialog");
        mf.KeepCommand.Execute(null);
        Environment.SetEnvironmentVariable("FORGE_MEDIA_URL", null);
        window.Close();
    }

    private static void ScrollPage(Window window, double y)
    {
        var sv = window.GetVisualDescendants().OfType<ScrollViewer>()
            .Where(s => s.IsEffectivelyVisible && s.Bounds.Height > 400 && s.Bounds.Width > 900)
            .OrderByDescending(s => s.Bounds.Width).FirstOrDefault();
        if (sv is null) return;
        sv.Offset = new Vector(0, y);
        Snap.Pump(400);
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

    private static (MainWindow window, MainViewModel vm) Open(string scenario, (string id, string version)[] receipts, bool engine = true, Action<string, string>? prepare = null)
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
        prepare?.Invoke(root, data);

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
