using Xunit;
using Forge.Core.Catalogue;
using Forge.Core.Engines;
using Forge.Core.Projects;

namespace Forge.Core.Tests;

/// <summary>
/// A project's EngineAssociation resolves to an engine (a version, a source build's GUID, or the folder it sits
/// in), and the planner says per Unreal Engine version what has a build.
/// </summary>
public sealed class EngineAndAvailabilityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kovati-engine-tests", Guid.NewGuid().ToString("N"));

    public EngineAndAvailabilityTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static readonly EngineInstall Launcher58 = new("5.8", "5.8.4", @"C:\Program Files\Epic Games\UE_5.8", true, CompatibleChangelist: 55116800);
    private static readonly EngineInstall Launcher57 = new("5.7", "5.7.4", @"C:\Program Files\Epic Games\UE_5.7", true);
    private const string Guid1 = "{8C2D5E1A-3F4B-4C6D-9E8F-0A1B2C3D4E5F}";
    private static readonly EngineInstall Source = new("5.8", "5.8.0", @"D:\src\UnrealEngine", false, RegistryId: Guid1);

    [Fact]
    public void A_version_association_finds_the_launcher_engine_or_says_it_is_not_installed()
    {
        var r = EngineAssociations.Resolve("5.8", @"C:\p\Game.uproject", [Source, Launcher58]);
        Assert.Equal(AssociationKind.Version, r.Kind);
        Assert.Same(Launcher58, r.Engine);                     // the launcher build before a source build of the same version
        Assert.Equal("Unreal Engine 5.8.4", r.Label);

        var missing = EngineAssociations.Resolve("5.6", @"C:\p\Game.uproject", [Launcher58]);
        Assert.Null(missing.Engine);
        Assert.Equal("5.6", missing.Version);
        Assert.Equal("Unreal Engine 5.6 (not installed)", missing.Label);

        Assert.Equal("5.7", EngineAssociations.Resolve("5.7.4", @"C:\p\Game.uproject", [Launcher58, Launcher57]).Engine!.Version);
    }

    [Fact]
    public void A_GUID_is_a_source_build_never_a_version()
    {
        Assert.False(EngineAssociations.IsVersion(Guid1));
        Assert.True(EngineAssociations.IsGuid(Guid1));
        Assert.True(EngineAssociations.IsGuid(Guid1.Trim('{', '}')));

        // Registered and found: by the registry name the engine was found under, braces or not.
        var r = EngineAssociations.Resolve(Guid1.Trim('{', '}').ToLowerInvariant(), @"C:\p\Game.uproject", [Launcher58, Source]);
        Assert.Equal(AssociationKind.SourceBuild, r.Kind);
        Assert.Same(Source, r.Engine);
        Assert.Equal("5.8", r.Version);

        // Registered, but not among the engines found: through the registry map and the engine folder.
        var viaRegistry = EngineAssociations.Resolve(Guid1, @"C:\p\Game.uproject", [Launcher58],
            new Dictionary<string, string> { [Guid1] = @"D:\src\UnrealEngine" },
            path => path == @"D:\src\UnrealEngine" ? Source with { RegistryId = null, IsLauncherBuild = true } : null);
        Assert.Equal("5.8", viaRegistry.Version);
        Assert.False(viaRegistry.Engine!.IsLauncherBuild);
        Assert.Equal(Guid1, viaRegistry.Engine.RegistryId);

        // Not registered here: no engine, no version, and it says so.
        var missing = EngineAssociations.Resolve(Guid1, @"C:\p\Game.uproject", [Launcher58], new Dictionary<string, string>());
        Assert.Null(missing.Engine);
        Assert.Null(missing.Version);
        Assert.Equal("a source build that is not on this computer", missing.Label);
    }

    [Fact]
    public void The_command_line_names_the_problem_instead_of_looking_for_builds_for_a_GUID()
    {
        var dir = Path.Combine(_root, "SourceGame");
        Directory.CreateDirectory(dir);
        var uproject = Path.Combine(dir, "SourceGame.uproject");
        File.WriteAllText(uproject, $$"""{ "FileVersion": 3, "EngineAssociation": "{{Guid1}}" }""");

        var (version, problem) = ProjectService.EngineVersionOf(uproject, [Launcher58], new Dictionary<string, string>());
        Assert.Null(version);
        Assert.Contains("source build", problem);
        Assert.Contains("--engine", problem);

        var (found, none) = ProjectService.EngineVersionOf(uproject, [Launcher58, Source], new Dictionary<string, string>());
        Assert.Equal("5.8", found);
        Assert.Null(none);
    }

    [Fact]
    public void An_empty_association_is_the_engine_folder_the_project_sits_in()
    {
        var engine = Path.Combine(_root, "UE_Src");
        Directory.CreateDirectory(Path.Combine(engine, "Engine", "Build"));
        File.WriteAllText(Path.Combine(engine, "Engine", "Build", "Build.version"),
            """{ "MajorVersion": 5, "MinorVersion": 9, "PatchVersion": 0, "CompatibleChangelist": 60000000, "BranchName": "++UE5+Main" }""");
        var game = Path.Combine(engine, "Samples", "Game", "Game.uproject");

        var r = EngineAssociations.Resolve("", game, []);
        Assert.Equal(AssociationKind.EngineFolder, r.Kind);
        Assert.Equal("5.9", r.Version);
        Assert.Equal(60000000, r.Engine!.CompatibleChangelist);
        Assert.Equal("++UE5+Main", r.Engine.BranchName);
        Assert.False(r.Engine.IsLauncherBuild);

        Assert.Null(EngineAssociations.Resolve(null, Path.Combine(_root, "Lonely", "Game.uproject"), []).Engine);
    }

    [Fact]
    public void Engine_versions_sort_as_numbers()
    {
        Assert.Equal(["5.10", "5.9", "5.8", "4.27"], new[] { "5.8", "4.27", "5.10", "5.9" }.OrderByDescending(v => v, VersionOrder.Instance));
    }

    // ── what is built for which Unreal Engine version ────────────────────

    [Fact]
    public void Each_product_says_whether_it_is_built_for_the_chosen_engine()
    {
        var at58 = Fixtures.Inputs();
        var at57 = Fixtures.Inputs(engine: "5.7");
        var cat = at58.Catalogue;
        Assert.Equal(BuildAvailability.Built, TrackPlanner.AvailabilityOf(cat.Product("motionforge")!, at58));
        Assert.Equal(BuildAvailability.NotBuiltForEngine, TrackPlanner.AvailabilityOf(cat.Product("motionforge")!, at57));
        Assert.Equal(BuildAvailability.NoRelease, TrackPlanner.AvailabilityOf(cat.Product("skinforge")!, at57));   // no release anywhere
        Assert.Equal(BuildAvailability.NoRelease, TrackPlanner.AvailabilityOf(cat.Product("hairforge")!, at58));   // no components
        Assert.Equal("Not built for Unreal Engine 5.7 yet.", TrackPlanner.NotBuiltLine("5.7"));
    }

    [Fact]
    public void A_track_on_an_engine_without_builds_installs_nothing_and_the_manifest_says_which_have_builds()
    {
        var at57 = Fixtures.Inputs(engine: "5.7");
        var plan = Fixtures.DefaultPlan("animation", at57);
        Assert.Equal(0, plan.ComponentCount);
        Assert.Empty(plan.Capabilities);

        // Ticked by hand on 5.7, everything is dropped with the no-build reason.
        var track = at57.Catalogue.Track("animation")!;
        var forced = TrackPlanner.Plan(track, new TrackSelection(["motion"], new Dictionary<string, IReadOnlyCollection<string>>()), at57 with { ShowLabs = true });
        Assert.Empty(forced.Ids);

        var m = at57.Manifest;
        Assert.Equal(["5.8"], m.EnginesWithBuilds());
        Assert.Equal(0, m.BuiltFor("5.7"));
        Assert.True(m.BuiltFor("5.8") > 30);
    }

    [Fact]
    public void A_build_for_another_version_is_offered_only_on_that_version()
    {
        var at57 = Fixtures.Inputs(engine: "5.7");
        var mf = at57.Manifest.Plugin("MotionForge")!;
        var v58 = mf.Latest("5.8")!;
        mf.Versions.Add(new Manifest.VersionInfo { Version = "0.4.1", Engine = "5.7", Platform = "Win64", Channel = "stable", ReleasedAt = v58.ReleasedAt, Size = 1 });
        Assert.Equal(BuildAvailability.Built, TrackPlanner.AvailabilityOf("MotionForge", at57));
        Assert.Equal("0.4.1", mf.Latest("5.7")!.Version);
        Assert.Equal(["5.8", "5.7"], at57.Manifest.EnginesWithBuilds());
        Assert.Equal(1, at57.Manifest.BuiltFor("5.7"));
    }

    // ── installing into a project ────────────────────────────────────────

    [Fact]
    public void A_project_keeps_what_its_engine_has_and_gets_the_foundation_its_engine_lacks()
    {
        var engineHas = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["MotionForge"] = "0.4.1" };
        var inputs = Fixtures.Inputs() with { TargetKind = "project", FoundationInProject = true, ProvidedElsewhere = engineHas };
        var plan = Fixtures.DefaultPlan("animation", inputs);

        Assert.DoesNotContain("MotionForge", plan.Ids);                                   // stays in the engine
        var here = plan.Items.Single(i => i.Id == "MotionForge");
        Assert.Equal((PlanAction.Current, "0.4.1"), (here.Action, here.InstalledVersion));
        Assert.Equal(["AutomationForgeHub", "AutomationForge"], plan.Ids.Take(2));          // the foundation, into the project

        // An engine with the foundation: the project does not get a second copy.
        var full = new Dictionary<string, string>(engineHas, StringComparer.OrdinalIgnoreCase) { ["AutomationForgeHub"] = "0.2.5", ["AutomationForge"] = "0.1.1" };
        var withFoundation = Fixtures.DefaultPlan("animation", inputs with { ProvidedElsewhere = full });
        Assert.DoesNotContain("AutomationForgeHub", withFoundation.Ids);
        Assert.DoesNotContain("AutomationForge", withFoundation.Ids);
        Assert.True(withFoundation.ComponentCount > 0);
    }

    [Fact]
    public void Something_a_project_has_from_elsewhere_is_installed_there_and_never_an_update()
    {
        var inputs = Fixtures.Inputs() with
        {
            TargetKind = "project",
            ProvidedElsewhere = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["MotionForge"] = "0.0.1" },
        };
        var p = inputs.Catalogue.Product("motionforge")!;
        Assert.Equal(ProductStatus.Installed, TrackPlanner.StatusOf(p, inputs));   // not Update: the engine's copy updates with the engine
        Assert.Null(TrackPlanner.NewerVersion("MotionForge", inputs));
        Assert.Equal(0, UpdatePlanner.Build(inputs).Count);
    }

    [Fact]
    public void The_editor_plugin_goes_into_a_project_only_when_its_engine_lacks_it()
    {
        var m = Fixtures.Manifest();
        Assert.Equal(["MotionForge"], m.WithHubPlugin(["MotionForge"], "project"));                                        // before beta.3, and without knowing
        Assert.Equal(["AutomationForgeHub", "MotionForge"], m.WithHubPlugin(["MotionForge"], "project", new HashSet<string>()));
        Assert.Equal(["MotionForge"], m.WithHubPlugin(["MotionForge"], "project", new HashSet<string> { "AutomationForgeHub" }));
        Assert.Equal(["AutomationForgeHub", "MotionForge"], m.WithHubPlugin(["MotionForge"], "engine"));
    }
}
