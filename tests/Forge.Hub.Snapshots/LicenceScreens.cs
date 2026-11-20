using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Forge.Core.Licence;
using Forge.Hub.ViewModels;
using Forge.Hub.ViewModels.Pages;
using Forge.Licence.Testing;

namespace Forge.Hub.Snapshots;

/// <summary>
/// The licence (licensing step 2), from fixed data: a licence signed with a THROWAWAY key made for this run, trusted
/// through FORGE_LICENCE_DEV_KEYS (this is a Debug build), written into the scenario's own data folder. Nothing of this
/// machine's sign-in or data folder is read or written.
///
///     dotnet run --project tests/Forge.Hub.Snapshots -- &lt;out dir&gt; licence
///
/// Two halves: today's catalogue (no free tier is live: only "Licensed" lines), and the same screens with a copy of
/// the catalogue in which TerrainForge's Lite is marked live, to show the words that wait for it.
/// </summary>
internal static partial class Screens
{
    private const string LicUid = "0123456789abcdef0123456789abcdef";
    private static readonly string LicSub = LicencePayload.SubjectFor(LicUid);

    public static void RunLicence(string outDir, string[] only)
    {
        _out = outDir;
        _only = only;
        _scratch = Path.Combine(Path.GetTempPath(), "kovati-hub-snapshots-lic");
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

        var key = TestLicenceKey.Generate("dev1");
        Environment.SetEnvironmentVariable(LicenceKeys.DevKeysVariable, key.DevKeysValue);
        Console.WriteLine($"  dev keys allowed: {LicenceKeys.DevKeysAllowed}");

        var now = DateTimeOffset.UtcNow;
        var products = new Dictionary<string, (string how, string since, int maxMajor)>
        {
            ["TerrainForge"] = ("bought", "2026-11-20T09:12:00Z", 1),
            ["PerformanceForge"] = ("test", "2026-11-18T16:40:00Z", 1),
        };
        LicenceScenario("lic_today", key, key.Envelope(LicSub, now.AddHours(-2), products), now.AddHours(-2), catalogue: null);
        LicenceScenario("lic_offline_today", key, key.Envelope(LicSub, now.AddDays(-25), products), now.AddDays(-25), catalogue: null);

        // The words that wait for a live free tier: a copy of the catalogue with TerrainForge's Lite marked live.
        var cat = JsonNode.Parse(File.ReadAllText(Path.Combine(repo, "src", "Forge.Hub", "catalogue", "catalogue.json")))!;
        foreach (var p in cat["products"]!.AsArray())
            if (p!["slug"]!.GetValue<string>() is "terrainforge" or "skinforge") p["freeTierLive"] = true;
        var liveCat = Path.Combine(_scratch, "catalogue-lite-live.json");
        File.WriteAllText(liveCat, cat.ToJsonString());
        LicenceScenario("lic_lite_offline", key, key.Envelope(LicSub, now.AddDays(-25), products), now.AddDays(-25), liveCat);
        LicenceScenario("lic_lite_expired", key, key.Envelope(LicSub, now.AddDays(-33), products), now.AddDays(-33), liveCat);
        LicenceScenario("lic_lite_not_licensed", key, key.Envelope(LicSub, now.AddHours(-1), new Dictionary<string, (string, string, int)>()), now.AddHours(-1), liveCat);
        Environment.SetEnvironmentVariable("FORGE_CATALOGUE", null);
        Environment.SetEnvironmentVariable(LicenceKeys.DevKeysVariable, null);
        Console.WriteLine($"done: {outDir}");
    }

    private static void LicenceScenario(string name, TestLicenceKey key, string envelope, DateTimeOffset refreshed, string? catalogue)
    {
        Environment.SetEnvironmentVariable("FORGE_CATALOGUE", catalogue);
        var (window, vm) = Open(name, receipts:
        [
            ("AutomationForgeHub", "0.2.5"), ("AutomationForge", "0.1.1"), ("TerrainForge", "0.1.0"), ("PerformanceForge", "0.1.2"),
        ], prepare: (_, data) =>
        {
            File.WriteAllText(Path.Combine(data, LicenceStore.FileName), envelope);
            File.WriteAllText(Path.Combine(data, LicenceStore.CheckFileName), JsonSerializer.Serialize(new
            {
                lastSuccessAt = refreshed, lastServerAnswerAt = refreshed, lastAttemptAt = refreshed, lastOutcome = "updated", sub = LicUid,
            }));
        });
        vm.PreviewAccount("Bojan Andrejek", "bojan@kovati.dev", ["TerrainForge", "PerformanceForge", "SkinForge"]);
        vm.PreviewLicenceAccount(LicUid);
        Snap.Pump(400);
        var snap = vm.LicenceSnapshot;
        Console.WriteLine($"  {name}: licence {(snap.Payload is null ? "NOT READ" : $"for {snap.Payload.Sub}, {snap.Payload.Products.Count} products, dev key {snap.Licence!.SignedWithDevKey}")}");

        foreach (var slug in new[] { "terrainforge", "performanceforge", "skinforge" })
        {
            vm.GoHome();
            vm.OpenProduct(slug);
            Snap.Pump(700);
            var page = (ProductPageViewModel)vm.CurrentPage!;
            Console.WriteLine($"    {slug}: '{page.LicenceLine}' restore={page.CanRestoreLicence}");
            if (slug != "skinforge" || catalogue is not null) Shot(window, $"hub_{name}_product_{slug}");
        }

        vm.ShowLibrary();
        var lib = (LibraryPageViewModel)vm.CurrentPage!;
        lib.Tab = LibraryTab.Installed;
        Snap.Pump(600);
        Console.WriteLine($"    library: {string.Join(" | ", lib.InstalledGroups.SelectMany(g => g.Rows).Where(r => r.HasLicenceLine).Select(r => $"{r.Name}: {r.LicenceLine}"))}");
        Shot(window, $"hub_{name}_library");

        vm.ToggleSettingsCommand.Execute(null);
        Snap.Pump(400);
        var drawer = window.GetVisualDescendants().OfType<ScrollViewer>()
            .Where(s => s.IsEffectivelyVisible && s.Bounds.Width is > 300 and < 460).OrderByDescending(s => s.Bounds.Height).FirstOrDefault();
        if (drawer is not null) { drawer.Offset = new Avalonia.Vector(0, 640); Snap.Pump(300); }
        Console.WriteLine($"    settings: {vm.LicenceAccountLine} · {vm.LicenceCoversLine} · {vm.LicenceRefreshedLine} · {vm.LicenceValidLine} · {vm.LicenceKeyLine}");
        Shot(window, $"hub_{name}_settings");
        vm.ClosePanels();
        window.Close();
    }
}
