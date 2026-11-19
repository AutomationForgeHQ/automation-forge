using Xunit;

namespace Forge.Core.Tests;

public class HubStateTests
{
    private static string Temp() => Path.Combine(Directory.CreateTempSubdirectory("forge-state-").FullName, "hub-state.json");

    [Fact]
    public void Missing_or_broken_file_is_an_empty_state()
    {
        var file = Temp();
        var s = HubState.Load(file);
        Assert.False(s.Existed);
        Assert.Null(s.AgentAccess);
        Assert.False(s.AgentsOn);
        File.WriteAllText(file, "{ broken");
        Assert.False(HubState.Load(file).Existed);
    }

    [Fact]
    public void A_profile_with_a_toolset_installed_comes_up_with_agents_on()
    {
        var file = Temp();
        var s = HubState.Load(file);
        Assert.True(s.MigrateIfNew(["MotionForge", "MotionForgeToolset"], id => id.EndsWith("Toolset"), file));
        Assert.True(HubState.Load(file).AgentsOn);
        Assert.Contains("\"agentAccess\": true", File.ReadAllText(file));
    }

    [Fact]
    public void A_profile_without_agent_components_leaves_the_choice_unset_and_migrates_once()
    {
        var file = Temp();
        var s = HubState.Load(file);
        Assert.True(s.MigrateIfNew(["MotionForge"], id => id.EndsWith("Toolset"), file));
        var again = HubState.Load(file);
        Assert.Null(again.AgentAccess);
        Assert.False(again.MigrateIfNew(["MotionForgeToolset"], id => id.EndsWith("Toolset"), file));
        Assert.Null(HubState.Load(file).AgentAccess);
    }

    [Fact]
    public void Tracks_and_choices_round_trip()
    {
        var file = Temp();
        var s = HubState.Load(file);
        s.AgentAccess = true;
        s.LastTarget = @"C:\Engine\Plugins\Marketplace\AutomationForge";
        s.RecordTrack("animation", s.LastTarget, ["motion", "gameplay"],
            new Dictionary<string, IReadOnlyCollection<string>> { ["motion"] = ["motionforge-kimodo"] });
        s.Pins["FaceForge"] = "0.2.3";
        s.Save(file);

        var back = HubState.Load(file);
        Assert.True(back.AgentsOn);
        var t = back.Track("animation", @"c:\engine\plugins\marketplace\automationforge");
        Assert.NotNull(t);
        Assert.Equal(["motion", "gameplay"], t!.Capabilities);
        Assert.Equal(["motionforge-kimodo"], t.Engines["MOTION"]);
        Assert.Equal("0.2.3", back.Pins["faceforge"]);
    }
}
