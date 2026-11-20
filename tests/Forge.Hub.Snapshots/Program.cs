using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Forge.Core;
using Forge.Core.Cloud;
using Forge.Core.Entitlements;
using Forge.Hub.ViewModels;
using Forge.Hub.Views;
#if !HUB044
using Forge.Core.Licence;
using Forge.Licence.Testing;
#endif

namespace Forge.Hub.Snapshots;

/// <summary>
/// The plugin rows of the 0.4.x Hub's main window, from fixed data, drawn headless by Skia into PNGs.
///
///     dotnet run --project tests/Forge.Hub.Snapshots -- &lt;out dir&gt; [&lt;scratch dir&gt;] [name filter...]
///
/// Each scenario has its own data folder (FORGE_DATA_DIR) holding settings (no update check), the repository's manifest
/// as the cached copy, an empty releases cache, the account catalogue's answer as its cache, receipts, and a licence
/// signed with a THROWAWAY key (FORGE_LICENCE_DEV_KEYS, read only by a Debug build). Every address is a closed local
/// port, so nothing leaves the machine. The sign-in is a stand-in put into the view model by reflection (the stored
/// sign-in is never read or written), and what the account holds is set the same way, as the account service's
/// answers would set it. Engines are this machine's, found read-only; receipts point at its 5.8 engine's plugin root
/// but at folders in the scratch directory, so nothing in the engine is touched.
///
/// Built with HUB044 defined (against 0.4.4's code), the licence scenarios are left out.
/// </summary>
internal static class Program
{
    private const string Uid = "snapshot-account-0001";
    private static string _out = "";
    private static string _scratch = "";
    private static string[] _only = [];
    private static string _repo = "";
#if !HUB044
    private static TestLicenceKey _key = null!;
#endif

    private const string OffersWithTiers = """
        { "payments": "stripe", "plugins": [
          { "id": "SkinForge", "name": "SkinForge", "price": { "amount": 4999, "currency": "usd" }, "forSale": true, "released": true,
            "tier": "lite", "tierSince": "0.4.0", "includedWith": null, "versions": [] },
          { "id": "TerrainForge", "name": "TerrainForge", "price": { "amount": 9999, "currency": "usd" }, "forSale": true, "released": true,
            "tier": "lite", "tierSince": "0.2.0", "includedWith": null, "versions": [] },
          { "id": "PerformanceForge", "price": { "amount": 7999, "currency": "usd" }, "forSale": true, "released": true, "tier": null } ] }
        """;

    /// <summary>An account API older than the free-tier fields: no tier, no tierSince, no versions.</summary>
    private const string OffersOld = """
        { "payments": "stripe", "plugins": [
          { "id": "SkinForge", "price": { "amount": 4999, "currency": "usd" }, "forSale": true, "released": true },
          { "id": "TerrainForge", "price": { "amount": 9999, "currency": "usd" }, "forSale": true, "released": true },
          { "id": "PerformanceForge", "price": { "amount": 7999, "currency": "usd" }, "forSale": true, "released": true } ] }
        """;

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length < 1) { Console.Error.WriteLine("usage: <out dir> [<scratch dir>] [name filter...]"); return 2; }
        _out = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(_out);
        _scratch = Path.GetFullPath(args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "forge-hub-snapshots"));
        _only = args.Skip(2).ToArray();
        if (Directory.Exists(_scratch)) Directory.Delete(_scratch, true);
        Directory.CreateDirectory(_scratch);
        _repo = FindRepo();

        // Nothing reachable: the manifest, the releases, the hub's releases, the account app, its API, Firebase.
        Environment.SetEnvironmentVariable("FORGE_MANIFEST_URL", "http://127.0.0.1:9/manifest.json");
        Environment.SetEnvironmentVariable("FORGE_RELEASES_API", "http://127.0.0.1:9/releases");
        Environment.SetEnvironmentVariable("FORGE_HUB_RELEASES_API", "http://127.0.0.1:9/hub-releases");
        Environment.SetEnvironmentVariable("FORGE_ACCOUNT_URL", "http://127.0.0.1:9");
        Environment.SetEnvironmentVariable("FORGE_API_URL", "http://127.0.0.1:9/api");
        Environment.SetEnvironmentVariable("FORGE_FIREBASE_EMULATOR", "127.0.0.1:9");
        Environment.SetEnvironmentVariable("FORGE_FIRESTORE_EMULATOR", "127.0.0.1:9");
#if !HUB044
        _key = TestLicenceKey.Generate("dev1");
        Environment.SetEnvironmentVariable(LicenceKeys.DevKeysVariable, _key.DevKeysValue);
#endif

        AppBuilder.Configure<App>()
            .UseSkia()
            .WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();

        Scenarios();
        Console.WriteLine($"done: {_out}");
        if (_problems > 0) Console.WriteLine($"{_problems} layout problem(s)");
        return _problems > 0 ? 1 : 0;
    }

    private static int _problems;

    /// <summary>
    /// Every visible plugin row: no part outside the row, no button outside the window, the name not squeezed below the
    /// panel's minimum, the status not clipped, nothing overlapping.
    /// </summary>
    private static void CheckRows(Window window, string shot)
    {
#if !HUB044
        void Problem(string row, string what) { _problems++; Console.WriteLine($"  LAYOUT PROBLEM in {shot}, {row}: {what}"); }
        foreach (var p in window.GetVisualDescendants().OfType<PluginRowPanel>().Where(x => x.IsEffectivelyVisible))
        {
            var row = (p.DataContext as PluginRow)?.Id ?? "?";
            var w = p.Bounds.Width;
            var (name, status, actions) = (p.Children[0], p.Children[1], p.Children[2]);
            foreach (var c in p.Children.Where(c => c.IsVisible))
                if (c.Bounds.X < -0.5 || c.Bounds.Right > w + 0.5) Problem(row, $"a part at {c.Bounds} outside the row's width {w:0}");
            if (p.TranslatePoint(new Point(w, 0), window) is { } edge && edge.X > window.Bounds.Width + 0.5) Problem(row, "the row runs past the window");
            if (status.DesiredSize.Width > status.Bounds.Width + 0.5) Problem(row, $"status clipped ({status.DesiredSize.Width:0} > {status.Bounds.Width:0})");
            if (!p.IsStacked)
            {
                if (name.Bounds.Width < p.MinNameWidth - 0.5) Problem(row, $"name squeezed to {name.Bounds.Width:0}");
                if (name.Bounds.Right > status.Bounds.X + 0.5 || status.Bounds.Right > actions.Bounds.X + 0.5) Problem(row, "parts overlap");
            }
            else if (name.Bounds.Right > status.Bounds.X + 0.5 || actions.Bounds.Y < Math.Max(name.Bounds.Bottom, status.Bounds.Bottom) - 0.5)
                Problem(row, "parts overlap (stacked)");
            foreach (var b in actions.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible))
                if (b.TranslatePoint(new Point(b.Bounds.Width, 0), p) is { } right && right.X > w + 0.5)
                    Problem(row, $"button '{b.Content}' past the row's edge");
            Console.WriteLine($"    row {row}: {(p.IsStacked ? "two lines" : "one line")}, name {name.Bounds.Width:0}, status {status.Bounds.Width:0}, buttons {actions.Bounds.Width:0} of {w:0}");
        }
#endif
    }

    private static void Scenarios()
    {
        var now = DateTimeOffset.UtcNow;
        var bothPaid = new[] { "SkinForge", "TerrainForge" };

        // A paid product with no free tier, and free plugins: must read exactly as in 0.4.4 (also built against 0.4.4).
        Scenario("notier_not_owned", ["performanceforge"], offers: OffersWithTiers, owned: [], licensed: [], receipts: []);
        Scenario("notier_owned", ["performanceforge"], offers: OffersWithTiers, owned: ["PerformanceForge"], licensed: ["PerformanceForge"],
            receipts: [("PerformanceForge", "0.1.2")]);
        // An account API older than the new fields: everything as in 0.4.4 (a paid plugin installs only when held).
        Scenario("oldapi_not_owned", ["skinforge", "terrainforge"], offers: OffersOld, owned: [], licensed: null, receipts: []);
        Scenario("oldapi_owned", ["skinforge", "terrainforge"], offers: OffersOld, owned: bothPaid, licensed: null, receipts: [("SkinForge", "0.4.0")]);
        Scenario("signed_out", ["skinforge"], offers: OffersWithTiers, owned: [], licensed: null, receipts: [], signedIn: false);
        // A free plugin one version behind, with release notes: Remove, What's new and Update (0.4.4's widest row).
        Scenario("free_update_notes", ["skinforge"], offers: OffersOld, owned: [], licensed: null, receipts: [("SkinForgeToolset", "0.3.0")],
            toolsetNotes: true);

#if !HUB044
        static Dictionary<string, (string how, string since, int maxMajor)> Products(string how) => new()
        {
            ["SkinForge"] = (how, "2026-11-20T18:05:00Z", 1),
            ["TerrainForge"] = (how, "2026-11-20T18:05:00Z", 1),
        };
        var none = new Dictionary<string, (string, string, int)>();

        Scenario("lite_not_installed", ["skinforge", "terrainforge"], offers: OffersWithTiers, owned: [], licensed: [], receipts: [],
            licence: (now.AddHours(-1), none));
        Scenario("lite_update", ["skinforge", "terrainforge"], offers: OffersWithTiers, owned: [], licensed: [],
            receipts: [("SkinForge", "0.4.0"), ("TerrainForge", "0.2.0")], licence: (now.AddHours(-1), none), newerBuilds: true);
        Scenario("licensed_bought", ["skinforge", "terrainforge"], offers: OffersWithTiers, owned: bothPaid, licensed: bothPaid,
            receipts: [("SkinForge", "0.4.0")], licence: (now.AddMinutes(-5), Products("bought")));
        Scenario("licensed_gifted", ["skinforge", "terrainforge"], offers: OffersWithTiers, owned: bothPaid, licensed: bothPaid,
            receipts: [("SkinForge", "0.4.0")], licence: (now.AddMinutes(-5), Products("gifted")));
        // Offline: the account service cannot be reached, so what it holds reads as nothing (LoadOwnedAsync's catch) and
        // `licensed` is unknown; the licence on disk was issued 25 days ago and has 5 days left.
        Scenario("offline_5_days", ["skinforge", "terrainforge"], offers: OffersWithTiers, owned: [], licensed: null,
            receipts: [("SkinForge", "0.4.0"), ("TerrainForge", "0.2.0")], licence: (now.AddDays(-25), Products("bought")),
            ownedLine: "No connection could be made because the target machine actively refused it. (127.0.0.1:9)");
        // The same licence, but the account service answered (it lists both as licensed).
        Scenario("offline_5_days_account_answered", ["skinforge"], offers: OffersWithTiers, owned: bothPaid, licensed: bothPaid,
            receipts: [("SkinForge", "0.4.0")], licence: (now.AddDays(-25), Products("bought")));
        // Every build below the floor: only 0.1.0 / 0.3.0 for this engine, the free tier starting at 0.2.0 / 0.4.0.
        Scenario("below_floor", ["skinforge", "terrainforge"], offers: OffersWithTiers, owned: [], licensed: [], receipts: [],
            licence: (now.AddHours(-1), none), belowFloor: true);
#endif
    }

    private static void Scenario(string name, string[] sets, string offers, string[] owned, string[]? licensed,
        (string id, string version)[] receipts, bool signedIn = true, string? ownedLine = null, bool newerBuilds = false, bool belowFloor = false,
        bool toolsetNotes = false
#if !HUB044
        , (DateTimeOffset issuedAt, Dictionary<string, (string how, string since, int maxMajor)> products)? licence = null
#endif
        )
    {
        if (_only.Length > 0 && !_only.Any(o => name.Contains(o, StringComparison.OrdinalIgnoreCase))) return;
        Console.WriteLine($"[{name}]");
        var data = Path.Combine(_scratch, name, "data");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "settings.json"),
            """{ "channel": "stable", "checkForUpdates": false, "runInBackground": false, "notifyOnUpdates": false }""");
        File.WriteAllText(Path.Combine(data, "releases.json"), "[]");
        File.WriteAllText(Path.Combine(data, "hub-releases.json"), "[]");
        File.WriteAllText(Path.Combine(data, "manifest.json"), Manifest(newerBuilds, belowFloor, toolsetNotes));
        File.WriteAllText(Path.Combine(data, "account-catalogue.json"), offers);
        Environment.SetEnvironmentVariable("FORGE_DATA_DIR", data);

#if !HUB044
        if (licence is { } lic)
        {
            var sub = LicencePayload.SubjectFor(Uid);
            File.WriteAllText(Path.Combine(data, LicenceStore.FileName), _key.Envelope(TestLicenceKey.Payload(sub, lic.issuedAt, lic.products)));
            File.WriteAllText(Path.Combine(data, LicenceStore.CheckFileName), JsonSerializer.Serialize(new
            {
                lastSuccessAt = lic.issuedAt, lastServerAnswerAt = DateTimeOffset.UtcNow, lastAttemptAt = DateTimeOffset.UtcNow,
                lastOutcome = lic.issuedAt < DateTimeOffset.UtcNow.AddDays(-1) ? "offline" : "updated", sub = Uid,
            }));
        }
#endif

        var settings = Settings.Load();
        var vm = new MainViewModel(settings, null);
        var window = new MainWindow { DataContext = vm, Width = 1180, Height = 760 };
        window.Show();
        Pump(1500);
        // A refused connection takes Windows about two seconds per address: wait for the start's load to finish.
        for (var i = 0; i < 80 && (vm.SourceStamp.Length == 0 || vm.Busy); i++) Pump(250);
        Console.WriteLine($"  status '{vm.Status}' · {vm.SourceStamp} · engines {string.Join("; ", vm.Engines)}");

        // This machine's 5.8 engine (the manifest's builds are for 5.8), and receipts on it pointing at scratch folders.
        var engine = vm.Engines.FirstOrDefault(e => e.Version == "5.8") ?? vm.Engines.FirstOrDefault();
        if (engine is null) { Console.WriteLine("  no engine on this machine: nothing to show"); window.Close(); return; }
        var items = receipts.Select(r =>
        {
            var dir = Path.Combine(_scratch, name, "installed", r.id);
            Directory.CreateDirectory(dir);
            return new { plugin = r.id, version = r.version, engine = "5.8", channel = "stable", targetKind = "engine",
                         targetRoot = engine.PluginRoot, path = dir, installedAt = "2026-11-01T10:00:00Z" };
        }).ToList();
        File.WriteAllText(Path.Combine(data, "installed.json"), JsonSerializer.Serialize(items));
        Reload(vm);

        if (signedIn) SignIn(vm, owned, licensed, ownedLine);
#if !HUB044
        Set(vm, "_offers", AccountCatalogue.Parse(offers));
        Set(vm, "_licenceSnapshot", null);
#endif
        vm.SelectedEngine = engine;
        Call(vm, "Rebuild");
        Pump(400);

        foreach (var setId in sets)
        {
            var set = vm.Sets.FirstOrDefault(s => s.Id == setId);
            if (set is null) { Console.WriteLine($"  no set {setId}"); continue; }
            vm.SelectedSet = set;
            foreach (var row in set.Plugins)
                Console.WriteLine($"  {row.Id,-22} '{row.VersionLine}' · status '{row.Status}' · button '{row.ActionLabel}' (can {row.CanAct})"
                                  + RowExtras(row));
            foreach (var (w, h, suffix) in new[] { (1180, 760, "default"), (760, 480, "min") })
            {
                window.Width = w;
                window.Height = h;
                Pump(600);
                Shot(window, $"{name}__{setId}__{suffix}");
            }
            window.Width = 1180;
            window.Height = 760;
        }
        window.Close();
        Pump(100);
    }

    private static string RowExtras(PluginRow row)
    {
#if HUB044
        return "";
#else
        return $" · buyFull {row.CanBuyFull} · licence '{row.LicenceLine}' · restore {row.CanRestoreLicence} · tip '{row.StatusTip}'";
#endif
    }

    /// <summary>The repository's manifest; with a newer free-tier build of each, or with only builds below each floor.</summary>
    private static string Manifest(bool newerBuilds, bool belowFloor, bool toolsetNotes)
    {
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(_repo, "manifest.json")))!;
        foreach (var p in manifest["plugins"]!.AsArray())
        {
            var id = p!["id"]!.GetValue<string>();
            if (toolsetNotes && id == "SkinForgeToolset")
            {
                foreach (var v in p["versions"]!.AsArray()) v!["notes"] = "https://example.invalid/skinforgetoolset/notes";
                p["versions"]!.AsArray().Add(JsonNode.Parse("""
                    { "version": "0.3.0", "engine": "5.8", "platform": "Win64", "channel": "stable", "sha256": null, "size": 100000,
                      "releasedAt": "2026-10-01T10:00:00Z", "url": null, "symbols": null, "notes": null }
                    """));
            }
            if (id is not ("SkinForge" or "TerrainForge")) continue;
            var versions = p["versions"]!.AsArray();
            var add = (newerBuilds, belowFloor, id) switch
            {
                (true, _, "SkinForge") => "0.4.1",
                (true, _, "TerrainForge") => "0.2.1",
                (_, true, "SkinForge") => "0.3.0",
                (_, true, "TerrainForge") => "0.1.0",
                _ => null,
            };
            if (add is null) continue;
            if (belowFloor) versions.Clear();
            versions.Add(JsonNode.Parse($$"""
                { "version": "{{add}}", "engine": "5.8", "platform": "Win64", "channel": "stable", "sha256": null, "size": 9400000,
                  "releasedAt": "{{(belowFloor ? "2026-10-01T10:00:00Z" : "2026-11-21T10:00:00Z")}}", "url": null, "symbols": null, "notes": null }
                """));
        }
        return manifest.ToJsonString();
    }

    // ── the stand-ins ────────────────────────────────────────────────────

    /// <summary>A signed-in account as the window would hold it after GET users/{uid} and GET me answered.</summary>
    private static void SignIn(MainViewModel vm, string[] owned, string[]? licensed, string? ownedLine)
    {
        var ent = Get(vm, "_entitlements")!;
        Set(ent, "_account", new StoredAccount(Uid, "snapshot@example.invalid", "not-a-token") { DisplayName = "Snapshot Account" });
        var ownedSet = new HashSet<string>(owned, StringComparer.OrdinalIgnoreCase);
        var licensedSet = licensed is null ? null : new HashSet<string>(licensed, StringComparer.OrdinalIgnoreCase);
        Set(ent, "_owned", ownedSet);
        Set(ent, "_licensed", licensedSet);
        Set(ent, "_licensedRead", true);
        Set(vm, "_owned", ownedSet);
        Set(vm, "_licensed", licensedSet);
        Set(vm, "_heldFor", Uid);
        vm.AccountName = "Snapshot Account";
        vm.AccountEmail = "snapshot@example.invalid";
        vm.OwnedLine = ownedLine ?? "";
        Call(vm, "OnPropertyChanged", nameof(MainViewModel.IsSignedIn));
    }

    private static void Reload(MainViewModel vm)
    {
        var state = (Forge.Core.Installs.InstallState)Get(vm, "_state")!;
        state.Reload();
    }

    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    private static object? Get(object o, string field) => o.GetType().GetField(field, Any)?.GetValue(o);

    /// <summary>Sets a private field; a field this build does not have (0.4.4) is skipped.</summary>
    private static void Set(object o, string field, object? value)
    {
        var f = o.GetType().GetField(field, Any);
        if (f is null) { Console.WriteLine($"  (no field {o.GetType().Name}.{field} in this build)"); return; }
        f.SetValue(o, value);
    }

    private static void Call(object o, string method, params object[] args)
    {
        var m = o.GetType().GetMethods(Any).FirstOrDefault(x => x.Name == method && x.GetParameters().Length == args.Length
                    && x.GetParameters().Select(p => p.ParameterType).SequenceEqual(args.Select(a => a.GetType())))
                ?? throw new MissingMethodException(o.GetType().Name, method);
        m.Invoke(o, args);
    }

    // ── drawing ──────────────────────────────────────────────────────────

    private static void Pump(int ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        do
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(15);
        } while (DateTime.UtcNow < until);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Shot(Window window, string name)
    {
        Pump(500);
        var frame = window.CaptureRenderedFrame();
        var file = Path.Combine(_out, name + ".png");
        if (frame is null) { Console.WriteLine($"  NO FRAME for {file}"); return; }
        frame.Save(file);
        Console.WriteLine($"  wrote {file} ({frame.PixelSize.Width}x{frame.PixelSize.Height})");
        CheckRows(window, name);
    }

    private static string FindRepo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Forge.sln"))) dir = dir.Parent;
        return dir?.FullName ?? Environment.GetEnvironmentVariable("FORGE_REPO") ?? throw new InvalidOperationException("Run from inside the repository, or set FORGE_REPO.");
    }
}
