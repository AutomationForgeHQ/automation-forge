using Forge.Core.Catalogue;
using Forge.Core.Cloud;
using Forge.Core.Entitlements;
using Forge.Core.Licence;
using Forge.Core.Manifest;
using Xunit;

namespace Forge.Core.Tests;

/// <summary>
/// A paid plugin's free tier (0.5.0-beta.7): the account API's offer `tier`, `tierSince` and per-build `freeTier`, and
/// GET me `licensed`. TerrainForge as released on 20 November 2026: 0.1.0 (the full tool, no cap, below the floor) and
/// 0.2.0 (the first build that enforces Lite, `tierSince` 0.2.0).
/// </summary>
public class FreeTierTests
{
    private const string Offers = """
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
    private const string OffersBeforeTiers = """
        { "payments": "stripe", "plugins": [
          { "id": "TerrainForge", "name": "TerrainForge", "price": { "amount": 9999, "currency": "usd" }, "forSale": true, "released": true, "tier": null,
            "includes": [], "includedWith": null, "versions": [ { "version": "0.1.0", "engine": "5.8", "platform": "Win64", "channel": "stable", "releasedAt": "2026-10-29T12:09:11Z" } ] } ] }
        """;

    private static VersionInfo Build(string version, string releasedAt, long size = 9_000_000) =>
        new() { Version = version, Engine = "5.8", Platform = "Win64", Channel = "stable", ReleasedAt = releasedAt, Size = size };

    /// <summary>The fixture manifest with TerrainForge's builds replaced.</summary>
    private static Manifest.Manifest WithTerrain(params VersionInfo[] builds)
    {
        var m = Fixtures.Manifest();
        var tf = m.Plugin("TerrainForge")!;
        tf.Versions = builds.ToList();
        return m;
    }

    private static Manifest.Manifest Released() =>
        WithTerrain(Build("0.1.0", "2026-10-29T12:09:11Z"), Build("0.2.0", "2026-11-20T10:00:00Z", 9_500_000));

    private static PlanInputs Inputs(Manifest.Manifest manifest, string? offers = Offers, IEnumerable<string>? owned = null,
        IEnumerable<string>? licensed = null, IDictionary<string, string>? installed = null)
    {
        var catalogue = Fixtures.Catalogue();
        return Fixtures.Inputs(catalogue: catalogue, owned: owned, installed: installed) with
        {
            Manifest = manifest,
            Offers = offers is null ? null : AccountCatalogue.Parse(offers),
            Licensed = licensed is null ? null : new HashSet<string>(licensed, StringComparer.OrdinalIgnoreCase),
            SignedIn = true,
        };
    }

    private static TrackPlan TerrainPlan(PlanInputs inputs)
    {
        var track = inputs.Catalogue.Track("worldbuilding")!;
        return TrackPlanner.Plan(track, new TrackSelection(["terrain"], new Dictionary<string, IReadOnlyCollection<string>>()), inputs);
    }

    // ── the planner ──────────────────────────────────────────────────────

    [Fact]
    public void A_non_holder_gets_the_newest_free_tier_build_marked_lite()
    {
        var inputs = Inputs(Released(), licensed: []);
        var tf = inputs.Manifest.Plugin("TerrainForge")!;
        Assert.Equal(PaidAccess.FreeTier, FreeTiers.AccessOf(tf, inputs));
        Assert.Equal("0.2.0", FreeTiers.BuildFor(tf, inputs)!.Version);

        var plan = TerrainPlan(inputs);
        var item = Assert.Single(plan.Items, i => i.Id == "TerrainForge");
        Assert.Equal(PlanAction.Install, item.Action);
        Assert.Equal("0.2.0", item.Version);
        Assert.Equal(9_500_000, item.Size);
        Assert.Equal("lite", item.FreeTier);
        Assert.True(item.IsFreeTier);
        Assert.DoesNotContain(plan.Drops, d => d.Id == "TerrainForge");
        Assert.Equal(["terrain"], plan.Capabilities);
    }

    [Fact]
    public void A_free_tier_product_is_available_and_ticked_by_default_in_its_track()
    {
        var inputs = Inputs(Released(), licensed: []);
        var product = inputs.Catalogue.Product("terrainforge")!;
        Assert.Equal(ProductStatus.Available, TrackPlanner.StatusOf(product, inputs));
        var world = TrackPlanner.DefaultSelection(inputs.Catalogue.Track("worldbuilding")!, inputs);
        Assert.Contains("terrain", world.Capabilities);
        var plan = Fixtures.DefaultPlan("worldbuilding", inputs);
        Assert.Contains("TerrainForge", plan.Ids);
        Assert.Equal("lite", plan.Items.Single(i => i.Id == "TerrainForge").FreeTier);
        // Nothing else in the track is marked.
        Assert.All(plan.Items.Where(i => i.Id != "TerrainForge"), i => Assert.Null(i.FreeTier));
    }

    [Fact]
    public void Below_the_floor_only_is_not_installable_and_says_so_in_the_servers_words()
    {
        var inputs = Inputs(WithTerrain(Build("0.1.0", "2026-10-29T12:09:11Z")), licensed: []);
        var tf = inputs.Manifest.Plugin("TerrainForge")!;
        Assert.Equal(PaidAccess.NeedsLicence, FreeTiers.AccessOf(tf, inputs));
        Assert.Null(FreeTiers.BuildFor(tf, inputs));
        Assert.Equal(ProductStatus.NeedsPurchase, TrackPlanner.StatusOf(inputs.Catalogue.Product("terrainforge")!, inputs));

        var plan = TerrainPlan(inputs);
        Assert.DoesNotContain("TerrainForge", plan.Ids);
        Assert.Empty(plan.Capabilities); // the capability itself is not taken, as for anything that must be bought

        // Reached as a dependency of something free, it is left out with the account service's own words, and so is what needs it.
        inputs.Manifest.Plugin("SurfaceForge")!.Dependencies.Add("TerrainForge");
        var world = inputs.Catalogue.Track("worldbuilding")!;
        var materials = TrackPlanner.Plan(world, new TrackSelection(["materials"], new Dictionary<string, IReadOnlyCollection<string>>()), inputs);
        var drop = Assert.Single(materials.Drops, d => d.Id == "TerrainForge");
        Assert.Equal("needs-licence", drop.Reason);
        Assert.Equal("Version 0.1.0 of TerrainForge needs a licence. The free Lite tier starts with 0.2.0.", drop.Message);
        Assert.Contains(materials.Drops, d => d.Id == "SurfaceForge" && d.Reason == "dependant");
        Assert.DoesNotContain("SurfaceForge", materials.Ids);

        // With a free-tier build, the same dependency goes in as Lite and nothing is left out.
        var released = Inputs(Released(), licensed: []);
        released.Manifest.Plugin("SurfaceForge")!.Dependencies.Add("TerrainForge");
        var lite = TrackPlanner.Plan(world, new TrackSelection(["materials"], new Dictionary<string, IReadOnlyCollection<string>>()), released);
        Assert.Contains("SurfaceForge", lite.Ids);
        Assert.Equal("lite", lite.Items.Single(i => i.Id == "TerrainForge").FreeTier);
        Assert.DoesNotContain(lite.Drops, d => d.Id is "TerrainForge" or "SurfaceForge");
    }

    [Fact]
    public void A_free_tier_never_takes_a_build_below_the_floor_even_when_it_was_released_later()
    {
        // A 0.1.1 hotfix of the uncapped line, published after 0.2.0: newest by date, below the floor.
        var inputs = Inputs(WithTerrain(Build("0.2.0", "2026-11-20T10:00:00Z"), Build("0.1.1", "2026-11-21T10:00:00Z")), licensed: []);
        var tf = inputs.Manifest.Plugin("TerrainForge")!;
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
        var manifest = WithTerrain(Build("0.1.0", "2026-10-29T12:09:11Z"), Build("0.2.0", "2026-11-20T10:00:00Z"), Build("0.3.0", "2026-12-01T10:00:00Z"));

        var licensed = Inputs(manifest, offers, owned: ["TerrainForge"], licensed: ["TerrainForge"]);
        var item = TerrainPlan(licensed).Items.Single(i => i.Id == "TerrainForge");
        Assert.Equal("0.3.0", item.Version);
        Assert.Null(item.FreeTier);
        Assert.Equal(PaidAccess.Full, FreeTiers.AccessOf(manifest.Plugin("TerrainForge")!, licensed));

        var lite = Inputs(manifest, offers, licensed: []);
        Assert.Equal("0.2.0", TerrainPlan(lite).Items.Single(i => i.Id == "TerrainForge").Version);
    }

    [Fact]
    public void A_preview_claim_holder_is_a_non_holder_for_a_free_tier()
    {
        // Held (a preview claim), but not by a licensed route: GET me `licensed` leaves it out.
        var inputs = Inputs(Released(), owned: ["TerrainForge"], licensed: []);
        var tf = inputs.Manifest.Plugin("TerrainForge")!;
        Assert.Equal(PaidAccess.FreeTier, FreeTiers.AccessOf(tf, inputs));
        var product = inputs.Catalogue.Product("terrainforge")!;
        Assert.False(ProductFacts.Owned(product, inputs));
        Assert.Equal("lite", ProductFacts.FreeTierOf(product, inputs));
    }

    [Fact]
    public void Without_a_tier_a_paid_plugin_is_dropped_as_today()
    {
        var inputs = Inputs(Released(), licensed: []);
        var perf = inputs.Manifest.Plugin("PerformanceForge")!;
        Assert.Equal(PaidAccess.NotOwned, FreeTiers.AccessOf(perf, inputs));
        var animation = inputs.Catalogue.Track("animation")!;
        var plan = TrackPlanner.Plan(animation, TrackPlanner.DefaultSelection(animation, inputs).With("capture", true), inputs);
        Assert.DoesNotContain("PerformanceForge", plan.Ids);
        Assert.DoesNotContain("capture", plan.Capabilities);
        // As a dependency: not on your account, as before.
        inputs.Manifest.Plugin("SurfaceForge")!.Dependencies.Add("PerformanceForge");
        var materials = TrackPlanner.Plan(inputs.Catalogue.Track("worldbuilding")!, new TrackSelection(["materials"], new Dictionary<string, IReadOnlyCollection<string>>()), inputs);
        Assert.Contains(materials.Drops, d => d.Id == "PerformanceForge" && d.Reason == "not-owned" && d.Message == "PerformanceForge is not on your account.");
        Assert.Equal(ProductStatus.NeedsPurchase, TrackPlanner.StatusOf(inputs.Catalogue.Product("performanceforge")!, inputs));
        // A holder without a licence and no tier keeps the old rule: it installs.
        Assert.Equal(PaidAccess.Full, FreeTiers.AccessOf(perf, Inputs(Released(), owned: ["PerformanceForge"], licensed: [])));
    }

    [Theory]
    [InlineData(true)]   // the account service's answer before the fields: tier null, no tierSince, no freeTier
    [InlineData(false)]  // no account catalogue read at all
    public void Without_the_fields_everything_is_as_before(bool oldAnswer)
    {
        var offers = oldAnswer ? OffersBeforeTiers : null;
        var notOwned = Inputs(Released(), offers);
        var plan = TerrainPlan(notOwned);
        Assert.DoesNotContain("TerrainForge", plan.Ids);
        Assert.Empty(plan.Capabilities);
        notOwned.Manifest.Plugin("SurfaceForge")!.Dependencies.Add("TerrainForge");
        var materials = TrackPlanner.Plan(notOwned.Catalogue.Track("worldbuilding")!, new TrackSelection(["materials"], new Dictionary<string, IReadOnlyCollection<string>>()), notOwned);
        Assert.Contains(materials.Drops, d => d.Id == "TerrainForge" && d.Reason == "not-owned");
        Assert.Equal(ProductStatus.NeedsPurchase, TrackPlanner.StatusOf(notOwned.Catalogue.Product("terrainforge")!, notOwned));

        // Owned, and `licensed` not said (null): owning it is enough, and it gets the newest build.
        var owned = Inputs(Released(), offers, owned: ["TerrainForge"]);
        var item = TerrainPlan(owned).Items.Single(i => i.Id == "TerrainForge");
        Assert.Equal("0.2.0", item.Version);
        Assert.Null(item.FreeTier);
        Assert.True(ProductFacts.Owned(owned.Catalogue.Product("terrainforge")!, owned));
    }

    [Fact]
    public void An_installed_lite_copy_updates_to_the_newest_free_tier_build()
    {
        var manifest = WithTerrain(Build("0.2.0", "2026-11-20T10:00:00Z"), Build("0.2.1", "2026-11-25T10:00:00Z"));
        var inputs = Inputs(manifest, licensed: [], installed: new Dictionary<string, string> { ["TerrainForge"] = "0.2.0" });
        // 0.2.1 is not in the offer's list of builds: it is judged against tierSince, and it is above it.
        Assert.Equal("0.2.1", TrackPlanner.NewerVersion("TerrainForge", inputs));
        var product = inputs.Catalogue.Product("terrainforge")!;
        Assert.Equal(ProductStatus.Update, TrackPlanner.StatusOf(product, inputs));
        var item = TerrainPlan(inputs).Items.Single(i => i.Id == "TerrainForge");
        Assert.Equal(PlanAction.Update, item.Action);
        Assert.Equal("lite", item.FreeTier);
    }

    [Fact]
    public void After_a_purchase_the_same_install_counts_as_full_with_no_reinstall()
    {
        var installed = new Dictionary<string, string> { ["TerrainForge"] = "0.2.0" };
        var before = Inputs(Released(), licensed: [], installed: installed);
        var after = Inputs(Released(), owned: ["TerrainForge"], licensed: ["TerrainForge"], installed: installed);
        var product = before.Catalogue.Product("terrainforge")!;
        Assert.Equal("lite", ProductFacts.FreeTierOf(product, before));
        Assert.Null(ProductFacts.FreeTierOf(product, after));
        Assert.True(ProductFacts.Owned(product, after));
        // Nothing to install or update: the same 0.2.0 is now Full.
        Assert.Equal(ProductStatus.Installed, TrackPlanner.StatusOf(product, after));
        Assert.Equal(PlanAction.Current, TerrainPlan(after).Items.Single(i => i.Id == "TerrainForge").Action);
    }

    // ── the words ────────────────────────────────────────────────────────

    [Fact]
    public void A_lite_install_is_never_called_owned_bought_or_licensed()
    {
        var inputs = Inputs(Released(), owned: ["TerrainForge"], licensed: [], installed: new Dictionary<string, string> { ["TerrainForge"] = "0.2.0" });
        var product = inputs.Catalogue.Product("terrainforge")!;
        Assert.False(ProductFacts.Owned(product, inputs));
        Assert.Equal("free-tier", ProductFacts.PriceKey(product, inputs));
        Assert.Equal("Free tier", inputs.Catalogue.PriceLabel(ProductFacts.PriceKey(product, inputs)));
        Assert.False(ProductFacts.FreeTierPromised(product, inputs));

        // The licence line the Hub shows for it: the licence does not list it.
        var line = LicenceWords.Line(new ProductLicence(LicenceStanding.NotLicensed), limitedTierLive: true);
        Assert.Equal("Not licensed: this runs as Lite", line);
        foreach (var word in new[] { "Owned", "Bought", "bought", "Licensed ·" }) Assert.DoesNotContain(word, line);
        Assert.Equal("Not licensed: this runs as a trial", LicenceWords.Line(new ProductLicence(LicenceStanding.NotLicensed), true, trial: true));
    }

    [Fact]
    public void An_api_tier_makes_the_free_tier_live_even_with_an_older_catalogue()
    {
        var inputs = Inputs(Released(), licensed: []);
        var product = inputs.Catalogue.Product("terrainforge")!;
        product.FreeTierLive = false; // the bundled catalogue of beta 6
        Assert.True(ProductFacts.FreeTierLive(product, inputs));
        Assert.Equal("free-tier", ProductFacts.PriceKey(product, inputs));
        Assert.False(ProductFacts.FreeTierPromised(product, inputs));

        var old = Inputs(Released(), OffersBeforeTiers);
        old.Catalogue.Product("terrainforge")!.FreeTierLive = false;
        Assert.True(ProductFacts.FreeTierPromised(old.Catalogue.Product("terrainforge")!, old));
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

        var included = AccountCatalogue.Parse("""
            { "payments": "stripe", "plugins": [ { "id": "MeshForgeGarmentMetaHuman", "name": "MeshForgeGarmentMetaHuman", "price": null,
              "forSale": false, "released": false, "tier": null, "includes": [], "includedWith": "MeshForgeGarment", "versions": [] } ] }
            """)!;
        Assert.Equal("MeshForgeGarment", included.Plugin("MeshForgeGarmentMetaHuman")!.IncludedWith);
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
