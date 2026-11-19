using Forge.Core.Catalogue;
using Forge.Core.Machine;
using Xunit;

namespace Forge.Core.Tests;

/// <summary>The set-up checklist of 4.5 against slice 2's acceptance: "1 of 4" after Animation, and each step ticking itself.</summary>
public class ChecklistTests
{
    private sealed class Facts : IMachineFacts
    {
        public HashSet<string> Keys { get; } = new(StringComparer.OrdinalIgnoreCase);
        public DockerState DockerState { get; set; } = DockerState.NotInstalled;
        public ContainerState RunnerState { get; set; } = ContainerState.Missing;
        public Dictionary<string, HashSet<string>> Projects { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool KeySet(DeclaredKey key) => Keys.Contains(key.Id);
        public DockerState Docker() => DockerState;
        public ContainerState Runner(DeclaredRunner runner) => RunnerState;
        public bool? ProjectEnabled(string uproject, string plugin) =>
            Projects.TryGetValue(uproject, out var on) ? on.Contains(plugin) : null;
    }

    private static readonly Dictionary<string, string> AnimationInstalled = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AutomationForgeHub"] = "0.2.5", ["MotionForge"] = "0.4.1", ["MotionForgeKimodo"] = "0.5.1",
        ["MotionForgeUthana"] = "0.2.1", ["MontageForge"] = "0.1.3",
    };

    private static Dictionary<string, PluginSurface> Surfaces() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["MotionForgeKimodo"] = new("MotionForgeKimodo", "x",
            [
                new DeclaredKey { Id = "HuggingFace", DisplayName = "Hugging Face token", Scope = "general", VaultEntry = "v" },
                new DeclaredKey { Id = "Runpod", DisplayName = "Runpod", Scope = "general", VaultEntry = "v", Optional = true },
            ],
            [new DeclaredRunner { Id = "Kimodo", DisplayName = "Kimodo", ComposeProject = "motionforge-kimodo", Service = "runner" }]),
        ["MotionForgeUthana"] = new("MotionForgeUthana", "y",
            [new DeclaredKey { Id = "MotionForge.Uthana", DisplayName = "Uthana", VaultEntry = "v" }], []),
    };

    private static Checklist Build(Facts facts, IReadOnlyList<string>? projects = null, bool opened = false, Dictionary<string, string>? installed = null)
    {
        var cat = Fixtures.Catalogue();
        return ChecklistBuilder.Build(cat.Track("animation")!, cat, installed ?? AnimationInstalled, Surfaces(), projects ?? [], opened, "Automation Forge", facts);
    }

    [Fact]
    public void After_installing_animation_it_reads_1_of_4()
    {
        var list = Build(new Facts());
        Assert.Equal("1 of 4", list.CountText);
        Assert.Equal([StepKind.Installed, StepKind.Project, StepKind.Engine, StepKind.FirstResult], list.Steps.Select(s => s.Kind));
        Assert.Equal("5 components", list.Steps[0].Line);
        Assert.Equal("Set up an engine for Motion", list.Steps[2].Title);
        Assert.Equal("Make your first motion", list.Steps[3].Title);
        Assert.Equal("turn the tools on in a project", list.Next!.ShortName);
    }

    [Fact]
    public void Docker_absent_reads_not_found_and_the_runner_names_the_menu()
    {
        var engine = Build(new Facts()).Steps.Single(s => s.Kind == StepKind.Engine);
        var kimodo = engine.Engines.Single(b => b.Product == "motionforge-kimodo");
        Assert.Equal("On your graphics card · free per take", kimodo.Title);
        Assert.Equal("Docker Desktop not found", kimodo.Lines[0].Text);
        Assert.Equal(LineAction.GetDocker, kimodo.Lines[0].Action);
        Assert.Contains(kimodo.Lines, l => l.Text == "Kimodo: set it up once inside Unreal, in the Automation Forge menu.");
        Assert.Single(kimodo.Optional);
    }

    [Fact]
    public void One_ready_engine_is_enough_and_a_key_ticks_its_line()
    {
        var facts = new Facts();
        facts.Keys.Add("MotionForge.Uthana");
        var list = Build(facts);
        var engine = list.Steps.Single(s => s.Kind == StepKind.Engine);
        Assert.True(engine.Done);
        Assert.Contains(engine.Engines.Single(b => b.Product == "motionforge-uthana").Lines, l => l.Text == "Uthana set" && l.Met);
        Assert.Equal("2 of 4", list.CountText);
    }

    [Fact]
    public void Kimodo_is_ready_with_docker_the_token_and_its_container()
    {
        var facts = new Facts { DockerState = DockerState.Running, RunnerState = ContainerState.Stopped };
        facts.Keys.Add("HuggingFace");
        var kimodo = Build(facts).Steps.Single(s => s.Kind == StepKind.Engine).Engines.Single(b => b.Product == "motionforge-kimodo");
        Assert.True(kimodo.Ready);
        Assert.Contains(kimodo.Lines, l => l.Text == "Kimodo is set up");
    }

    [Fact]
    public void A_project_with_every_component_on_ticks_the_project_step()
    {
        var facts = new Facts();
        facts.Projects["C:/p/MyGame.uproject"] = new HashSet<string>(AnimationInstalled.Keys.Take(4), StringComparer.OrdinalIgnoreCase);
        Assert.False(Build(facts, ["C:/p/MyGame.uproject"]).Steps[1].Done);
        facts.Projects["C:/p/MyGame.uproject"].Add("MontageForge");
        var step = Build(facts, ["C:/p/MyGame.uproject"]).Steps[1];
        Assert.True(step.Done);
        Assert.Equal("On in MyGame. Restart Unreal if it is open.", step.DoneLine);
    }

    [Fact]
    public void Opening_the_guide_ticks_the_last_step()
    {
        Assert.True(Build(new Facts(), opened: true).Steps.Last().Done);
    }

    [Fact]
    public void Track_components_include_agent_access_and_the_editor_plugin_that_are_installed()
    {
        var installed = new Dictionary<string, string>(AnimationInstalled, StringComparer.OrdinalIgnoreCase) { ["MotionForgeToolset"] = "0.3.1" };
        var cat = Fixtures.Catalogue();
        var ids = ChecklistBuilder.TrackComponents(cat.Track("animation")!, cat, installed);
        Assert.Equal(6, ids.Count);
        Assert.Contains("MotionForgeToolset", ids);
        Assert.Equal("AutomationForgeHub", ids[0]);
    }

    [Theory]
    [InlineData("Motion from a sentence", "motion")]
    [InlineData("Meshes from a picture", "mesh")]
    [InlineData("Voices", "voice")]
    [InlineData("Gameplay clips", "gameplay clip")]
    [InlineData("Terrain", "terrain")]
    public void Thing_names(string capability, string thing) => Assert.Equal(thing, ChecklistBuilder.Thing(capability));
}
