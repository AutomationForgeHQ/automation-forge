using Forge.Core.Manifest;

namespace Forge.Core.Catalogue;

/// <summary>Where one product stands on one target (6.5). The order of the tests is the order of this list.</summary>
public enum ProductStatus
{
    /// <summary>No components; or the manifest does not know its first one; or that one has no version for this engine.</summary>
    Coming,
    /// <summary>Its first component's stage is Labs (0.0.x).</summary>
    Labs,
    /// <summary>A component is paid in the manifest and not on the account.</summary>
    NeedsPurchase,
    Installed,
    Update,
    Available,
}

public enum PlanAction { Install, Update, Current }

/// <summary>Why a component is in a plan: the product itself, one of its engines, its agent access, the editor plugin, or a dependency.</summary>
public enum PlanRole { Product, Engine, Agent, Always, Dependency }

/// <summary>Everything the planner reads. One record so the window and the command line build it the same way.</summary>
public sealed record PlanInputs
{
    public required Catalogue Catalogue { get; init; }
    public required Manifest.Manifest Manifest { get; init; }
    /// <summary>The target's engine version, "5.8".</summary>
    public required string Engine { get; init; }
    public string Channel { get; init; } = Settings.Stable;
    /// <summary>engine or project. Only an engine install carries the editor plugin.</summary>
    public string TargetKind { get; init; } = "engine";
    public IReadOnlySet<string> Owned { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public bool SignedIn { get; init; }
    public bool AgentAccess { get; init; }
    public bool ShowLabs { get; init; }
    /// <summary>Component id to installed version, on this target.</summary>
    public IReadOnlyDictionary<string, string> Installed { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    /// <summary>Component id to pinned version (8.3).</summary>
    public IReadOnlyDictionary<string, string> Pins { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// A project target only: components the project already has from somewhere else, id to version. Its engine's
    /// copies, and copies inside the project that Kovati Hub did not put there (a source tree under Plugins). They
    /// count as here, are never installed into the project a second time (Unreal refuses two plugins of one name),
    /// and are never offered as an update for the project: an engine copy updates with its engine.
    /// </summary>
    public IReadOnlyDictionary<string, string> ProvidedElsewhere { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A project target only: carry the Kovati foundation into the project, each piece its engine does not
    /// already provide (<see cref="ProvidedElsewhere"/>). Kovati Hub and `forge` set it from 0.5.0-beta.3: a
    /// project whose engine has no Kovati menu, keys page or ledger gets them in the project. Off, a project
    /// install carries no foundation, the rule before beta.3.
    /// </summary>
    public bool FoundationInProject { get; init; }

    /// <summary>Installed on the target, or provided to it from elsewhere.</summary>
    public bool Has(string id) => Installed.ContainsKey(id) || ProvidedElsewhere.ContainsKey(id);
}

/// <summary>Whether a component has a build for the chosen Unreal Engine version.</summary>
public enum BuildAvailability
{
    /// <summary>Built for this version.</summary>
    Built,
    /// <summary>Released, but only for other versions: "Not built for Unreal Engine 5.7 yet."</summary>
    NotBuiltForEngine,
    /// <summary>Not released at all, or not in the manifest: Coming soon.</summary>
    NoRelease,
}

/// <summary>What a person ticked on a track page: capability ids, and per capability the engine product slugs.</summary>
public sealed record TrackSelection(IReadOnlyCollection<string> Capabilities, IReadOnlyDictionary<string, IReadOnlyCollection<string>> Engines)
{
    public bool HasCapability(string id) => Capabilities.Contains(id, StringComparer.OrdinalIgnoreCase);

    public bool HasEngine(string capability, string engine) =>
        Engines.TryGetValue(capability, out var list) && list.Contains(engine, StringComparer.OrdinalIgnoreCase);

    public TrackSelection With(string capability, bool on)
    {
        var caps = Capabilities.Where(c => !string.Equals(c, capability, StringComparison.OrdinalIgnoreCase)).ToList();
        if (on) caps.Add(capability);
        return this with { Capabilities = caps };
    }

    public TrackSelection WithEngine(string capability, string engine, bool on)
    {
        var map = Engines.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        var list = (map.TryGetValue(capability, out var l) ? l : []).Where(e => !string.Equals(e, engine, StringComparison.OrdinalIgnoreCase)).ToList();
        if (on) list.Add(engine);
        map[capability] = list;
        return this with { Engines = map };
    }
}

/// <summary>One component in a plan.</summary>
public sealed record PlanItem(
    string Id,
    PlanAction Action,
    string Version,
    string? InstalledVersion,
    long Size,
    PlanRole Role,
    string? Capability,
    string? Product,
    string? NeededBy);

/// <summary>A component that was left out, with the sentence that says why (5.2, last row).</summary>
public sealed record PlanDrop(string Id, string Reason, string Message);

public sealed record TrackPlan
{
    public required string Track { get; init; }
    public required IReadOnlyList<PlanItem> Items { get; init; }
    public required IReadOnlyList<PlanDrop> Drops { get; init; }
    /// <summary>The ticked capabilities that passed step 1.</summary>
    public required IReadOnlyList<string> Capabilities { get; init; }

    public IEnumerable<PlanItem> ToChange => Items.Where(i => i.Action != PlanAction.Current);
    public int ComponentCount => Items.Count(i => i.Action != PlanAction.Current);
    public int AlreadyHere => Items.Count(i => i.Action == PlanAction.Current);
    public long Size => Items.Where(i => i.Action != PlanAction.Current).Sum(i => i.Size);
    public int CapabilityCount => Capabilities.Count;

    /// <summary>Capabilities with at least one component still to install or update: "Install 1 more capability".</summary>
    public int NewCapabilityCount => Capabilities.Count(c => Items.Any(i => i.Action != PlanAction.Current && string.Equals(i.Capability, c, StringComparison.OrdinalIgnoreCase)));

    /// <summary>"2 capabilities · 5 components", the words of the install panel and of the dry run.</summary>
    public string Summary => $"{Words.Count(CapabilityCount, "capability", "capabilities")} · {Words.Count(ComponentCount, "component")}";

    public IReadOnlyList<string> Ids => Items.Where(i => i.Action != PlanAction.Current).Select(i => i.Id).ToList();
}

/// <summary>
/// The planner of 6.5: catalogue, manifest, receipts, ownership and choices in; the components to install
/// out, with what was left out and why. A pure function with no I/O, shared by the window and `forge
/// install --track`, and the thing the unit tests hold to the spec's numbers.
/// </summary>
public static class TrackPlanner
{
    public static ProductStatus StatusOf(Product product, PlanInputs inputs)
    {
        var m = inputs.Manifest;
        if (product.Plugins.Count == 0) return ProductStatus.Coming;
        var first = m.Plugin(product.Plugins[0]);
        var latest = first?.Latest(inputs.Engine, inputs.Channel);
        if (first is null || latest is null) return ProductStatus.Coming;
        if (Stages.Of(latest.Version) == Stage.Labs) return ProductStatus.Labs;
        if (product.Plugins.Any(id => m.Plugin(id) is { IsPaid: true } && !inputs.Owned.Contains(id) && !inputs.Has(id)))
            return ProductStatus.NeedsPurchase;
        if (inputs.Has(product.Plugins[0]))
            return HasUpdate(product, inputs) ? ProductStatus.Update : ProductStatus.Installed;
        return ProductStatus.Available;
    }

    /// <summary>
    /// Whether the product's first component has a build for the target's Unreal Engine version. A product that is
    /// <see cref="ProductStatus.Coming"/> on this version is either not released at all, or released for another
    /// version only; the screens say the second as "Not built for Unreal Engine 5.7 yet."
    /// </summary>
    public static BuildAvailability AvailabilityOf(Product product, PlanInputs inputs) =>
        product.Plugins.Count == 0 ? BuildAvailability.NoRelease : AvailabilityOf(product.Plugins[0], inputs);

    public static BuildAvailability AvailabilityOf(string id, PlanInputs inputs)
    {
        var plugin = inputs.Manifest.Plugin(id);
        if (plugin is null) return BuildAvailability.NoRelease;
        if (plugin.Latest(inputs.Engine, inputs.Channel) is not null) return BuildAvailability.Built;
        return plugin.Versions.Any(v => inputs.Channel == Settings.Nightly || v.Channel == Settings.Stable)
            ? BuildAvailability.NotBuiltForEngine : BuildAvailability.NoRelease;
    }

    /// <summary>"Not built for Unreal Engine 5.7 yet." in the site's plain voice.</summary>
    public static string NotBuiltLine(string engine) => $"Not built for Unreal Engine {engine} yet.";

    /// <summary>Any installed component of the product (its own and its agent access) has a newer version that is not pinned away.</summary>
    public static bool HasUpdate(Product product, PlanInputs inputs) =>
        product.Plugins.Concat(product.AgentPlugins).Any(id => NewerVersion(id, inputs) is not null);

    /// <summary>The newer version offered for an installed component, or null. A pinned component has none.</summary>
    public static string? NewerVersion(string id, PlanInputs inputs)
    {
        if (!inputs.Installed.TryGetValue(id, out var have)) return null;
        if (inputs.Pins.ContainsKey(id)) return null;
        var latest = inputs.Manifest.Plugin(id)?.Latest(inputs.Engine, inputs.Channel);
        return latest is not null && IsNewer(latest.Version, have) ? latest.Version : null;
    }

    public static bool IsNewer(string offered, string installed) =>
        SemVer.TryParse(offered, out var o) && SemVer.TryParse(installed, out var i) ? o > i : offered != installed;

    /// <summary>The stage of a product on this target, from its first component.</summary>
    public static Stage StageOf(Product product, PlanInputs inputs) =>
        product.MainComponent is { } id && inputs.Manifest.Plugin(id)?.Latest(inputs.Engine, inputs.Channel) is { } v
            ? Stages.Of(v.Version)
            : Stage.Coming;

    /// <summary>A Labs engine is Labs by the catalogue's mark or by its own stage.</summary>
    public static bool IsLabsEngine(EngineRef engine, PlanInputs inputs) =>
        engine.Labs || inputs.Catalogue.Product(engine.Product) is { } p && StageOf(p, inputs) == Stage.Labs;

    private static bool Takeable(ProductStatus s, bool labsAllowed) =>
        s is ProductStatus.Available or ProductStatus.Installed or ProductStatus.Update
        || (s == ProductStatus.Labs && labsAllowed);

    /// <summary>
    /// What a track ticks by itself (6.1): every capability that can be installed for nothing more than the
    /// person already has (free, released for this engine, not Labs; paid ones the account owns), and the
    /// engines marked default that can be installed and are not Labs.
    /// </summary>
    public static TrackSelection DefaultSelection(Track track, PlanInputs inputs)
    {
        var caps = new List<string>();
        var engines = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var cap in track.Capabilities)
        {
            if (inputs.Catalogue.Product(cap.Product) is not { } product) continue;
            if (!Takeable(StatusOf(product, inputs), labsAllowed: false)) continue;
            caps.Add(cap.Id);
            engines[cap.Id] = cap.Engines
                .Where(e => e.Default && !IsLabsEngine(e, inputs)
                            && inputs.Catalogue.Product(e.Product) is { } ep && Takeable(StatusOf(ep, inputs), labsAllowed: false))
                .Select(e => e.Product)
                .ToList();
        }
        return new TrackSelection(caps, engines);
    }

    public static TrackPlan Plan(Track track, TrackSelection selection, PlanInputs inputs)
    {
        var cat = inputs.Catalogue;
        var m = inputs.Manifest;
        var drops = new List<PlanDrop>();
        var passed = new List<string>();

        // (id, role, capability, product) in the order they are met: products, then their engines, then agent access.
        var wanted = new List<(string id, PlanRole role, string? cap, string? product)>();
        var taken = new List<(Product product, string cap)>();

        // 1 and 2: the ticked capabilities whose product can be had, and their ticked engines.
        foreach (var cap in track.Capabilities)
        {
            if (!selection.HasCapability(cap.Id)) continue;
            if (cat.Product(cap.Product) is not { } product) continue;
            var status = StatusOf(product, inputs);
            if (!Takeable(status, inputs.ShowLabs)) continue;
            passed.Add(cap.Id);
            taken.Add((product, cap.Id));
            foreach (var id in product.Plugins) wanted.Add((id, PlanRole.Product, cap.Id, product.Slug));

            foreach (var engine in cap.Engines)
            {
                if (!selection.HasEngine(cap.Id, engine.Product)) continue;
                if (cat.Product(engine.Product) is not { } ep) continue;
                if (IsLabsEngine(engine, inputs) && !inputs.ShowLabs)
                {
                    foreach (var id in ep.Plugins)
                        drops.Add(new PlanDrop(id, "labs", $"{ep.Name} is in Labs. Turn on Show Labs to install it."));
                    continue;
                }
                if (!Takeable(StatusOf(ep, inputs), inputs.ShowLabs)) continue;
                taken.Add((ep, cap.Id));
                foreach (var id in ep.Plugins) wanted.Add((id, PlanRole.Engine, cap.Id, ep.Slug));
            }
        }

        // 3: agent access for everything taken, and for this track's products that are already installed.
        if (inputs.AgentAccess)
        {
            var agentSources = taken.ToList();
            foreach (var cap in track.Capabilities)
            {
                foreach (var slug in new[] { cap.Product }.Concat(cap.Engines.Select(e => e.Product)))
                {
                    if (cat.Product(slug) is not { } p || p.MainComponent is not { } main) continue;
                    if (!inputs.Installed.ContainsKey(main)) continue;
                    if (agentSources.Any(t => t.product.Slug == p.Slug)) continue;
                    agentSources.Add((p, cap.Id));
                }
            }
            foreach (var (p, capId) in agentSources)
                foreach (var id in p.AgentPlugins) wanted.Add((id, PlanRole.Agent, capId, p.Slug));
        }

        // 4: close over the manifest's dependencies, dependencies first, no duplicates.
        var order = new List<(string id, PlanRole role, string? cap, string? product, string? neededBy)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in wanted)
        {
            if (m.Plugin(w.id) is not { } plugin)
            {
                drops.Add(new PlanDrop(w.id, "unknown", $"{w.id} is not in the list of tools yet."));
                continue;
            }
            foreach (var p in m.Closure(plugin))
            {
                if (!seen.Add(p.Id)) continue;
                var own = string.Equals(p.Id, w.id, StringComparison.OrdinalIgnoreCase);
                order.Add(own
                    ? (p.Id, w.role, w.cap, w.product, null)
                    : (p.Id, PlanRole.Dependency, w.cap, w.product, NameOf(cat, w.product) ?? w.id));
            }
        }

        // 5: nothing paid that the account does not hold, nor anything that depends on it.
        var dropped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in order)
        {
            var p = m.Plugin(o.id)!;
            if (p.IsPaid && !inputs.Owned.Contains(p.Id) && !inputs.Has(p.Id))
            {
                dropped.Add(p.Id);
                drops.Add(new PlanDrop(p.Id, "not-owned", $"{Display(cat, p.Id)} is not on your account."));
            }
        }

        // 6: nothing without a version for this engine. What a project already has from its engine needs none.
        foreach (var o in order)
        {
            if (dropped.Contains(o.id) || inputs.ProvidedElsewhere.ContainsKey(o.id)) continue;
            if (m.Plugin(o.id)!.Latest(inputs.Engine, inputs.Channel) is null)
            {
                dropped.Add(o.id);
                drops.Add(new PlanDrop(o.id, "no-build", $"{Display(cat, o.id)} has no build for Unreal Engine {inputs.Engine} yet."));
            }
        }

        // ...and what depends on something dropped goes too, said in the words of 5.2.
        bool changed;
        do
        {
            changed = false;
            foreach (var o in order)
            {
                if (dropped.Contains(o.id)) continue;
                var blocker = m.Plugin(o.id)!.Dependencies.FirstOrDefault(dropped.Contains);
                if (blocker is null) continue;
                dropped.Add(o.id);
                drops.Add(new PlanDrop(o.id, "dependant", o.role == PlanRole.Agent
                    ? $"Agent access for {Display(cat, blocker)} was left out, because {Display(cat, blocker)} is not on your account."
                    : $"{Display(cat, o.id)} was left out, because {Display(cat, blocker)} could not be installed."));
                changed = true;
            }
        } while (changed);

        var kept = order.Where(o => !dropped.Contains(o.id)).ToList();

        // 7: the foundation first (catalogue `always`): the editor plugin, the shared ledger, and with agent access
        // on their agent components, each with what it needs. A piece that cannot be had for this engine (no build,
        // paid and not owned) is left out quietly, with whatever needs it. On a project the same foundation goes
        // into the project, except what the project's engine already has: then the engine's copy serves it.
        if ((inputs.TargetKind == "engine" || inputs.TargetKind == "project" && inputs.FoundationInProject) && kept.Count > 0)
        {
            var foundation = FoundationItems(inputs);
            var ids = new HashSet<string>(foundation.Select(f => f.id), StringComparer.OrdinalIgnoreCase);
            kept.RemoveAll(o => ids.Contains(o.id));
            kept.InsertRange(0, foundation);
        }

        // 8: install, update or current, by the receipts. A pinned component that is installed is current; so is
        // one the project has from its engine or from a copy of its own.
        var items = kept.Select(o =>
        {
            var p = m.Plugin(o.id)!;
            if (!inputs.Installed.ContainsKey(p.Id) && inputs.ProvidedElsewhere.TryGetValue(p.Id, out var elsewhere))
                return new PlanItem(p.Id, PlanAction.Current, elsewhere, elsewhere, 0, o.role, o.cap, o.product, o.neededBy);
            var latest = p.Latest(inputs.Engine, inputs.Channel)!;
            inputs.Installed.TryGetValue(p.Id, out var have);
            var action = have is null ? PlanAction.Install
                : inputs.Pins.ContainsKey(p.Id) || !IsNewer(latest.Version, have) ? PlanAction.Current
                : PlanAction.Update;
            return new PlanItem(p.Id, action, latest.Version, have, latest.Size, o.role, o.cap, o.product, o.neededBy);
        }).ToList();

        return new TrackPlan { Track = track.Slug, Items = items, Drops = drops, Capabilities = passed };
    }

    /// <summary>
    /// The foundation components for this engine, dependencies first: each `always` entry, and its agent
    /// components when agent access is on. With no `always` list, the editor plugin alone.
    /// </summary>
    public static List<(string id, PlanRole role, string? cap, string? product, string? neededBy)> FoundationItems(PlanInputs inputs)
    {
        var cat = inputs.Catalogue;
        var m = inputs.Manifest;
        var entries = cat.Always.Count > 0 ? cat.Always : [new AlwaysEntry { Plugin = Manifest.Manifest.HubPlugin, Name = "Kovati Hub link" }];
        var list = new List<(string id, PlanRole role, string? cap, string? product, string? neededBy)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool CanHave(PluginInfo p) =>
            inputs.ProvidedElsewhere.ContainsKey(p.Id)
            || p.Latest(inputs.Engine, inputs.Channel) is not null
               && !(p.IsPaid && !inputs.Owned.Contains(p.Id) && !inputs.Installed.ContainsKey(p.Id));
        foreach (var a in entries)
            foreach (var id in new[] { a.Plugin }.Concat(inputs.AgentAccess ? a.AgentPlugins : []))
            {
                if (m.Plugin(id) is not { } plugin) continue;
                var closure = m.Closure(plugin);
                if (!closure.All(CanHave)) continue;
                foreach (var p in closure)
                    if (seen.Add(p.Id)) list.Add((p.Id, PlanRole.Always, null, null, string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase) ? null : a.Name));
            }
        return list;
    }

    private static string? NameOf(Catalogue cat, string? slug) => slug is null ? null : cat.Product(slug)?.Name;

    /// <summary>A component by the name a person knows it by: its product's name, else its id.</summary>
    private static string Display(Catalogue cat, string id) =>
        cat.Products.FirstOrDefault(p => p.Plugins.Contains(id, StringComparer.OrdinalIgnoreCase))?.Name ?? id;
}

/// <summary>The facts about a product that the screens show, decided in one place (6.6).</summary>
public static class ProductFacts
{
    /// <summary>
    /// The price word key: the catalogue's price, with one guard. A free-tier product whose free tier is not
    /// live shows "paid" when its component is paid in the manifest, and "free-tier" when it has no release yet.
    /// </summary>
    public static string PriceKey(Product product, Manifest.Manifest manifest)
    {
        if (product.Price == "free-tier" && product.FreeTierLive != true)
        {
            var paid = product.Plugins.Any(id => manifest.Plugin(id) is { IsPaid: true } p && p.Versions.Count > 0);
            return paid ? "paid" : "free-tier";
        }
        return product.Price;
    }

    /// <summary>"A free tier is planned. Today this is the full version." applies.</summary>
    public static bool FreeTierPromised(Product product, Manifest.Manifest manifest) =>
        product.Price == "free-tier" && product.FreeTierLive != true && PriceKey(product, manifest) == "paid";
}

/// <summary>The house rules for numbers in sentences (section 2).</summary>
public static class Words
{
    public static string Count(int n, string one, string? many = null) => n == 1 ? $"1 {one}" : $"{n} {many ?? one + "s"}";

    /// <summary>"0.4 MB", "12 MB", "1.2 GB": one decimal below 10, none above, GB from 1,000 MB.</summary>
    public static string Size(long bytes)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var mb = bytes / 1_000_000.0;
        if (mb >= 1000)
        {
            var gb = mb / 1000;
            return gb < 10 ? string.Create(inv, $"{gb:0.0} GB") : string.Create(inv, $"{gb:0} GB");
        }
        return mb < 10 ? string.Create(inv, $"{mb:0.0} MB") : string.Create(inv, $"{mb:0} MB");
    }

    /// <summary>"$49.99" from an amount in cents; "$69" when there are none (4.6).</summary>
    public static string Price(long cents) => cents % 100 == 0 ? $"${cents / 100}" : $"${cents / 100}.{cents % 100:00}";
}
