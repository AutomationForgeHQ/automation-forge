using System.Text.Json;
using Forge.Core.Catalogue;
using Forge.Core.Cloud;
using Forge.Core.Licence;
using Xunit;

namespace Forge.Core.Tests;

/// <summary>
/// The free-tier rule against the account API's real answer: GET https://app.kovati.dev/api/catalogue as it was on
/// 20 November 2026 (Fixtures/live-catalogue-2026-11-20.json, trimmed to SkinForge, TerrainForge and PerformanceForge,
/// otherwise as served), read by this build's reader, with the manifest's builds of the same day
/// (Fixtures/manifest-2026-11-20.json, those three plugins, notes left out).
/// </summary>
public class LiveCatalogueTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static readonly AccountCatalogue Offers = AccountCatalogue.Parse(Fixture("live-catalogue-2026-11-20.json"))!;

    private static readonly Manifest.Manifest Builds =
        JsonSerializer.Deserialize<Manifest.Manifest>(Fixture("manifest-2026-11-20.json"), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    private static AccessInputs Account(IEnumerable<string>? owned = null, IEnumerable<string>? licensed = null) => new()
    {
        Engine = "5.8",
        Channel = Settings.Stable,
        Owned = new HashSet<string>(owned ?? [], StringComparer.OrdinalIgnoreCase),
        Licensed = licensed is null ? null : new HashSet<string>(licensed, StringComparer.OrdinalIgnoreCase),
        Offers = Offers,
    };

    [Fact]
    public void The_live_answer_reads_with_its_free_tiers()
    {
        Assert.True(Offers.PaymentsLive);
        var skin = Offers.Plugin("SkinForge")!;
        var terrain = Offers.Plugin("TerrainForge")!;
        Assert.Equal(("lite", "0.4.0"), (skin.FreeTier, skin.TierSince));
        Assert.Equal(("lite", "0.2.0"), (terrain.FreeTier, terrain.TierSince));
        Assert.True(skin.Versions.Single(v => v.Version == "0.4.0").FreeTier);
        Assert.True(terrain.Versions.Single(v => v.Version == "0.2.0").FreeTier);
        Assert.Null(Offers.Plugin("PerformanceForge")!.FreeTier);
    }

    [Theory]
    [InlineData(false)] // GET me answered: nothing licensed
    [InlineData(true)]  // GET me without `licensed` (or not reachable): holding counts, and nothing is held
    public void An_account_with_nothing_installs_SkinForge_and_TerrainForge_as_Lite_and_not_PerformanceForge(bool meSilent)
    {
        var account = meSilent ? Account() : Account(licensed: []);
        var skin = Builds.Plugin("SkinForge")!;
        var terrain = Builds.Plugin("TerrainForge")!;
        var performance = Builds.Plugin("PerformanceForge")!;

        Assert.Equal(PaidAccess.FreeTier, FreeTiers.AccessOf(skin, account));
        Assert.Equal("0.4.0", FreeTiers.BuildFor(skin, account)!.Version);
        Assert.Equal(PaidAccess.FreeTier, FreeTiers.AccessOf(terrain, account));
        Assert.Equal("0.2.0", FreeTiers.BuildFor(terrain, account)!.Version);

        Assert.Equal(PaidAccess.NotOwned, FreeTiers.AccessOf(performance, account));
        Assert.Null(FreeTiers.BuildFor(performance, account));

        // The row the window draws for each.
        Assert.Equal("Install Lite", Row(skin, account).ActionLabel);
        Assert.Equal("Install Lite", Row(terrain, account).ActionLabel);
        Assert.Equal(("paid · not on your account", "Buy"), (Row(performance, account).Status, Row(performance, account).ActionLabel));
    }

    [Fact]
    public void A_licensed_account_gets_the_same_builds_as_Full()
    {
        var account = Account(owned: ["SkinForge", "TerrainForge"], licensed: ["SkinForge", "TerrainForge"]);
        Assert.Equal(PaidAccess.Full, FreeTiers.AccessOf(Builds.Plugin("SkinForge")!, account));
        Assert.Equal("0.4.0", FreeTiers.BuildFor(Builds.Plugin("SkinForge")!, account)!.Version);
        Assert.Equal(PaidAccess.Full, FreeTiers.AccessOf(Builds.Plugin("TerrainForge")!, account));
        Assert.Equal("0.2.0", FreeTiers.BuildFor(Builds.Plugin("TerrainForge")!, account)!.Version);
        Assert.Equal("Install", Row(Builds.Plugin("SkinForge")!, account).ActionLabel);
        Assert.False(Row(Builds.Plugin("SkinForge")!, account).CanBuyFull);
    }

    [Fact]
    public void PerformanceForge_installs_only_when_the_account_holds_it()
    {
        var performance = Builds.Plugin("PerformanceForge")!;
        // Held, GET me answering without it among `licensed` (it has no free tier, so holding it is what counts).
        Assert.Equal(PaidAccess.Full, FreeTiers.AccessOf(performance, Account(owned: ["PerformanceForge"])));
        Assert.Equal("0.1.2", FreeTiers.BuildFor(performance, Account(owned: ["PerformanceForge"]))!.Version);
        Assert.Equal(PaidAccess.Full, FreeTiers.AccessOf(performance, Account(owned: ["PerformanceForge"], licensed: ["PerformanceForge"])));
        // Licensed for something else only: still not installable.
        Assert.Equal(PaidAccess.NotOwned, FreeTiers.AccessOf(performance, Account(owned: ["SkinForge"], licensed: ["SkinForge"])));
    }

    private static PluginRowState Row(Manifest.PluginInfo p, AccessInputs inputs) => new()
    {
        Offered = FreeTiers.BuildFor(p, inputs) ?? p.Latest(inputs.Engine, inputs.Channel),
        SignedIn = true,
        IsPaid = p.IsPaid,
        Owned = inputs.Owned.Contains(p.Id),
        Access = FreeTiers.AccessOf(p, inputs),
        Tier = FreeTiers.TierOf(p.Id, inputs),
        Licence = new ProductLicence(LicenceStanding.NotLicensed),
    };
}
