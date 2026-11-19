using Forge.Core.Manifest;

namespace Forge.Core.Catalogue;

/// <summary>One component that changes in an update: "Kimodo engine 0.5.1 → 0.5.2".</summary>
public sealed record UpdatePart(string Id, string Name, string From, string To, long Size);

/// <summary>
/// One row of the Updates tab (8.1): a product with at least one newer component, counted once however many of
/// its components change. A component the catalogue does not know is a row of its own under OTHER TOOLS.
/// </summary>
public sealed record ProductUpdate(
    Product? Product,
    string? Track,
    string Title,
    string Subtitle,
    IReadOnlyList<UpdatePart> Parts,
    bool IsLabs,
    bool IsPinned,
    string? PinnedAt,
    bool LeavesLabs)
{
    /// <summary>The product's main component, or the one component of an unknown row.</summary>
    public UpdatePart Main => Parts[0];
    public bool HasManyParts => Parts.Count > 1;
    /// <summary>"MotionForge 0.4.1 → 0.4.2 · Kimodo engine 0.5.1 → 0.5.2 · Agent access 0.3.1 → 0.3.2".</summary>
    public string PartsLine => string.Join(" · ", Parts.Select(p => $"{p.Name} {p.From} → {p.To}"));
    public bool CountsInUpdateAll => !IsLabs && !IsPinned;
}

/// <summary>The Updates tab's rows, the counts and what "Update all" leaves alone (8.1 to 8.4).</summary>
public sealed record UpdateList(IReadOnlyList<ProductUpdate> Rows)
{
    public IEnumerable<ProductUpdate> Regular => Rows.Where(r => r.CountsInUpdateAll);
    public IEnumerable<ProductUpdate> Labs => Rows.Where(r => r.IsLabs && !r.IsPinned);
    public IEnumerable<ProductUpdate> Pinned => Rows.Where(r => r.IsPinned);

    /// <summary>The header pill and Home: products with a newer component, without pinned and without Labs.</summary>
    public int Count => Regular.Count();

    /// <summary>The components "Update all" takes: every regular row's parts.</summary>
    public IReadOnlyList<string> UpdateAllIds => Regular.SelectMany(r => r.Parts.Select(p => p.Id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>"Updates 3 tools. 1 Labs update and 1 pinned tool are left as they are." (8.2)</summary>
    public string UpdateAllTip
    {
        get
        {
            var labs = Labs.Count();
            var pinned = Pinned.Count();
            var line = $"Updates {Words.Count(Count, "tool")}.";
            if (labs == 0 && pinned == 0) return line;
            var left = new List<string>();
            if (labs > 0) left.Add(Words.Count(labs, "Labs update"));
            if (pinned > 0) left.Add(Words.Count(pinned, "pinned tool"));
            return $"{line} {string.Join(" and ", left)} {(labs + pinned == 1 ? "is left as it is" : "are left as they are")}.";
        }
    }
}

public static class UpdatePlanner
{
    /// <summary>
    /// Every installed product with a newer component on the target, and every installed component the
    /// catalogue does not know that has one. Pins are respected: a pinned product is listed apart, never
    /// counted. A product whose first component is Labs (0.0.x) is listed under LABS.
    /// </summary>
    public static UpdateList Build(PlanInputs inputs, IReadOnlyDictionary<string, string>? pins = null)
    {
        var cat = inputs.Catalogue;
        var m = inputs.Manifest;
        pins ??= inputs.Pins;
        var rows = new List<ProductUpdate>();
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var noPins = inputs with { Pins = new Dictionary<string, string>() };

        // Tools first, so an engine folds into the tool it extends: MotionForge, its Kimodo engine and its agent
        // access changing together are one update. An engine whose tool is not installed is a row of its own.
        foreach (var p in cat.Products.OrderBy(x => x.IsEngine ? 1 : 0))
        {
            if (p.MainComponent is not { } main || !inputs.Installed.ContainsKey(main)) continue;
            // The tool, its engines, then agent access: "MotionForge · Kimodo engine · Agent access". A Labs engine
            // (TRELLIS under MeshForge) is not folded in: Labs updates never ride along in "Update all" (8.4).
            var engines = p.IsEngine ? [] : cat.Products
                .Where(e => e.IsEngine && string.Equals(e.Extends, p.Slug, StringComparison.OrdinalIgnoreCase)
                            && e.MainComponent is { } em && inputs.Installed.ContainsKey(em) && TrackPlanner.StageOf(e, inputs) != Stage.Labs)
                .ToList();
            var members = new List<(Product owner, string id)>();
            members.AddRange(p.Plugins.Select(id => (p, id)));
            foreach (var e in engines) members.AddRange(e.Plugins.Select(id => (e, id)));
            members.AddRange(p.AgentPlugins.Select(id => (p, id)));
            foreach (var e in engines) members.AddRange(e.AgentPlugins.Select(id => (e, id)));
            var parts = new List<UpdatePart>();
            foreach (var (owner, id) in members)
            {
                if (!inputs.Installed.ContainsKey(id) || !claimed.Add(id)) continue;
                if (TrackPlanner.NewerVersion(id, noPins) is not { } to) continue;
                parts.Add(new UpdatePart(id, PartName(cat, p, owner, id), inputs.Installed[id], to, m.Plugin(id)?.Latest(inputs.Engine, inputs.Channel)?.Size ?? 0));
            }
            if (parts.Count == 0) continue;
            // The main component first.
            parts.Sort((a, b) => string.Equals(a.Id, main, StringComparison.OrdinalIgnoreCase) ? -1 : string.Equals(b.Id, main, StringComparison.OrdinalIgnoreCase) ? 1 : 0);
            var pinned = pins.TryGetValue(main, out var at);
            var fromStage = Stages.Of(inputs.Installed[main]);
            var toStage = Stages.Of(parts.FirstOrDefault(x => x.Id == main)?.To ?? inputs.Installed[main]);
            var capability = cat.Track(p.Track)?.Capabilities.FirstOrDefault(c => c.Product == p.Slug)?.Name;
            rows.Add(new ProductUpdate(p, cat.Track(p.Track) is null ? null : p.Track, capability ?? p.Name, capability is null ? "" : p.Name, parts,
                IsLabs: toStage == Stage.Labs, IsPinned: pinned, PinnedAt: pinned ? at : null,
                LeavesLabs: fromStage == Stage.Labs && toStage != Stage.Labs));
        }

        // Components the catalogue does not name still update (4.9): OTHER TOOLS, by id.
        foreach (var (id, have) in inputs.Installed)
        {
            if (claimed.Contains(id) || cat.Products.Any(p => p.Plugins.Contains(id, StringComparer.OrdinalIgnoreCase) || p.AgentPlugins.Contains(id, StringComparer.OrdinalIgnoreCase))) continue;
            if (cat.FoundationName(id) is { } foundationName)
            {
                if (TrackPlanner.NewerVersion(id, noPins) is { } hubTo)
                    rows.Add(new ProductUpdate(null, null, foundationName, id,
                        [new UpdatePart(id, id, have, hubTo, m.Plugin(id)?.Latest(inputs.Engine, inputs.Channel)?.Size ?? 0)],
                        false, pins.ContainsKey(id), pins.GetValueOrDefault(id), false));
                continue;
            }
            if (TrackPlanner.NewerVersion(id, noPins) is not { } to) continue;
            rows.Add(new ProductUpdate(null, null, id, "", [new UpdatePart(id, id, have, to, m.Plugin(id)?.Latest(inputs.Engine, inputs.Channel)?.Size ?? 0)],
                IsLabs: Stages.Of(to) == Stage.Labs, IsPinned: pins.ContainsKey(id), PinnedAt: pins.GetValueOrDefault(id), LeavesLabs: false));
        }
        return new UpdateList(rows);
    }

    /// <summary>"MotionForge", "Kimodo engine", "Agent access", "Kimodo agent access".</summary>
    private static string PartName(Catalogue cat, Product row, Product owner, string id)
    {
        var agent = owner.AgentPlugins.Contains(id, StringComparer.OrdinalIgnoreCase);
        if (owner == row) return agent ? "Agent access" : owner.IsEngine ? $"{cat.ShortName(owner)} engine" : owner.Name;
        return agent ? $"{cat.ShortName(owner)} agent access" : $"{cat.ShortName(owner)} engine";
    }

    /// <summary>Pin a product: every installed component of it, at its installed version (8.3).</summary>
    public static Dictionary<string, string> PinsFor(Product p, IReadOnlyDictionary<string, string> installed) =>
        p.Plugins.Concat(p.AgentPlugins).Where(installed.ContainsKey)
            .ToDictionary(id => id, id => installed[id], StringComparer.OrdinalIgnoreCase);
}
