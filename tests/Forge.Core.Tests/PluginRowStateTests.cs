using Forge.Core.Catalogue;
using Forge.Core.Licence;
using Forge.Core.Manifest;
using Xunit;
using static Forge.Core.Tests.FreeTierTests;

namespace Forge.Core.Tests;

/// <summary>
/// The 0.4.5 Hub's plugin rows: the install rule and the words, state by state. A free plugin and a paid one without a
/// free tier must read exactly as in 0.4.4; a paid one with a live free tier not held licensed reads Install Lite with
/// Buy Full beside it.
/// </summary>
public class PluginRowStateTests
{
    /// <summary>A row as the window builds it (MainViewModel.RowState), from the rules and a licence standing.</summary>
    private static PluginRowState Row(string id, AccessInputs inputs, Manifest.Manifest? manifest = null, string? installed = null,
        bool signedIn = true, ProductLicence? licence = null)
    {
        var m = manifest ?? TestManifest();
        var p = m.Plugin(id)!;
        var how = FreeTiers.AccessOf(p, inputs);
        var latest = p.Latest(inputs.Engine, inputs.Channel);
        var tier = p.IsPaid ? FreeTiers.TierOf(id, inputs) : null;
        return new PluginRowState
        {
            Offered = FreeTiers.BuildFor(p, inputs) ?? latest,
            InstalledVersion = installed,
            SignedIn = signedIn,
            IsPaid = p.IsPaid,
            Owned = inputs.Owned.Contains(id),
            Access = how,
            Tier = tier,
            NeedsLicenceLine = how == PaidAccess.NeedsLicence ? FreeTiers.NeedsLicenceLine(id, latest?.Version, tier, FreeTiers.FloorOf(id, inputs)) : null,
            Licence = p.IsPaid ? licence ?? new ProductLicence(LicenceStanding.NoLicenceYet) : null,
        };
    }

    private static readonly ProductLicence NotLicensed = new(LicenceStanding.NotLicensed);
    private static readonly ProductLicence Bought = new(LicenceStanding.Licensed) { How = "bought", Since = new DateOnly(2026, 11, 20) };

    // ── 0.4.4, unchanged ─────────────────────────────────────────────────

    [Fact]
    public void A_free_plugin_reads_as_in_0_4_4()
    {
        var r = Row("SurfaceForge", Inputs(licensed: []));
        Assert.Equal(("not installed", "Add & install", RowAction.Install), (r.Status, r.ActionLabel, r.Action));
        Assert.False(r.CanBuyFull);
        Assert.Equal("", r.LicenceLine);
        Assert.False(r.CanRestoreLicence);

        var owned = Row("SurfaceForge", Inputs(owned: ["SurfaceForge"], licensed: []), installed: "0.3.0");
        Assert.Equal(("up to date", "Installed"), (owned.Status, owned.ActionLabel));
        Assert.False(owned.CanAct);
        Assert.Equal(("on your account", "Install"), (Row("SurfaceForge", Inputs(owned: ["SurfaceForge"], licensed: [])).Status, Row("SurfaceForge", Inputs(owned: ["SurfaceForge"], licensed: [])).ActionLabel));
        Assert.Equal(("update available", "Update"), (Row("SurfaceForge", Inputs(licensed: []), installed: "0.2.0").Status, Row("SurfaceForge", Inputs(licensed: []), installed: "0.2.0").ActionLabel));
    }

    [Fact]
    public void A_paid_plugin_without_a_free_tier_reads_as_in_0_4_4()
    {
        var r = Row("PerformanceForge", Inputs(licensed: []));
        Assert.Equal(("paid · not on your account", "Buy", RowAction.Buy), (r.Status, r.ActionLabel, r.Action));
        Assert.True(r.CanAct);
        Assert.False(r.CanBuyFull);
        Assert.Equal("0.1.2", r.Offered!.Version);
        Assert.Equal("", r.LicenceLine); // no tier: nothing may say it is limited
        Assert.False(r.CanRestoreLicence);

        var held = Row("PerformanceForge", Inputs(owned: ["PerformanceForge"], licensed: []));
        Assert.Equal(("on your account", "Install", RowAction.Install), (held.Status, held.ActionLabel, held.Action));
    }

    [Fact]
    public void Signed_out_every_row_says_sign_in()
    {
        foreach (var id in new[] { "SurfaceForge", "PerformanceForge", "TerrainForge" })
        {
            var r = Row(id, Inputs(), signedIn: false, licence: new ProductLicence(LicenceStanding.SignedOut));
            Assert.Equal(("sign in to install", "Sign in", RowAction.SignIn), (r.Status, r.ActionLabel, r.Action));
            Assert.False(r.CanBuyFull);
            Assert.Equal("", r.LicenceLine); // "Sign in to get Full back" only for a copy installed here
        }
        var installed = Row("TerrainForge", Inputs(), installed: "0.2.0", signedIn: false, licence: new ProductLicence(LicenceStanding.SignedOut));
        Assert.Equal("Sign in to get Full back", installed.LicenceLine);
    }

    [Fact]
    public void Without_the_api_fields_a_free_tier_product_reads_as_in_0_4_4()
    {
        var r = Row("TerrainForge", Inputs(OffersBeforeTiers));
        Assert.Equal(("paid · not on your account", "Buy"), (r.Status, r.ActionLabel));
        Assert.False(r.CanBuyFull);
        var none = Row("TerrainForge", Inputs(null));
        Assert.Equal(("paid · not on your account", "Buy"), (none.Status, none.ActionLabel));
    }

    // ── the free tier ────────────────────────────────────────────────────

    [Fact]
    public void Not_licensed_and_not_installed_says_Install_Lite_with_Buy_Full_beside_it()
    {
        var r = Row("TerrainForge", Inputs(licensed: []), licence: NotLicensed);
        Assert.Equal(PaidAccess.FreeTier, r.Access);
        Assert.Equal(("paid · free Lite tier", "Install Lite", RowAction.Install), (r.Status, r.ActionLabel, r.Action));
        Assert.Equal("0.2.0", r.Offered!.Version); // the newest build with freeTier: true
        Assert.True(r.CanAct);
        Assert.True(r.CanBuyFull);
        Assert.False(r.NeedsPurchase);
        Assert.Equal("Not licensed: this runs as Lite", r.LicenceLine);
        Assert.True(r.CanRestoreLicence);
    }

    [Fact]
    public void Installed_as_Lite_says_so_and_still_offers_Buy_Full()
    {
        var r = Row("TerrainForge", Inputs(licensed: []), installed: "0.2.0", licence: NotLicensed);
        Assert.Equal(("Lite · up to date", "Installed"), (r.Status, r.ActionLabel));
        Assert.False(r.CanAct);
        Assert.True(r.CanBuyFull);
        Assert.Equal("Not licensed: this runs as Lite", r.LicenceLine);

        var update = Row("TerrainForge", Inputs(licensed: []), TestManifest(Build("0.2.0", "2026-11-20T10:00:00Z"), Build("0.2.1", "2026-11-25T10:00:00Z")), installed: "0.2.0", licence: NotLicensed);
        Assert.Equal(("Lite · update available", "Update", "0.2.1"), (update.Status, update.ActionLabel, update.Offered!.Version));
    }

    [Fact]
    public void A_full_copy_newer_than_the_free_tier_is_not_offered_down_to_it()
    {
        // Installed 0.3.0 while licensed; no longer licensed, the newest free-tier build is 0.2.0.
        var offers = Offers.Replace("""{ "version": "0.2.0", "engine": "5.8", "platform": "Win64", "channel": "stable", "freeTier": true } ] },""",
            """{ "version": "0.2.0", "engine": "5.8", "platform": "Win64", "channel": "stable", "freeTier": true }, { "version": "0.3.0", "engine": "5.8", "platform": "Win64", "channel": "stable", "freeTier": false } ] },""");
        var m = TestManifest(Build("0.2.0", "2026-11-20T10:00:00Z"), Build("0.3.0", "2026-12-01T10:00:00Z"));
        var r = Row("TerrainForge", Inputs(offers, licensed: []), m, installed: "0.3.0", licence: NotLicensed);
        Assert.False(r.HasUpdate);
        Assert.Equal("Lite · up to date", r.Status);
    }

    [Fact]
    public void A_preview_claim_holder_is_shown_as_Lite_not_as_owned()
    {
        var r = Row("TerrainForge", Inputs(owned: ["TerrainForge"], licensed: []), licence: NotLicensed);
        Assert.Equal(("paid · free Lite tier", "Install Lite"), (r.Status, r.ActionLabel));
        Assert.DoesNotContain("on your account", r.Status);
        Assert.True(r.CanBuyFull);
        Assert.Equal("Not licensed: this runs as Lite", r.LicenceLine);
    }

    [Fact]
    public void A_licensed_account_reads_as_today_with_its_licence_line()
    {
        var inputs = Inputs(owned: ["TerrainForge"], licensed: ["TerrainForge"]);
        var r = Row("TerrainForge", inputs, licence: Bought);
        Assert.Equal(("on your account", "Install", RowAction.Install), (r.Status, r.ActionLabel, r.Action));
        Assert.False(r.CanBuyFull);
        Assert.Equal("Licensed · bought 20 Nov 2026", r.LicenceLine);
        Assert.False(r.CanRestoreLicence);

        var installed = Row("TerrainForge", inputs, installed: "0.2.0", licence: Bought);
        Assert.Equal(("up to date", "Installed"), (installed.Status, installed.ActionLabel));

        var gifted = Row("TerrainForge", inputs, licence: new ProductLicence(LicenceStanding.Licensed) { How = "gifted", Since = new DateOnly(2026, 11, 20) });
        Assert.Equal("Licensed · gifted 20 Nov 2026", gifted.LicenceLine);
    }

    [Fact]
    public void Offline_grace_expired_and_a_clock_behind_say_what_to_do()
    {
        var inputs = Inputs(owned: ["TerrainForge"], licensed: []);
        var grace = Row("TerrainForge", inputs, installed: "0.2.0", licence: new ProductLicence(LicenceStanding.OfflineGrace) { How = "bought", DaysLeft = 5 });
        Assert.Equal("Offline, 5 days left", grace.LicenceLine);
        Assert.True(grace.CanRestoreLicence);
        var expired = Row("TerrainForge", inputs, installed: "0.2.0", licence: new ProductLicence(LicenceStanding.Expired) { How = "bought" });
        Assert.Equal("Running as Lite until you are online", expired.LicenceLine);
        var clock = Row("TerrainForge", inputs, installed: "0.2.0", licence: new ProductLicence(LicenceStanding.Expired) { How = "bought", ClockBehind = true });
        Assert.Equal("This computer's clock is behind. Set the date, then Restore licence", clock.LicenceLine);
        Assert.True(clock.CanRestoreLicence); // the hint names the link beside it
    }

    [Fact]
    public void Below_the_floor_is_Buy_Full_with_the_servers_sentence()
    {
        var r = Row("TerrainForge", Inputs(licensed: []), TestManifest(Build("0.1.0", "2026-10-29T12:09:11Z")), licence: NotLicensed);
        Assert.Equal(PaidAccess.NeedsLicence, r.Access);
        Assert.Equal(("paid · needs a licence", "Buy Full", RowAction.Buy), (r.Status, r.ActionLabel, r.Action));
        Assert.False(r.CanBuyFull); // the main button is the Buy
        Assert.Equal("Version 0.1.0 of TerrainForge needs a licence. The free Lite tier starts with 0.2.0.", r.StatusTip);
        // Nothing is installed and nothing can install as Lite: no line may say it "runs as Lite". Restore stays.
        Assert.Equal("", r.LicenceLine);
        Assert.True(r.CanRestoreLicence);
        // A copy that is installed here (an older build bought before) still says where its licence stands.
        var installed = Row("TerrainForge", Inputs(licensed: []), TestManifest(Build("0.1.0", "2026-10-29T12:09:11Z")), installed: "0.1.0", licence: NotLicensed);
        Assert.Equal("Not licensed: this runs as Lite", installed.LicenceLine);
    }

    [Fact]
    public void A_trial_says_trial()
    {
        var offers = Offers.Replace("\"tier\": \"lite\"", "\"tier\": \"trial\"");
        var r = Row("TerrainForge", Inputs(offers, licensed: []), licence: NotLicensed);
        Assert.Equal(("paid · free trial", "Install trial"), (r.Status, r.ActionLabel));
        Assert.Equal("Not licensed: this runs as a trial", r.LicenceLine);
    }
}
