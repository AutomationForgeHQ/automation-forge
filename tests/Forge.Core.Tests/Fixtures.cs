using System.Text.Json;
using Forge.Core.Catalogue;

[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

namespace Forge.Core.Tests;

/// <summary>The shipped catalogue and a frozen manifest, read fresh for each test so a test may change them.</summary>
public static class Fixtures
{
    private static string Dir => Path.Combine(AppContext.BaseDirectory, "fixtures");

    public static string CataloguePath => Path.Combine(Dir, "catalogue.json");

    public static Catalogue.Catalogue Catalogue() => Forge.Core.Catalogue.Catalogue.Parse(File.ReadAllText(CataloguePath), "fixture");

    public static Manifest.Manifest Manifest() =>
        JsonSerializer.Deserialize<Manifest.Manifest>(File.ReadAllText(Path.Combine(Dir, "manifest-2026-11-19.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    public static PlanInputs Inputs(
        Catalogue.Catalogue? catalogue = null,
        bool agents = false,
        bool labs = false,
        IEnumerable<string>? owned = null,
        IDictionary<string, string>? installed = null,
        IDictionary<string, string>? pins = null,
        string engine = "5.8") => new()
        {
            Catalogue = catalogue ?? Catalogue(),
            Manifest = Manifest(),
            Engine = engine,
            AgentAccess = agents,
            ShowLabs = labs,
            Owned = new HashSet<string>(owned ?? [], StringComparer.OrdinalIgnoreCase),
            Installed = new Dictionary<string, string>(installed ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase),
            Pins = new Dictionary<string, string>(pins ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase),
        };

    public static TrackPlan DefaultPlan(string track, PlanInputs inputs)
    {
        var t = inputs.Catalogue.Track(track)!;
        return TrackPlanner.Plan(t, TrackPlanner.DefaultSelection(t, inputs), inputs);
    }
}
