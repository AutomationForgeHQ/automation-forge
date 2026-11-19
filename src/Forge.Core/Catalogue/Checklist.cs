using Forge.Core.Machine;

namespace Forge.Core.Catalogue;

/// <summary>What the checklist asks of the machine. The window answers from the vault, Docker and the .uproject files; tests answer from a table.</summary>
public interface IMachineFacts
{
    bool KeySet(DeclaredKey key);
    DockerState Docker();
    ContainerState Runner(DeclaredRunner runner);
    /// <summary>Whether the project has the plugin enabled; null when the project cannot be read.</summary>
    bool? ProjectEnabled(string uproject, string plugin);
}

public enum StepKind { Installed, Project, Engine, Key, FirstResult }

/// <summary>What a line's button does: add a key, get Docker Desktop, or nothing.</summary>
public enum LineAction { None, AddKey, GetDocker }

/// <summary>One need inside a step (4.5): met or not, or information that never ticks and does not hold up the count.</summary>
public sealed record ChecklistLine(string Text, bool Met, bool Info = false, LineAction Action = LineAction.None, string? KeyId = null);

/// <summary>One installed engine of a capability, titled by where it runs, with a line per need.</summary>
public sealed record EngineBlock(string Title, string Product, IReadOnlyList<ChecklistLine> Lines, IReadOnlyList<ChecklistLine> Optional)
{
    /// <summary>Every need that is not optional and not information is met.</summary>
    public bool Ready => Lines.Where(l => !l.Info).All(l => l.Met);
}

public sealed record ChecklistStep(
    StepKind Kind,
    string Title,
    string? Line,
    bool Done,
    string ShortName,
    IReadOnlyList<EngineBlock> Engines,
    IReadOnlyList<ChecklistLine> Lines,
    string? Capability = null,
    string? DoneLine = null);

public sealed record Checklist(string Track, IReadOnlyList<ChecklistStep> Steps)
{
    public int DoneCount => Steps.Count(s => s.Done);
    public int Total => Steps.Count;
    public bool AllDone => Steps.All(s => s.Done);
    public int Left => Total - DoneCount;
    public ChecklistStep? Next => Steps.FirstOrDefault(s => !s.Done);
    /// <summary>"1 of 4", the header's count.</summary>
    public string CountText => $"{DoneCount} of {Total}";
}

/// <summary>
/// The set-up checklist of 4.5, built per track from what is installed, never per plugin. Every step ticks
/// itself by looking at the machine: the person never ticks anything.
/// </summary>
public static class ChecklistBuilder
{
    public static Checklist Build(
        Track track,
        Catalogue cat,
        IReadOnlyDictionary<string, string> installed,
        IReadOnlyDictionary<string, PluginSurface> surfaces,
        IReadOnlyList<string> projects,
        bool firstResultOpened,
        string menuName,
        IMachineFacts facts)
    {
        var steps = new List<ChecklistStep>();
        var components = TrackComponents(track, cat, installed);

        // 1. Installed.
        steps.Add(new ChecklistStep(StepKind.Installed, "Installed", Words.Count(components.Count, "component"), true, "installed", [], []));

        // 2. Turn the tools on in a project.
        var onIn = projects.FirstOrDefault(p => components.All(id => facts.ProjectEnabled(p, id) == true));
        steps.Add(new ChecklistStep(StepKind.Project, "Turn the tools on in a project",
            "Unreal only loads a tool in projects where it is on.", onIn is not null, "turn the tools on in a project", [], [],
            DoneLine: onIn is null ? null : $"On in {Path.GetFileNameWithoutExtension(onIn)}. Restart Unreal if it is open."));

        var installedCaps = track.Capabilities
            .Where(c => cat.Product(c.Product)?.MainComponent is { } main && installed.ContainsKey(main))
            .ToList();

        // 3. An engine for each installed capability that has engines installed.
        foreach (var cap in installedCaps)
        {
            var blocks = new List<EngineBlock>();
            foreach (var e in cap.Engines)
            {
                if (cat.Product(e.Product) is not { } ep || ep.MainComponent is not { } main || !installed.ContainsKey(main)) continue;
                blocks.Add(EngineBlockFor(ep, cat, surfaces.GetValueOrDefault(main), menuName, facts));
            }
            if (blocks.Count == 0) continue;
            var word = FirstWord(cap.Name);
            steps.Add(new ChecklistStep(StepKind.Engine, $"Set up an engine for {word}", "One is enough.",
                blocks.Any(b => b.Ready), $"set up an engine for {word}", blocks, [], cap.Id));
        }

        // 4. A tool that is not an engine and declares a key it needs (SpeechForge DeepL).
        foreach (var cap in installedCaps)
        {
            if (cat.Product(cap.Product) is not { } p || p.IsEngine || p.MainComponent is not { } main) continue;
            if (surfaces.GetValueOrDefault(main) is not { } surface) continue;
            var keys = surface.Keys.Where(k => !k.Optional && !k.IsGeneral).ToList();
            if (keys.Count == 0) continue;
            var lines = keys.Select(k => new ChecklistLine(facts.KeySet(k) ? $"{k.DisplayName} key set" : $"{k.DisplayName} key",
                facts.KeySet(k), Action: LineAction.AddKey, KeyId: k.Id)).ToList();
            var name = keys.Count == 1 ? keys[0].DisplayName : "its";
            steps.Add(new ChecklistStep(StepKind.Key, $"{cap.Name} needs your {name} key", null, lines.All(l => l.Met),
                $"add your {name} key", [], lines, cap.Id));
        }

        // 5. Make your first result, when the first installed capability's product has a guide.
        if (installedCaps.FirstOrDefault(c => cat.Product(c.Product)?.Docs is not null) is { } first)
        {
            var thing = Thing(first.Name);
            steps.Add(new ChecklistStep(StepKind.FirstResult, $"Make your first {thing}", null, firstResultOpened,
                $"make your first {thing}", [], [], first.Id));
        }

        return new Checklist(track.Slug, steps);
    }

    /// <summary>Every installed component of the track: its tools, their engines, their agent access, and the editor plugin.</summary>
    public static IReadOnlyList<string> TrackComponents(Track track, Catalogue cat, IReadOnlyDictionary<string, string> installed)
    {
        var ids = new List<string>();
        foreach (var a in cat.Always)
            foreach (var id in new[] { a.Plugin }.Concat(a.AgentPlugins))
                if (installed.ContainsKey(id) && !ids.Contains(id, StringComparer.OrdinalIgnoreCase)) ids.Add(id);
        foreach (var cap in track.Capabilities)
            foreach (var slug in new[] { cap.Product }.Concat(cap.Engines.Select(e => e.Product)))
                if (cat.Product(slug) is { } p)
                    foreach (var id in p.Plugins.Concat(p.AgentPlugins))
                        if (installed.ContainsKey(id) && !ids.Contains(id, StringComparer.OrdinalIgnoreCase)) ids.Add(id);
        return ids;
    }

    private static EngineBlock EngineBlockFor(Product engine, Catalogue cat, PluginSurface? surface, string menu, IMachineFacts facts)
    {
        var lines = new List<ChecklistLine>();
        var optional = new List<ChecklistLine>();
        if (surface is not null)
        {
            foreach (var k in surface.Keys)
            {
                var set = facts.KeySet(k);
                var line = new ChecklistLine(set ? $"{k.DisplayName} set" : k.DisplayName, set, Action: LineAction.AddKey, KeyId: k.Id);
                (k.Optional ? optional : lines).Add(line);
            }
            if (surface.Runners.Count > 0)
            {
                var docker = facts.Docker();
                lines.Insert(0, new ChecklistLine(docker == DockerState.NotInstalled ? "Docker Desktop not found" : "Docker Desktop found",
                    docker != DockerState.NotInstalled, Action: docker == DockerState.NotInstalled ? LineAction.GetDocker : LineAction.None));
                foreach (var r in surface.Runners)
                {
                    var name = string.IsNullOrWhiteSpace(r.DisplayName) ? r.Id : r.DisplayName;
                    var state = docker == DockerState.Running ? facts.Runner(r) : ContainerState.Unknown;
                    var setUp = state is ContainerState.Running or ContainerState.Stopped;
                    lines.Add(new ChecklistLine(setUp ? $"{name} is set up" : $"{name}: set it up once inside Unreal, in the {menu} menu.", setUp));
                }
            }
        }
        else
        {
            // The component declares nothing to the Hub (MeshForge Cloud): its needs are information only.
            foreach (var n in engine.Needs.Where(n => n.Kind is "key" or "account"))
                (n.Optional ? optional : lines).Add(new ChecklistLine($"{n.Name}. Set its key inside Unreal, in the {menu} menu, under Keys.", false, Info: true));
        }
        return new EngineBlock(engine.Runs ?? engine.Name, engine.Slug, lines, optional);
    }

    private static string FirstWord(string name) => name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? name;

    /// <summary>"Motion from a sentence" → "motion"; "Meshes from a picture" → "mesh"; "Voices" → "voice".</summary>
    public static string Thing(string capability)
    {
        var s = capability;
        var from = s.IndexOf(" from ", StringComparison.OrdinalIgnoreCase);
        if (from > 0) s = s[..from];
        s = s.ToLowerInvariant();
        if (s.EndsWith("shes") || s.EndsWith("ches")) return s[..^2];
        if (s.EndsWith('s') && !s.EndsWith("ss")) return s[..^1];
        return s;
    }
}
