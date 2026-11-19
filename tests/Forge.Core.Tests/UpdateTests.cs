using Forge.Core.Catalogue;
using Xunit;

namespace Forge.Core.Tests;

/// <summary>Updates counted per product (8.1), Labs and pins left out of "Update all" (8.3, 8.4): slice 3's acceptance.</summary>
public class UpdateTests
{
    [Fact]
    public void A_tool_its_engine_and_its_agent_access_one_version_behind_are_one_update_with_three_parts()
    {
        var installed = new Dictionary<string, string>
        {
            ["MotionForge"] = "0.4.0", ["MotionForgeKimodo"] = "0.5.0", ["MotionForgeToolset"] = "0.3.0", ["AutomationForgeHub"] = "0.2.5",
        };
        var list = UpdatePlanner.Build(Fixtures.Inputs(installed: installed));
        Assert.Equal(1, list.Count);
        var row = list.Rows.Single();
        Assert.Equal("Motion from a sentence", row.Title);
        Assert.Equal("MotionForge", row.Subtitle);
        Assert.Equal(["MotionForge", "MotionForgeKimodo", "MotionForgeToolset"], row.Parts.Select(p => p.Id));
        Assert.Equal("MotionForge 0.4.0 → 0.4.1 · Kimodo engine 0.5.0 → 0.5.1 · Agent access 0.3.0 → 0.3.1", row.PartsLine);
        Assert.Equal("animation", row.Track);
    }

    [Fact]
    public void Update_all_leaves_a_pinned_product_and_a_labs_product_and_says_so()
    {
        var installed = new Dictionary<string, string>
        {
            ["MotionForge"] = "0.4.0", ["FaceForge"] = "0.2.2", ["MeshForge"] = "0.3.2", ["MeshForgeTrellis"] = "0.0.2", ["MontageForge"] = "0.1.2",
        };
        var pins = new Dictionary<string, string> { ["FaceForge"] = "0.2.2" };
        var list = UpdatePlanner.Build(Fixtures.Inputs(installed: installed, pins: pins));
        Assert.Equal(2, list.Count); // MotionForge, MontageForge
        Assert.Contains("MotionForge", list.UpdateAllIds);
        Assert.DoesNotContain("FaceForge", list.UpdateAllIds);
        Assert.DoesNotContain("MeshForgeTrellis", list.UpdateAllIds);
        // TRELLIS is a Labs engine of MeshForge: it is not folded into MeshForge's row but listed under LABS.
        Assert.Single(list.Labs);
        Assert.Equal("MeshForgeTrellis", list.Labs.Single().Main.Id);
        Assert.Single(list.Pinned);
        Assert.Equal("0.2.2", list.Pinned.Single().PinnedAt);
        Assert.Equal("Updates 2 tools. 1 Labs update and 1 pinned tool are left as they are.", list.UpdateAllTip);
    }

    [Fact]
    public void A_labs_engine_on_its_own_is_listed_apart_and_not_counted()
    {
        var installed = new Dictionary<string, string> { ["MeshForgeTrellis"] = "0.0.2" };
        var list = UpdatePlanner.Build(Fixtures.Inputs(installed: installed));
        Assert.Equal(0, list.Count);
        Assert.Single(list.Labs);
        Assert.Equal("Updates 0 tools. 1 Labs update is left as it is.", list.UpdateAllTip);
    }

    [Fact]
    public void A_component_the_catalogue_does_not_know_appears_under_other_tools_and_updates()
    {
        var m = Fixtures.Manifest();
        m.Plugins.Add(new Manifest.PluginInfo
        {
            Id = "BrandNewTool", Role = "core", Distribution = "open",
            Versions = [new Manifest.VersionInfo { Version = "0.2.0", Engine = "5.8", Channel = "stable", ReleasedAt = "2026-11-18T00:00:00Z" }],
        });
        var inputs = Fixtures.Inputs(installed: new Dictionary<string, string> { ["BrandNewTool"] = "0.1.0" }) with { Manifest = m };
        var list = UpdatePlanner.Build(inputs);
        var row = list.Rows.Single();
        Assert.Null(row.Product);
        Assert.Equal("BrandNewTool", row.Title);
        Assert.Contains("BrandNewTool", list.UpdateAllIds);
    }

    [Fact]
    public void Pinning_a_product_pins_each_of_its_installed_components()
    {
        var installed = new Dictionary<string, string> { ["FaceForge"] = "0.2.3", ["FaceForgeToolset"] = "0.2.3" };
        var pins = UpdatePlanner.PinsFor(Fixtures.Catalogue().Product("faceforge")!, installed);
        Assert.Equal(2, pins.Count);
        Assert.Equal("0.2.3", pins["FaceForgeToolset"]);
    }
}
