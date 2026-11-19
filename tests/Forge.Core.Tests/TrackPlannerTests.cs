using Forge.Core.Catalogue;
using Xunit;

namespace Forge.Core.Tests;

/// <summary>The planner against the spec's own numbers (section 7, slice 1 acceptance check 5 and 6).</summary>
public class TrackPlannerTests
{
    [Fact]
    public void Animation_default_is_the_five_components_in_order()
    {
        var plan = Fixtures.DefaultPlan("animation", Fixtures.Inputs());
        Assert.Equal(["AutomationForgeHub", "MotionForge", "MotionForgeKimodo", "MotionForgeUthana", "MontageForge"], plan.Ids);
        Assert.Equal("2 capabilities · 5 components", plan.Summary);
        Assert.Equal("12 MB", Words.Size(plan.Size));
    }

    [Fact]
    public void Animation_with_agents_adds_the_three_toolsets()
    {
        var plan = Fixtures.DefaultPlan("animation", Fixtures.Inputs(agents: true));
        Assert.Equal(["AutomationForgeHub", "MotionForge", "MotionForgeKimodo", "MotionForgeUthana", "MontageForge",
                      "MotionForgeToolset", "MotionForgeKimodoToolset", "MontageForgeToolset"], plan.Ids);
        Assert.Equal("2 capabilities · 8 components", plan.Summary);
        Assert.Equal("13 MB", Words.Size(plan.Size));
        Assert.All(plan.Items.Where(i => i.Id.EndsWith("Toolset")), i => Assert.Equal(PlanRole.Agent, i.Role));
    }

    [Fact]
    public void Characters_default_is_meshforge_and_its_cloud_engine()
    {
        var plan = Fixtures.DefaultPlan("characters", Fixtures.Inputs());
        Assert.Equal(["AutomationForgeHub", "MeshForge", "MeshForgeCloud"], plan.Ids);
        Assert.Equal("1 capability · 3 components", plan.Summary);
        Assert.Equal("2.4 MB", Words.Size(plan.Size));

        var agents = Fixtures.DefaultPlan("characters", Fixtures.Inputs(agents: true));
        Assert.Equal(["AutomationForgeHub", "MeshForge", "MeshForgeCloud", "MeshForgeToolset"], agents.Ids);
        Assert.Equal("2.6 MB", Words.Size(agents.Size));
    }

    [Fact]
    public void Worldbuilding_default()
    {
        var plan = Fixtures.DefaultPlan("worldbuilding", Fixtures.Inputs());
        Assert.Equal(["AutomationForgeHub", "MeshForge", "MeshForgeCloud", "SurfaceForge", "SurfaceForgeLocal", "SurfaceForgePatina"], plan.Ids);
        Assert.Equal("2 capabilities · 6 components", plan.Summary);
        Assert.Equal("3.1 MB", Words.Size(plan.Size));
        Assert.Equal("2 capabilities · 8 components", Fixtures.DefaultPlan("worldbuilding", Fixtures.Inputs(agents: true)).Summary);
    }

    [Fact]
    public void Storytelling_default()
    {
        var plan = Fixtures.DefaultPlan("storytelling", Fixtures.Inputs());
        Assert.Equal(["AutomationForgeHub", "SpeechForge", "SpeechForgeElevenLabs", "FaceForge", "FaceForgeMetaHuman"], plan.Ids);
        Assert.Equal("2 capabilities · 5 components", plan.Summary);
        Assert.Equal("2.7 MB", Words.Size(plan.Size));
        Assert.Equal("2 capabilities · 7 components", Fixtures.DefaultPlan("storytelling", Fixtures.Inputs(agents: true)).Summary);
    }

    [Theory]
    [InlineData("characters")]
    [InlineData("animation")]
    [InlineData("worldbuilding")]
    [InlineData("storytelling")]
    public void No_default_plan_holds_labs_paid_or_unreleased_components(string track)
    {
        foreach (var agents in new[] { false, true })
        {
            var inputs = Fixtures.Inputs(agents: agents);
            var plan = Fixtures.DefaultPlan(track, inputs);
            Assert.DoesNotContain("MeshForgeTrellis", plan.Ids);
            Assert.DoesNotContain("MeshForgeTrellisToolset", plan.Ids);
            foreach (var id in plan.Ids)
            {
                var p = inputs.Manifest.Plugin(id)!;
                Assert.False(p.IsPaid, $"{id} is paid and not owned");
                Assert.NotNull(p.Latest("5.8"));
            }
        }
    }

    [Fact]
    public void Unticking_gameplay_and_uthana_leaves_one_capability_and_three_components()
    {
        var inputs = Fixtures.Inputs();
        var track = inputs.Catalogue.Track("animation")!;
        var selection = TrackPlanner.DefaultSelection(track, inputs)
            .With("gameplay", false)
            .WithEngine("motion", "motionforge-uthana", false);
        var plan = TrackPlanner.Plan(track, selection, inputs);
        Assert.Equal("1 capability · 3 components", plan.Summary);
        Assert.Equal(["AutomationForgeHub", "MotionForge", "MotionForgeKimodo"], plan.Ids);
    }

    [Fact]
    public void Nothing_ticked_plans_nothing_not_even_the_editor_plugin()
    {
        var inputs = Fixtures.Inputs();
        var track = inputs.Catalogue.Track("animation")!;
        var plan = TrackPlanner.Plan(track, new TrackSelection([], new Dictionary<string, IReadOnlyCollection<string>>()), inputs);
        Assert.Empty(plan.Items);
        Assert.Equal("0 capabilities · 0 components", plan.Summary);
    }

    [Fact]
    public void Paid_labs_and_coming_soon_are_not_ticked_and_not_counted()
    {
        var inputs = Fixtures.Inputs();
        var characters = TrackPlanner.DefaultSelection(inputs.Catalogue.Track("characters")!, inputs);
        Assert.Equal(["meshes"], characters.Capabilities);           // outfits paid, hair and skin coming soon
        Assert.Equal(["meshforge-cloud"], characters.Engines["meshes"]); // TRELLIS is Labs

        var animation = TrackPlanner.DefaultSelection(inputs.Catalogue.Track("animation")!, inputs);
        Assert.DoesNotContain("capture", animation.Capabilities);   // PerformanceForge is paid

        var world = TrackPlanner.DefaultSelection(inputs.Catalogue.Track("worldbuilding")!, inputs);
        Assert.Equal(["props", "materials"], world.Capabilities);   // terrain paid, three coming soon

        // Ticking a paid capability by hand still installs nothing from it.
        var forced = TrackPlanner.Plan(inputs.Catalogue.Track("animation")!, animation.With("capture", true), inputs);
        Assert.DoesNotContain("PerformanceForge", forced.Ids);
        Assert.Equal(2, forced.CapabilityCount);
    }

    [Fact]
    public void Statuses_follow_the_order_of_6_5()
    {
        var inputs = Fixtures.Inputs();
        var cat = inputs.Catalogue;
        Assert.Equal(ProductStatus.Coming, TrackPlanner.StatusOf(cat.Product("hairforge")!, inputs));     // no components
        Assert.Equal(ProductStatus.Coming, TrackPlanner.StatusOf(cat.Product("skinforge")!, inputs));     // no release
        Assert.Equal(ProductStatus.Labs, TrackPlanner.StatusOf(cat.Product("meshforge-trellis")!, inputs));
        Assert.Equal(ProductStatus.NeedsPurchase, TrackPlanner.StatusOf(cat.Product("terrainforge")!, inputs));
        Assert.Equal(ProductStatus.Available, TrackPlanner.StatusOf(cat.Product("motionforge")!, inputs));
        Assert.Equal(ProductStatus.Coming, TrackPlanner.StatusOf(cat.Product("motionforge")!, Fixtures.Inputs(engine: "5.7")));
    }

    [Fact]
    public void An_owned_paid_product_is_ticked_and_installed()
    {
        var inputs = Fixtures.Inputs(owned: ["TerrainForge"]);
        var plan = Fixtures.DefaultPlan("worldbuilding", inputs);
        Assert.Contains("TerrainForge", plan.Ids);
        Assert.Equal(3, plan.CapabilityCount);
    }

    [Fact]
    public void Labs_engine_installs_only_with_show_labs_and_a_tick()
    {
        var inputs = Fixtures.Inputs(labs: true);
        var track = inputs.Catalogue.Track("characters")!;
        var selection = TrackPlanner.DefaultSelection(track, inputs);
        Assert.DoesNotContain("meshforge-trellis", selection.Engines["meshes"]); // never ticked by default
        var ticked = TrackPlanner.Plan(track, selection.WithEngine("meshes", "meshforge-trellis", true), inputs);
        Assert.Contains("MeshForgeTrellis", ticked.Ids);

        var off = Fixtures.Inputs(labs: false);
        var refused = TrackPlanner.Plan(track, selection.WithEngine("meshes", "meshforge-trellis", true), off);
        Assert.DoesNotContain("MeshForgeTrellis", refused.Ids);
        Assert.Contains(refused.Drops, d => d.Id == "MeshForgeTrellis" && d.Reason == "labs");
    }

    [Fact]
    public void Installed_components_are_current_and_counted_as_already_here()
    {
        var installed = new Dictionary<string, string>
        {
            ["AutomationForgeHub"] = "0.2.5", ["MotionForge"] = "0.4.1", ["MotionForgeKimodo"] = "0.5.1",
        };
        var plan = Fixtures.DefaultPlan("animation", Fixtures.Inputs(installed: installed));
        Assert.Equal(["MotionForgeUthana", "MontageForge"], plan.Ids);
        Assert.Equal(3, plan.AlreadyHere);
        Assert.Equal("2 capabilities · 2 components", plan.Summary);
        Assert.Equal(2, plan.NewCapabilityCount);
    }

    [Fact]
    public void An_older_receipt_is_an_update_unless_pinned()
    {
        var installed = new Dictionary<string, string> { ["MotionForge"] = "0.4.0" };
        var plan = Fixtures.DefaultPlan("animation", Fixtures.Inputs(installed: installed));
        Assert.Equal(PlanAction.Update, plan.Items.Single(i => i.Id == "MotionForge").Action);
        Assert.Equal(ProductStatus.Update, TrackPlanner.StatusOf(Fixtures.Catalogue().Product("motionforge")!, Fixtures.Inputs(installed: installed)));

        var pinned = Fixtures.DefaultPlan("animation", Fixtures.Inputs(installed: installed, pins: new Dictionary<string, string> { ["MotionForge"] = "0.4.0" }));
        Assert.Equal(PlanAction.Current, pinned.Items.Single(i => i.Id == "MotionForge").Action);
    }

    [Fact]
    public void Agent_switch_adds_agent_access_for_installed_products_of_the_track()
    {
        // MontageForge installed earlier; the person now installs only Motion with agents on.
        var installed = new Dictionary<string, string> { ["MontageForge"] = "0.1.3", ["AutomationForgeHub"] = "0.2.5" };
        var inputs = Fixtures.Inputs(agents: true, installed: installed);
        var track = inputs.Catalogue.Track("animation")!;
        var plan = TrackPlanner.Plan(track, TrackPlanner.DefaultSelection(track, inputs).With("gameplay", false), inputs);
        Assert.Contains("MontageForgeToolset", plan.Ids);
        Assert.Contains("MotionForgeToolset", plan.Ids);
    }

    [Fact]
    public void A_dependant_of_a_paid_component_not_owned_is_dropped_and_said()
    {
        var cat = Fixtures.Catalogue();
        cat.Product("meshforge")!.AgentPlugins.Add("MeshForgeGarmentToolset"); // its dependency MeshForgeGarment is paid
        var plan = Fixtures.DefaultPlan("characters", Fixtures.Inputs(catalogue: cat, agents: true));
        Assert.DoesNotContain("MeshForgeGarment", plan.Ids);
        Assert.DoesNotContain("MeshForgeGarmentToolset", plan.Ids);
        Assert.Contains(plan.Drops, d => d.Id == "MeshForgeGarment" && d.Reason == "not-owned");
        Assert.Contains(plan.Drops, d => d.Id == "MeshForgeGarmentToolset" && d.Message.StartsWith("Agent access for MeshForge Garment was left out"));
    }

    [Fact]
    public void Dependencies_come_first_and_are_marked()
    {
        var cat = Fixtures.Catalogue();
        var inputs = Fixtures.Inputs(catalogue: cat, owned: ["SpeechForgeDeepL"]);
        var track = cat.Track("storytelling")!;
        var selection = new TrackSelection(["translation"], new Dictionary<string, IReadOnlyCollection<string>>());
        var plan = TrackPlanner.Plan(track, selection, inputs);
        Assert.Equal(["AutomationForgeHub", "SpeechForge", "SpeechForgeDeepL"], plan.Ids);
        var dep = plan.Items.Single(i => i.Id == "SpeechForge");
        Assert.Equal(PlanRole.Dependency, dep.Role);
        Assert.Equal("SpeechForge DeepL", dep.NeededBy);
    }

    [Fact]
    public void A_project_target_does_not_carry_the_editor_plugin()
    {
        var inputs = Fixtures.Inputs() with { TargetKind = "project" };
        var plan = Fixtures.DefaultPlan("animation", inputs);
        Assert.DoesNotContain("AutomationForgeHub", plan.Ids);
        Assert.Equal(4, plan.ComponentCount);
    }
}
