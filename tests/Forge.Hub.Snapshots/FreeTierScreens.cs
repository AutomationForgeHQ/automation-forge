using System.Text.Json;
using System.Text.Json.Nodes;
using Forge.Core.Licence;
using Forge.Hub.ViewModels.Pages;
using Forge.Licence.Testing;

namespace Forge.Hub.Snapshots;

/// <summary>
/// A paid product's free tier (0.5.0-beta.7), from fixed data: TerrainForge 0.1.0 and 0.2.0 in a copy of the manifest,
/// the account service's offer with `tier: lite` from 0.2.0 as its cached answer, and a licence signed with a THROWAWAY
/// key (FORGE_LICENCE_DEV_KEYS, a Debug build) in the scenario's own data folder. Nothing of this machine's sign-in or
/// data folder is read or written.
///
///     dotnet run --project tests/Forge.Hub.Snapshots -- &lt;out dir&gt; freetier
///
/// The product page in three states (free tier not installed, installed as Lite, licensed), each at the default width
/// and at the narrowest the window allows, and the worldbuilding track with TerrainForge going in as Lite.
/// </summary>
internal static partial class Screens
{
    private const string FreeTierOffers = """
        { "payments": "stripe", "plugins": [
          { "id": "TerrainForge", "name": "TerrainForge", "price": { "amount": 9999, "currency": "usd" }, "forSale": true, "released": true,
            "tier": "lite", "tierSince": "0.2.0", "includes": [], "includedWith": null,
            "versions": [
              { "version": "0.1.0", "engine": "5.8", "platform": "Win64", "channel": "stable", "freeTier": false },
              { "version": "0.2.0", "engine": "5.8", "platform": "Win64", "channel": "stable", "freeTier": true } ] },
          { "id": "MeshForgeGarment", "price": { "amount": 7999, "currency": "usd" }, "forSale": true, "released": true, "tier": null },
          { "id": "PerformanceForge", "price": { "amount": 7999, "currency": "usd" }, "forSale": true, "released": true, "tier": null },
          { "id": "SpeechForgeDeepL", "price": { "amount": 4999, "currency": "usd" }, "forSale": true, "released": true, "tier": null } ] }
        """;

    public static void RunFreeTier(string outDir, string[] only)
    {
        _out = outDir;
        _only = only;
        _scratch = Path.Combine(Path.GetTempPath(), "kovati-hub-snapshots-freetier");
        if (Directory.Exists(_scratch)) Directory.Delete(_scratch, true);
        Directory.CreateDirectory(_scratch);
        var repo = FindRepo();

        // The manifest with TerrainForge 0.2.0, the first build that enforces Lite, beside 0.1.0.
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(repo, "manifest.json")))!;
        foreach (var p in manifest["plugins"]!.AsArray())
            if (p!["id"]!.GetValue<string>() == "TerrainForge")
                p["versions"]!.AsArray().Add(JsonNode.Parse("""
                    { "version": "0.2.0", "engine": "5.8", "platform": "Win64", "channel": "stable", "sha256": null, "size": 9400000,
                      "releasedAt": "2026-11-20T10:00:00Z", "url": null, "symbols": null, "notes": null }
                    """));
        var manifestPath = Path.Combine(_scratch, "manifest-freetier.json");
        File.WriteAllText(manifestPath, manifest.ToJsonString());

        Environment.SetEnvironmentVariable("FORGE_MANIFEST_URL", manifestPath);
        Environment.SetEnvironmentVariable("FORGE_RELEASES_API", "off");
        Environment.SetEnvironmentVariable("FORGE_CATALOGUE_URL", "off");
        Environment.SetEnvironmentVariable("FORGE_SITE_URL", "http://127.0.0.1:9");
        Environment.SetEnvironmentVariable("FORGE_API_URL", "http://127.0.0.1:9/api");
        Environment.SetEnvironmentVariable("FORGE_ACCOUNT_URL", "http://127.0.0.1:9");
        Environment.SetEnvironmentVariable("FORGE_UNREAL_RECORDS", "off");
        var site = Path.Combine(Path.GetDirectoryName(repo)!, "AutomationForge-website", "v2", "public");
        if (Directory.Exists(site)) Environment.SetEnvironmentVariable("FORGE_MEDIA_URL", site);

        var key = TestLicenceKey.Generate("dev1");
        Environment.SetEnvironmentVariable(LicenceKeys.DevKeysVariable, key.DevKeysValue);
        var now = DateTimeOffset.UtcNow;
        var none = new Dictionary<string, (string how, string since, int maxMajor)>();
        var bought = new Dictionary<string, (string how, string since, int maxMajor)> { ["TerrainForge"] = ("bought", "2026-11-20T18:05:00Z", 1) };

        FreeTierScenario("ft_lite_not_installed", receipts: [], owned: [], licensed: [], envelope: null, key, now);
        FreeTierScenario("ft_lite_installed", receipts: [("TerrainForge", "0.2.0")], owned: [], licensed: [], key.Envelope(LicSub, now.AddHours(-1), none), key, now);
        FreeTierScenario("ft_licensed", receipts: [("TerrainForge", "0.2.0")], owned: ["TerrainForge"], licensed: ["TerrainForge"], key.Envelope(LicSub, now.AddMinutes(-5), bought), key, now);

        Environment.SetEnvironmentVariable(LicenceKeys.DevKeysVariable, null);
        Environment.SetEnvironmentVariable("FORGE_MEDIA_URL", null);
        Console.WriteLine($"done: {outDir}");
    }

    private static void FreeTierScenario(string name, (string id, string version)[] receipts, string[] owned, string[] licensed,
        string? envelope, TestLicenceKey key, DateTimeOffset now)
    {
        var (window, vm) = Open(name, receipts: [("AutomationForgeHub", "0.2.5"), ("AutomationForge", "0.1.1"), .. receipts], prepare: (_, data) =>
        {
            File.WriteAllText(Path.Combine(data, "account-catalogue.json"), FreeTierOffers);
            if (envelope is null) return;
            File.WriteAllText(Path.Combine(data, LicenceStore.FileName), envelope);
            File.WriteAllText(Path.Combine(data, LicenceStore.CheckFileName), JsonSerializer.Serialize(new
            {
                lastSuccessAt = now, lastServerAnswerAt = now, lastAttemptAt = now, lastOutcome = "updated", sub = LicUid,
            }));
        });
        vm.PreviewAccount("Bojan Andrejek", "bojan@kovati.dev", owned, licensed);
        vm.PreviewLicenceAccount(LicUid);
        Snap.Pump(400);

        foreach (var (width, suffix) in new[] { (1180, ""), (1040, "_narrow") })
        {
            window.Width = width;
            vm.GoHome();
            vm.OpenProduct("terrainforge");
            Snap.Pump(800);
            var page = (ProductPageViewModel)vm.CurrentPage!;
            Console.WriteLine($"  {name}{suffix}: chips [{string.Join(", ", page.Chips.Select(c => c.Text))}] · button '{page.ButtonText}' enabled={page.ButtonEnabled}"
                              + $" · buy '{page.BuyFullText}' shown={page.HasBuyFull} · line '{page.ButtonLine}' · licence '{page.LicenceLine}' · version {page.VersionLine}");
            Shot(window, $"hub_{name}_product{suffix}");
        }

        if (name == "ft_lite_not_installed")
        {
            window.Width = 1180;
            var track = OpenTrack(vm, "worldbuilding");
            var row = track.Rows.FirstOrDefault(r => r.Capability.Id == "terrain");
            Console.WriteLine($"  track: terrain row chips [{string.Join(", ", row?.Chips.Select(c => c.Text) ?? [])}] ticked={row?.IsTicked} · {track.CurrentPlan?.Summary}"
                              + $" · {string.Join(" ", track.CurrentPlan?.Items.Where(i => i.IsFreeTier).Select(i => $"{i.Id} {i.Version} {i.FreeTier}") ?? [])}");
            Shot(window, $"hub_{name}_track_worldbuilding");
        }
        window.Close();
    }
}
