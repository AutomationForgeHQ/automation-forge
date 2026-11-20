using Forge.Core.Catalogue;
using Forge.Core.Cloud;
using Forge.Core.Entitlements;
using Forge.Core.Licence;
using Forge.Core.Manifest;
using Forge.Licence.Testing;
using Xunit;

namespace Forge.Core.Tests;

/// <summary>
/// A paid plugin's free tier, as the 0.4.5 Hub and CLI apply it: the account API's offer `tier`, `tierSince` and per-build
/// `freeTier`, and GET me `licensed`. Ported from Kovati Hub 0.5.0-beta.7 (tests/Forge.Core.Tests/FreeTierTests.cs) where
/// the rule is the same; the planner and track cases are not here because the 0.4.x Hub has no tracks. TerrainForge as
/// released on 20 November 2026: 0.1.0 (the full tool, no cap, below the floor) and 0.2.0 (the first build that enforces
/// Lite, `tierSince` 0.2.0).
/// </summary>
public class FreeTierTests
{
    internal const string Offers = """
        { "payments": "stripe", "plugins": [
          { "id": "TerrainForge", "name": "TerrainForge", "price": { "amount": 9999, "currency": "usd" }, "forSale": true, "released": true,
            "tier": "lite", "tierSince": "0.2.0", "includedWith": null,
            "versions": [
              { "version": "0.1.0", "engine": "5.8", "platform": "Win64", "channel": "stable", "freeTier": false },
              { "version": "0.2.0", "engine": "5.8", "platform": "Win64", "channel": "stable", "freeTier": true } ] },
          { "id": "PerformanceForge", "price": { "amount": 7999, "currency": "usd" }, "forSale": true, "released": true, "tier": null,
            "versions": [ { "version": "0.1.2", "engine": "5.8", "freeTier": false } ] } ] }
        """;

    /// <summary>The live answer of 20 November 2026 before the free-tier fields: `tier: null`, no `tierSince`, no per-build `freeTier`.</summary>
    internal const string OffersBeforeTiers = """
        { "payments": "stripe", "plugins": [
          { "id": "TerrainForge", "name": "TerrainForge", "price": { "amount": 9999, "currency": "usd" }, "forSale": true, "released": true, "tier": null,
            "includes": [], "includedWith": null, "versions": [ { "version": "0.1.0", "engine": "5.8", "platform": "Win64", "channel": "stable", "releasedAt": "2026-10-29T12:09:11Z" } ] } ] }
        """;

    internal static VersionInfo Build(string version, string releasedAt) =>
        new() { Version = version, Engine = "5.8", Platform = "Win64", Channel = "stable", ReleasedAt = releasedAt };

    /// <summary>A small manifest: TerrainForge (paid, a free tier), PerformanceForge (paid, none), SurfaceForge (free).</summary>
    internal static Manifest.Manifest TestManifest(params VersionInfo[] terrain) => new()
    {
        Plugins =
        [
            new PluginInfo { Id = "TerrainForge", Distribution = "paid", Role = "core",
                Versions = terrain.Length > 0 ? terrain.ToList() : [Build("0.1.0", "2026-10-29T12:09:11Z"), Build("0.2.0", "2026-11-20T10:00:00Z")] },
            new PluginInfo { Id = "PerformanceForge", Distribution = "paid", Role = "core", Versions = [Build("0.1.2", "2026-10-04T21:57:24Z")] },
            new PluginInfo { Id = "SurfaceForge", Distribution = "open", Role = "core", Versions = [Build("0.3.0", "2026-11-01T10:00:00Z")] },
        ],
    };

    internal static AccessInputs Inputs(string? offers = Offers, IEnumerable<string>? owned = null, IEnumerable<string>? licensed = null) => new()
    {
        Engine = "5.8",
        Owned = new HashSet<string>(owned ?? [], StringComparer.OrdinalIgnoreCase),
        Licensed = licensed is null ? null : new HashSet<string>(licensed, StringComparer.OrdinalIgnoreCase),
        Offers = offers is null ? null : AccountCatalogue.Parse(offers),
    };

    // ── the rule ─────────────────────────────────────────────────────────

    [Fact]
    public void A_non_holder_gets_the_newest_free_tier_build()
    {
        var m = TestManifest();
        var inputs = Inputs(licensed: []);
        var tf = m.Plugin("TerrainForge")!;
        Assert.Equal(PaidAccess.FreeTier, FreeTiers.AccessOf(tf, inputs));
        Assert.Equal("0.2.0", FreeTiers.BuildFor(tf, inputs)!.Version);
        Assert.Equal("lite", FreeTiers.TierOf("TerrainForge", inputs));
        // A free plugin is unchanged.
        Assert.Equal(PaidAccess.Free, FreeTiers.AccessOf(m.Plugin("SurfaceForge")!, inputs));
        Assert.Equal("0.3.0", FreeTiers.BuildFor(m.Plugin("SurfaceForge")!, inputs)!.Version);
    }

    [Fact]
    public void Below_the_floor_only_is_not_installable()
    {
        var m = TestManifest(Build("0.1.0", "2026-10-29T12:09:11Z"));
        var inputs = Inputs(licensed: []);
        var tf = m.Plugin("TerrainForge")!;
        Assert.Equal(PaidAccess.NeedsLicence, FreeTiers.AccessOf(tf, inputs));
        Assert.Null(FreeTiers.BuildFor(tf, inputs));
    }

    [Fact]
    public void A_free_tier_never_takes_a_build_below_the_floor_even_when_it_was_released_later()
    {
        // A 0.1.1 hotfix of the uncapped line, published after 0.2.0: newest by date, below the floor.
        var m = TestManifest(Build("0.2.0", "2026-11-20T10:00:00Z"), Build("0.1.1", "2026-11-21T10:00:00Z"));
        var tf = m.Plugin("TerrainForge")!;
        var inputs = Inputs(licensed: []);
        Assert.Equal("0.1.1", tf.Latest("5.8")!.Version);
        Assert.Equal("0.2.0", FreeTiers.BuildFor(tf, inputs)!.Version);
    }

    [Fact]
    public void A_licensed_account_gets_the_newest_build_as_today()
    {
        // 0.3.0 listed by the offer without the free tier (the account service decides per build).
        var offers = Offers.Replace("""{ "version": "0.2.0", "engine": "5.8", "platform": "Win64", "channel": "stable", "freeTier": true } ] },""",
            """{ "version": "0.2.0", "engine": "5.8", "platform": "Win64", "channel": "stable", "freeTier": true }, { "version": "0.3.0", "engine": "5.8", "platform": "Win64", "channel": "stable", "freeTier": false } ] },""");
        Assert.NotEqual(Offers, offers);
        var m = TestManifest(Build("0.1.0", "2026-10-29T12:09:11Z"), Build("0.2.0", "2026-11-20T10:00:00Z"), Build("0.3.0", "2026-12-01T10:00:00Z"));
        var tf = m.Plugin("TerrainForge")!;

        var licensed = Inputs(offers, owned: ["TerrainForge"], licensed: ["TerrainForge"]);
        Assert.Equal(PaidAccess.Full, FreeTiers.AccessOf(tf, licensed));
        Assert.Equal("0.3.0", FreeTiers.BuildFor(tf, licensed)!.Version);

        Assert.Equal("0.2.0", FreeTiers.BuildFor(tf, Inputs(offers, licensed: []))!.Version);
    }

    [Fact]
    public void A_preview_claim_holder_is_a_non_holder_for_a_free_tier()
    {
        // Held (a preview claim), but not by a licensed route: GET me `licensed` leaves it out.
        var tf = TestManifest().Plugin("TerrainForge")!;
        Assert.Equal(PaidAccess.FreeTier, FreeTiers.AccessOf(tf, Inputs(owned: ["TerrainForge"], licensed: [])));
    }

    [Fact]
    public void Without_a_tier_a_paid_plugin_is_as_today()
    {
        var perf = TestManifest().Plugin("PerformanceForge")!;
        Assert.Equal(PaidAccess.NotOwned, FreeTiers.AccessOf(perf, Inputs(licensed: [])));
        Assert.Null(FreeTiers.BuildFor(perf, Inputs(licensed: [])));
        // A holder without a licence and no tier keeps the old rule: it installs.
        Assert.Equal(PaidAccess.Full, FreeTiers.AccessOf(perf, Inputs(owned: ["PerformanceForge"], licensed: [])));
        // 0.4.4 never counted a copy that is merely installed; neither does 0.4.5 (no installed set in the inputs at all).
    }

    [Theory]
    [InlineData(true)]   // the account service's answer before the fields: tier null, no tierSince, no freeTier
    [InlineData(false)]  // no account catalogue read at all
    public void Without_the_fields_everything_is_as_before(bool oldAnswer)
    {
        var offers = oldAnswer ? OffersBeforeTiers : null;
        var tf = TestManifest().Plugin("TerrainForge")!;
        Assert.Equal(PaidAccess.NotOwned, FreeTiers.AccessOf(tf, Inputs(offers)));
        Assert.Null(FreeTiers.BuildFor(tf, Inputs(offers)));
        // Owned, and `licensed` not said (null): owning it is enough, and it gets the newest build.
        var owned = Inputs(offers, owned: ["TerrainForge"]);
        Assert.Equal(PaidAccess.Full, FreeTiers.AccessOf(tf, owned));
        Assert.Equal("0.2.0", FreeTiers.BuildFor(tf, owned)!.Version);
    }

    [Fact]
    public void An_installed_lite_copy_updates_to_the_newest_free_tier_build()
    {
        // 0.2.1 is not in the offer's list of builds: it is judged against tierSince, and it is above it.
        var m = TestManifest(Build("0.2.0", "2026-11-20T10:00:00Z"), Build("0.2.1", "2026-11-25T10:00:00Z"));
        Assert.Equal("0.2.1", FreeTiers.BuildFor(m.Plugin("TerrainForge")!, Inputs(licensed: []))!.Version);
    }

    [Fact]
    public void The_licence_on_this_computer_counts_as_licensed_before_the_account_is_read_again()
    {
        var dir = Directory.CreateTempSubdirectory("forge-freetier-");
        try
        {
            const string uid = "0123456789abcdef0123456789abcdef";
            var key = TestLicenceKey.Generate("dev1");
            var store = new LicenceStore(dir.FullName, key.Ring(), isDefaultDataDir: false);
            var now = DateTimeOffset.UtcNow;
            Assert.Equal(LicenceSave.Stored, store.Offer(key.Envelope(LicencePayload.SubjectFor(uid), now.AddHours(-1),
                new Dictionary<string, (string, string, int)> { ["TerrainForge"] = ("bought", "2026-11-20", 1) }), uid, out _));
            var snap = new LicenceSnapshot(store.Load(), store.LoadCheck(), uid, now);

            // GET me said nothing licensed yet (read before the purchase), the licence says TerrainForge: licensed.
            var set = FreeTiers.LicensedNow(new HashSet<string>(StringComparer.OrdinalIgnoreCase), snap)!;
            Assert.Contains("terrainforge", set);
            var tf = TestManifest().Plugin("TerrainForge")!;
            Assert.Equal(PaidAccess.Full, FreeTiers.AccessOf(tf, Inputs(licensed: set)));

            // The API did not say at all: null stays null (holding counts), whatever the licence lists.
            Assert.Null(FreeTiers.LicensedNow(null, snap));
            // An expired licence does not count.
            var old = new LicenceSnapshot(store.Load(), store.LoadCheck(), uid, now.AddDays(31));
            Assert.DoesNotContain("TerrainForge", FreeTiers.LicensedNow(new HashSet<string>(), old)!);
        }
        finally { dir.Delete(true); }
    }

    // ── the words ────────────────────────────────────────────────────────

    [Fact]
    public void A_lite_install_is_never_called_owned_bought_or_licensed()
    {
        var line = LicenceWords.Line(new ProductLicence(LicenceStanding.NotLicensed), limitedTierLive: true);
        Assert.Equal("Not licensed: this runs as Lite", line);
        foreach (var word in new[] { "Owned", "Bought", "bought", "Licensed ·" }) Assert.DoesNotContain(word, line);
        Assert.Equal("Not licensed: this runs as a trial", LicenceWords.Line(new ProductLicence(LicenceStanding.NotLicensed), true, trial: true));
    }

    [Fact]
    public void Gifted_says_gifted()
    {
        Assert.Equal("Licensed · gifted 20 Nov 2026",
            LicenceWords.Line(new ProductLicence(LicenceStanding.Licensed) { How = "gifted", Since = new DateOnly(2026, 11, 20) }, limitedTierLive: true));
        Assert.Equal("Licensed · gifted", LicenceWords.Line(new ProductLicence(LicenceStanding.Licensed) { How = "gifted" }, limitedTierLive: false));
    }

    [Fact]
    public void The_needs_licence_sentence_matches_the_account_service()
    {
        Assert.Equal("Version 0.1.0 of TerrainForge needs a licence. The free Lite tier starts with 0.2.0.",
            FreeTiers.NeedsLicenceLine("TerrainForge", "0.1.0", "lite", "0.2.0"));
        Assert.Equal("Version 0.1.0 of HairForge needs a licence. The free trial starts with 0.2.0.",
            FreeTiers.NeedsLicenceLine("HairForge", "0.1.0", "trial", "0.2.0"));
        Assert.Equal("TerrainForge needs a licence.", FreeTiers.NeedsLicenceLine("TerrainForge", null, "lite", null));
        Assert.Equal("Lite", FreeTiers.Word("lite"));
        Assert.Equal("trial", FreeTiers.Word("trial"));
    }

    // ── the readers ──────────────────────────────────────────────────────

    [Fact]
    public void The_account_catalogue_reads_tiers_builds_and_included_with()
    {
        var c = AccountCatalogue.Parse(Offers)!;
        var tf = c.Plugin("terrainforge")!;
        Assert.Equal("lite", tf.FreeTier);
        Assert.Equal("0.2.0", tf.TierSince);
        Assert.Equal(2, tf.Versions.Count);
        Assert.False(tf.ServesFreeTier(Build("0.1.0", "")));
        Assert.True(tf.ServesFreeTier(Build("0.2.0", "")));
        Assert.True(tf.ServesFreeTier(Build("0.2.5", "")));   // not listed: judged against tierSince
        Assert.False(tf.ServesFreeTier(Build("0.3.0-beta", ""))); // not plain x.y.z: never at or above a floor
        Assert.Null(c.Plugin("PerformanceForge")!.FreeTier);
        Assert.False(c.Plugin("PerformanceForge")!.ServesFreeTier(Build("0.1.2", "")));
    }

    [Fact]
    public void The_account_catalogue_without_the_fields_reads_as_no_free_tier()
    {
        var c = AccountCatalogue.Parse(OffersBeforeTiers)!;
        var tf = c.Plugin("TerrainForge")!;
        Assert.Null(tf.FreeTier);
        Assert.Null(tf.TierSince);
        Assert.Null(tf.Versions.Single().FreeTier);
        Assert.False(tf.ServesFreeTier(Build("0.1.0", "")));
        Assert.Equal("$99.99", c.PriceOf("TerrainForge"));
        // An unknown tier word (a typo, "full") is no free tier at all.
        Assert.Null(AccountCatalogue.Parse("""{ "plugins": [ { "id": "X", "tier": "full", "tierSince": "0.1.0" } ] }""")!.Plugin("X")!.FreeTier);
        // A tier with freeTier flags but no tierSince: the flags decide.
        var flags = AccountCatalogue.Parse("""{ "plugins": [ { "id": "X", "tier": "lite", "versions": [ { "version": "1.0.0", "engine": "5.8", "freeTier": true } ] } ] }""")!.Plugin("X")!;
        Assert.True(flags.ServesFreeTier(Build("1.0.0", "")));
        Assert.False(flags.ServesFreeTier(Build("1.0.1", "")));
    }

    [Fact]
    public void Me_licensed_is_read_when_there_and_null_when_not()
    {
        var licensed = FirebaseEntitlements.ParseLicensed("""{ "uid": "u", "entitlements": ["TerrainForge", "SkinForge"], "licensed": ["SkinForge"] }""");
        Assert.NotNull(licensed);
        Assert.Contains("skinforge", licensed);
        Assert.DoesNotContain("TerrainForge", licensed);
        Assert.Empty(FirebaseEntitlements.ParseLicensed("""{ "licensed": [] }""")!);
        Assert.Null(FirebaseEntitlements.ParseLicensed("""{ "uid": "u", "entitlements": ["TerrainForge"] }"""));
        Assert.Null(FirebaseEntitlements.ParseLicensed("""{ "licensed": "TerrainForge" }"""));
    }

    [Theory]
    [InlineData("0.2.0", "0.2.0", true)]
    [InlineData("0.10.0", "0.2.0", true)]
    [InlineData("0.1.9", "0.2.0", false)]
    [InlineData("1.0.0-beta.1", "0.2.0", false)]
    [InlineData("0.2.0", null, false)]
    [InlineData("0.2.0", "two", false)]
    public void Plain_versions_compare_as_the_server_does(string version, string? floor, bool atLeast) =>
        Assert.Equal(atLeast, PlainVersion.AtLeast(version, floor));
}
