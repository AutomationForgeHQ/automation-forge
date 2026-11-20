using System.Globalization;
using Forge.Core.Engines;
using Forge.Core.Installs;

namespace Forge.Core.Projects;

/// <summary>
/// Discovery, the cache and the per-project reading, in the one shape Kovati Hub and `forge projects` both use, so
/// the command line lists exactly what the window shows.
/// </summary>
public static class ProjectService
{
    /// <summary>The discovery inputs from the Hub's choices and the receipts.</summary>
    public static ProjectDiscoveryInputs InputsFor(HubState state, InstallState receipts) => new()
    {
        UserFolders = state.ProjectFolders.ToList(),
        ReceiptRoots = receipts.Items.Where(i => i.TargetKind == "project").Select(i => i.TargetRoot).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        Remembered = state.Projects.Select(p => p.Path).ToList(),
    };

    public static ProjectCache ToCache(ProjectDiscovery d, DateTime now) => new()
    {
        ScannedAt = now.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
        Projects = d.Projects.Select(p => new CachedProject
        {
            Path = p.Path,
            Sources = SourceNames(p.Sources).ToList(),
            LastOpened = p.LastOpened?.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
        }).ToList(),
        Folders = d.Scans.Select(s => new CachedFolder { Folder = s.Folder, Found = s.Found.Count, Line = s.Line, Complete = s.Complete }).ToList(),
    };

    /// <summary>The cached projects that are still there.</summary>
    public static IReadOnlyList<FoundProject> FromCache(ProjectCache? cache) =>
        cache is null ? [] : cache.Projects
            .Where(p => File.Exists(p.Path))
            .Select(p => new FoundProject(p.Path, ParseSources(p.Sources),
                DateTime.TryParse(p.LastOpened, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null))
            .ToList();

    private static readonly (ProjectSource flag, string name)[] SourceTable =
    [
        (ProjectSource.Recent, "recent"), (ProjectSource.CreatedFolder, "created-folder"), (ProjectSource.UserFolder, "user-folder"),
        (ProjectSource.Receipt, "receipt"), (ProjectSource.Remembered, "remembered"),
    ];

    public static IEnumerable<string> SourceNames(ProjectSource s) => SourceTable.Where(x => s.HasFlag(x.flag)).Select(x => x.name);

    public static ProjectSource ParseSources(IEnumerable<string> names) =>
        names.Aggregate(ProjectSource.None, (acc, n) => acc | SourceTable.FirstOrDefault(x => x.name == n).flag);

    /// <summary>"Unreal's recent projects · your folder", for the Projects page.</summary>
    public static string SourcesLine(ProjectSource s)
    {
        var words = new List<string>();
        if (s.HasFlag(ProjectSource.Recent)) words.Add("Unreal's recent projects");
        if (s.HasFlag(ProjectSource.CreatedFolder)) words.Add("Unreal's project folder");
        if (s.HasFlag(ProjectSource.UserFolder)) words.Add("your project folders");
        if (s.HasFlag(ProjectSource.Receipt)) words.Add("installed into before");
        if (s.HasFlag(ProjectSource.Remembered)) words.Add("chosen in Kovati Hub");
        return string.Join(" · ", words);
    }

    /// <summary>
    /// What a project has from somewhere other than Kovati Hub's own folder in it: its engine's copies, and copies of
    /// its own elsewhere under Plugins. The planner counts them as here (<see cref="Catalogue.PlanInputs.ProvidedElsewhere"/>).
    /// </summary>
    public static Dictionary<string, string> ProvidedElsewhere(ProjectReport report)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in report.Plugins)
        {
            if (p.ProjectCopy is { ManagedByHub: false } own) map[p.Id] = own.Version ?? "";
            else if (p.ProjectCopy is null && p.EngineCopy is { } e) map[p.Id] = e.Version ?? "";
        }
        return map;
    }

    /// <summary>
    /// The version string a project's engine has builds under ("5.8"), or the reason there is none: a GUID is
    /// resolved through the registry, never taken for a version.
    /// </summary>
    public static (string? Version, string? Problem) EngineVersionOf(string uproject, IReadOnlyList<EngineInstall> engines, IReadOnlyDictionary<string, string>? registry = null)
    {
        var descriptor = new ProjectDescriptor(uproject);
        var resolved = EngineAssociations.Resolve(descriptor.EngineAssociation, descriptor.Path, engines, registry ?? EngineLocator.RegisteredBuilds());
        if (resolved.Version is { } v) return (v, null);
        return (null, resolved.Kind switch
        {
            AssociationKind.SourceBuild => $"{descriptor.Name} uses a source build ({resolved.Association}) that is not registered on this computer. Name the engine with --engine.",
            _ => $"{descriptor.Name} does not say which Unreal Engine it uses. Name the engine with --engine.",
        });
    }
}
