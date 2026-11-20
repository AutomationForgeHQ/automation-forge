using System.CommandLine;
using Forge.Core;
using Forge.Core.Catalogue;
using Forge.Core.Cloud;
using Forge.Core.Engines;
using Forge.Core.Entitlements;
using Forge.Core.Installs;
using Forge.Core.Licence;
using Forge.Core.Manifest;
using Forge.Core.Projects;

// forge — the Kovati installer, as a command line (Kovati Hub's engine room).
//
// What CI, scripts and agents use, and what the hub runs when it needs to write
// somewhere privileged. Nothing installs without an account (`forge login`):
// a free plugin is added to the account as it installs, a paid one is bought
// on the account site first.

var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
http.DefaultRequestHeaders.UserAgent.ParseAdd(AppInfo.UserAgent("cli"));
var manifestClient = new ManifestClient(http);
var state = new InstallState();
var settings = Settings.Load();
var entitlements = new FirebaseEntitlements(http);
var installer = new Installer(http, state, entitlements, line => Console.WriteLine($"  {line}"));

var engineOpt = new Option<string?>("--engine", "-e") { Description = "Engine version (5.8), full version, or path. Default: the newest found." };
var projectOpt = new Option<string?>("--project", "-p") { Description = "Install into this project's Plugins folder instead of an engine." };
var channelOpt = new Option<string>("--channel") { Description = "stable or nightly. Default: the channel chosen in settings (see `forge channel`).", DefaultValueFactory = _ => settings.Channel };
var offlineOpt = new Option<bool>("--offline") { Description = "Use the cached manifest only." };
var forceOpt = new Option<bool>("--force") { Description = "Reinstall even if the same version is present." };
var noElevate = new Option<bool>(Elevation.NoElevateFlag) { Hidden = true, Recursive = true };
var yesOpt = new Option<bool>("--yes") { Description = "With --project: go ahead when it cannot be told whether Unreal Editor has the project open." };

var root = new RootCommand("Kovati: install, update and manage Kovati's tools for Unreal Engine 5.8.");
root.Options.Add(noElevate);

// ── engines ───────────────────────────────────────────────────────────────
var engines = new Command("engines", "List the Unreal Engine installations found on this machine, and which have Kovati builds.");
engines.Options.Add(offlineOpt); engines.Options.Add(channelOpt);
engines.SetAction(async (parse, ct) =>
{
    var found = EngineLocator.Find();
    Manifest? manifest = null;
    try { (manifest, _) = await manifestClient.GetAsync(parse.GetValue(offlineOpt), ct); }
    catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or InvalidDataException or System.Text.Json.JsonException)
    {
        Console.Error.WriteLine($"  (no list of tools to compare with: {ex.Message})");
    }
    var channel = parse.GetValue(channelOpt)!;
    if (found.Count == 0) Console.WriteLine("No Unreal Engine installations found.");
    foreach (var e in found)
    {
        var builds = manifest is null ? "" : manifest.BuiltFor(e.Version, channel) is var n and > 0 ? $"{n} Kovati tools built for {e.Version}" : $"no Kovati builds for {e.Version} yet";
        var kind = e.IsLauncherBuild ? "" : e.RegistryId is { } id ? $"  (source build {id})" : "  (source build)";
        Console.WriteLine($"  UE {e.FullVersion,-8} {builds,-34} {e.Path}{kind}");
    }
    if (manifest is not null)
    {
        var have = found.Select(e => e.Version).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = manifest.EnginesWithBuilds(channel).Where(v => !have.Contains(v)).ToList();
        if (missing.Count > 0) Console.WriteLine($"  Kovati also builds for Unreal Engine {string.Join(", ", missing)}, which is not installed here.");
    }
    return found.Count == 0 ? 1 : 0;
});
root.Subcommands.Add(engines);

// ── projects ──────────────────────────────────────────────────────────────
var jsonOpt = new Option<bool>("--json") { Description = "Print the list as JSON." };
var projects = new Command("projects",
    "List the Unreal projects Kovati Hub knows, with their engine and their Kovati tools. Found from Unreal's and the Epic launcher's own records, "
    + "the project folders chosen in Kovati Hub's Settings, and the projects installed into before; the disk is never searched.");
projects.Options.Add(jsonOpt); projects.Options.Add(offlineOpt); projects.Options.Add(channelOpt);
projects.SetAction(async (parse, ct) =>
{
    var hubState = HubState.Load();
    var discovery = ProjectLocator.Discover(ProjectService.InputsFor(hubState, state), ct);
    Manifest? manifest = null;
    try { (manifest, _) = await manifestClient.GetAsync(parse.GetValue(offlineOpt), ct); }
    catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or InvalidDataException or System.Text.Json.JsonException) { }
    var channel = parse.GetValue(channelOpt)!;
    var allEngines = EngineLocator.Find();
    var registry = EngineLocator.RegisteredBuilds();
    var ids = new HashSet<string>(manifest?.Plugins.Select(p => p.Id) ?? [], StringComparer.OrdinalIgnoreCase);
    var deps = (manifest?.Plugins ?? []).ToDictionary(p => p.Id, p => (IReadOnlyList<string>)p.Dependencies, StringComparer.OrdinalIgnoreCase);
    var reports = discovery.Projects.Select(p => ProjectInspector.Inspect(p, allEngines, ids, deps, state.Items, registry)).ToList();

    if (parse.GetValue(jsonOpt))
    {
        var doc = new
        {
            sources = discovery.Sources.Select(s => new { source = s.Source, where = s.Where, found = s.Found, note = s.Note }),
            projects = reports.Select(r => new
            {
                name = r.Name, path = r.Path,
                foundIn = ProjectService.SourceNames(r.Found.Sources),
                lastOpened = r.Found.LastOpened?.ToString("yyyy-MM-ddTHH:mm:ss"),
                engineAssociation = r.Engine.Association, engine = r.Engine.Version, engineInstalled = r.Engine.IsInstalled,
                enginePath = r.Engine.Engine?.Path, cpp = r.IsCodeProject, readOnly = r.IsReadOnly, sourceControl = r.SourceControl, error = r.Error,
                plugins = r.Plugins.Select(pl => new
                {
                    id = pl.Id, enabled = pl.Enabled, reason = pl.Reason.ToString(), neededBy = pl.NeededBy, entry = pl.Entry,
                    project = pl.ProjectCopy is { } pc ? new { version = pc.Version, folder = pc.Folder, managedByHub = pc.ManagedByHub } : null,
                    engine = pl.EngineCopy is { } ec ? new { version = ec.Version, folder = ec.Folder } : null,
                    latest = r.Engine.Version is { } v ? manifest?.Plugin(pl.Id)?.Latest(v, channel)?.Version : null,
                }),
            }),
        };
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(doc, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    foreach (var s in discovery.Sources)
        Console.WriteLine($"  {s.Source}: {s.Found}{(s.Note is null ? "" : $" ({s.Note})")}  {s.Where}");
    Console.WriteLine();
    if (reports.Count == 0) { Console.WriteLine("No projects found. Add a folder in Kovati Hub's Settings, Project folders."); return 0; }
    foreach (var r in reports)
    {
        Console.WriteLine($"{r.Name}  ·  {r.Engine.Label}{(r.IsCodeProject ? "  ·  C++" : "")}{(r.SourceControl is { } vcs ? $"  ·  {vcs}" : "")}");
        Console.WriteLine($"  {r.Path}");
        if (r.Error is { } err) { Console.WriteLine($"  {err}"); continue; }
        if (r.Plugins.Count == 0) Console.WriteLine("  no Kovati tools");
        foreach (var pl in r.Plugins)
        {
            var where = pl.Overrides ? "project (overrides the engine's copy)" : pl.InProject ? (pl.ProjectCopy!.ManagedByHub ? "project" : "project, its own copy") : pl.InEngine ? "engine" : "missing";
            var latest = r.Engine.Version is { } v ? manifest?.Plugin(pl.Id)?.Latest(v, channel)?.Version : null;
            var version = pl.Loaded?.Version ?? "";
            var update = latest is not null && version.Length > 0 && TrackPlanner.IsNewer(latest, version) ? $"  update {latest}" : "";
            var onOff = pl.Enabled ? "on " : "off";
            var why = pl.Reason switch
            {
                EnabledReason.Explicit => "",
                EnabledReason.ProjectDefault => pl.Enabled ? " (on by default in the project)" : " (off: its descriptor is off by default)",
                EnabledReason.EngineDefault => pl.Enabled ? " (on by default from the engine)" : " (not turned on in the project)",
                EnabledReason.NeededByEnabled => $" (needed by {pl.NeededBy})",
                _ => " (named in the project, not found)",
            };
            Console.WriteLine($"  {onOff}  {pl.Id,-28} {version,-10} {where}{why}{update}");
        }
    }
    return 0;
});
root.Subcommands.Add(projects);

// ── list ──────────────────────────────────────────────────────────────────
var list = new Command("list", "Show the sets and plugins in the manifest, with what is installed.");
list.Options.Add(engineOpt); list.Options.Add(projectOpt); list.Options.Add(channelOpt); list.Options.Add(offlineOpt);
list.SetAction(async (parse, ct) =>
{
    var (manifest, fromCache) = await manifestClient.GetAsync(parse.GetValue(offlineOpt), ct);
    var target = ResolveTarget(parse.GetValue(engineOpt), parse.GetValue(projectOpt));
    var engineVersion = target.version;
    var channel = parse.GetValue(channelOpt)!;
    var owned = await OwnedOrEmptyAsync(ct);
    Console.WriteLine($"manifest {manifest.GeneratedAt}{(fromCache ? " (cached)" : "")} · target {target.target.Label} · channel {channel}"
                      + (entitlements.IsSignedIn ? $" · account {entitlements.AccountLabel}" : " · not signed in (forge login)"));
    foreach (var set in manifest.Sets)
    {
        Console.WriteLine();
        Console.WriteLine($"{set.Name}");
        foreach (var id in set.Members)
        {
            var p = manifest.Plugin(id); if (p is null) continue;
            var latest = p.Latest(engineVersion, channel);
            var installed = state.Find(target.target, p.Id);
            var status = installed is null ? "" : installed.Version == latest?.Version ? "installed" : $"installed {installed.Version}, update available";
            var avail = latest is null ? "no release yet" : latest.Version;
            var standing = owned.Contains(p.Id) ? "yours" : p.IsPaid ? "paid" : "free";
            Console.WriteLine($"  {p.Id,-28} {p.Role,-9} {standing,-6} {avail,-16} {status}");
        }
    }
    return 0;
});
root.Subcommands.Add(list);

// ── install ───────────────────────────────────────────────────────────────
var whatArg = new Argument<string[]>("plugins") { Description = "Plugin ids or set names (a set installs every member).", Arity = ArgumentArity.ZeroOrMore };
var trackOpt = new Option<string?>("--track") { Description = "Install a track by capability, as Kovati Hub does: characters, animation, worldbuilding or storytelling." };
var onlyOpt = new Option<string?>("--only") { Description = "With --track: only these capabilities, comma separated (e.g. motion,gameplay)." };
var enginesOpt = new Option<string[]>("--engines") { Description = "With --track: the engines of a capability, e.g. motion=motionforge-uthana (repeat per capability).", AllowMultipleArgumentsPerToken = true };
var agentsOpt = new Option<bool>("--agents") { Description = "With --track: also install agent access. Default: the choice saved by Kovati Hub." };
var noAgentsOpt = new Option<bool>("--no-agents") { Description = "With --track: no agent access, whatever Kovati Hub saved." };
var dryRunOpt = new Option<bool>("--dry-run") { Description = "With --track: print the plan and install nothing." };
var install = new Command("install", "Download, verify and install plugins, whole sets, or a track by capability, dependencies first.");
install.Arguments.Add(whatArg);
install.Options.Add(engineOpt); install.Options.Add(projectOpt); install.Options.Add(channelOpt); install.Options.Add(offlineOpt); install.Options.Add(forceOpt);
install.Options.Add(trackOpt); install.Options.Add(onlyOpt); install.Options.Add(enginesOpt); install.Options.Add(agentsOpt); install.Options.Add(noAgentsOpt); install.Options.Add(dryRunOpt);
install.Options.Add(yesOpt);
install.SetAction(async (parse, ct) =>
{
    if (parse.GetValue(trackOpt) is { Length: > 0 } trackName)
        return await InstallTrackAsync(parse, trackName, ct);
    if ((parse.GetValue(whatArg) ?? []).Length == 0) { Console.Error.WriteLine("Name plugins or sets to install, or a track with --track."); return 2; }

    var (manifest, _) = await manifestClient.GetAsync(parse.GetValue(offlineOpt), ct);
    var target = ResolveTarget(parse.GetValue(engineOpt), parse.GetValue(projectOpt));
    // A GUID association is a source build, resolved through the registry: never a version (before beta.3 it was).
    var engineVersion = target.version;
    var channel = parse.GetValue(channelOpt)!;
    if (!GuardProject(target.target, parse.GetValue(yesOpt))) return 6;

    var wanted = new List<PluginInfo>();
    foreach (var name in parse.GetValue(whatArg) ?? [])
    {
        if (manifest.Set(name) is { } set)
            wanted.AddRange(set.Members.Select(m => manifest.Plugin(m)).Where(p => p is not null)!);
        else if (manifest.Plugin(name) is { } p)
            wanted.Add(p);
        else { Console.Error.WriteLine($"Unknown plugin or set: {name}"); return 2; }
    }
    var order = wanted.SelectMany(manifest.Closure).DistinctBy(p => p.Id, StringComparer.OrdinalIgnoreCase).ToList();
    // An engine install carries the editor plugin (menu, update badge) whenever the manifest has it; a project
    // install does too, unless the project has it already from its engine (0.5.0-beta.3).
    var elsewhere = ProvidedTo(target.target, manifest);
    order = manifest.WithHubPlugin(order.Select(p => p.Id), target.target.Kind, elsewhere is null ? null : new HashSet<string>(elsewhere.Keys, StringComparer.OrdinalIgnoreCase))
        .Select(id => manifest.Plugin(id)!).ToList();
    if (elsewhere is not null)
    {
        // Unreal refuses two plugins of one name: what the project has from its engine or a copy of its own stays there.
        foreach (var p in order.Where(p => elsewhere.ContainsKey(p.Id) && state.Find(target.target, p.Id) is null).ToList())
        {
            Console.WriteLine($"  here already: {p.Id} {elsewhere[p.Id]} (from the engine or the project's own copy; not installed into the project again)");
            order.Remove(p);
        }
        if (order.Count == 0) return 0;
    }

    if (!entitlements.IsSignedIn) { Console.Error.WriteLine($"  {AnonymousEntitlements.SignInMessage}  (forge login)"); return 4; }
    var owned = await OwnedOrEmptyAsync(ct);
    // Free tiers (0.5.0-beta.7): a paid plugin the account does not hold licensed installs as its free tier when its
    // offer has one and a build for this engine is at or above the floor. Without those fields, as before.
    var installed = state.On(target.target).GroupBy(i => i.Plugin, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Version, StringComparer.OrdinalIgnoreCase);
    // Without receipts: a paid plugin with no free tier installs only when the account holds it, as before.
    var access = await AccessInputsAsync(manifest, engineVersion, channel, target.target.Kind, owned, parse.GetValue(offlineOpt), ct);
    var unbought = order.Where(p => FreeTiers.AccessOf(p, access) == PaidAccess.NotOwned).Select(p => p.Id).ToList();
    var belowFloor = order.Where(p => FreeTiers.AccessOf(p, access) == PaidAccess.NeedsLicence && !installed.ContainsKey(p.Id)).ToList();
    foreach (var p in belowFloor)
        Console.Error.WriteLine($"  {FreeTiers.NeedsLicenceLine(p.Id, p.Latest(engineVersion, channel)?.Version, FreeTiers.TierOf(p.Id, access), FreeTiers.FloorOf(p.Id, access))} Buy it at {CloudConfig.PluginsUrl}");
    if (unbought.Count > 0)
        Console.Error.WriteLine($"  not on your account: {string.Join(", ", unbought)} — buy at {CloudConfig.PluginsUrl}");
    unbought.AddRange(belowFloor.Select(p => p.Id));
    if (unbought.Count > 0)
    {
        order.RemoveAll(p => unbought.Contains(p.Id, StringComparer.OrdinalIgnoreCase));
        if (order.Count == 0) return 5;
    }
    foreach (var p in order.Where(p => FreeTiers.AccessOf(p, access) == PaidAccess.FreeTier))
        Console.WriteLine($"  {p.Id}: not licensed, so it installs as {FreeTiers.Word(FreeTiers.TierOf(p.Id, access))}. Buy Full at {CloudConfig.PluginsUrl} and it becomes Full with no reinstall.");

    if (!Installer.IsWritable(target.target.Root) && !parse.GetValue(noElevate) && !Elevation.IsElevated())
    {
        Console.WriteLine($"{target.target.Root} needs administrator rights — asking for elevation.");
        var code = Elevation.RelaunchElevated(args);
        if (code is null) { Console.Error.WriteLine("Elevation was declined; nothing installed."); return 3; }
        return code.Value;
    }

    var failures = 0;
    foreach (var p in order)
    {
        // The build this account may have: the newest, or a free tier's newest free-tier build.
        var version = FreeTiers.BuildFor(p, access) ?? (installed.ContainsKey(p.Id) ? null : p.Latest(engineVersion, channel));
        if (version is null) { Console.WriteLine($"  skipped: {p.Id} has no {channel} release for UE {engineVersion}{(installed.ContainsKey(p.Id) ? " that this account may install" : "")}"); continue; }
        try
        {
            var lastPct = -1;
            var progress = new Progress<double>(f => { var pct = (int)(f * 100); if (pct / 10 != lastPct / 10) { lastPct = pct; Console.Write($"\r  {p.Id} {pct,3}%"); } });
            var result = await installer.InstallAsync(new InstallRequest(p, version, target.target), parse.GetValue(forceOpt), progress, ct);
            Console.Write("\r");
            if (result.Outcome == "already-current") Console.WriteLine($"  current: {p.Id} {version.Version}{(FreeTiers.AccessOf(p, access) == PaidAccess.FreeTier ? $" ({FreeTiers.Word(FreeTiers.TierOf(p.Id, access))})" : "")}");
        }
        catch (Exception ex) when (ex is EntitlementException or InvalidDataException or HttpRequestException or UnauthorizedAccessException)
        {
            Console.Write("\r"); Console.Error.WriteLine($"  failed: {p.Id} — {ex.Message}"); failures++;
        }
    }
    AfterProjectChange(target.target);
    return failures == 0 ? (unbought.Count == 0 ? 0 : 5) : 1;
});
root.Subcommands.Add(install);

// ── claim ─────────────────────────────────────────────────────────────────
var claimArg = new Argument<string[]>("plugins") { Description = "Plugin ids or set names to add to your account. Free ones are added; paid ones are named." };
var claim = new Command("claim", "Add free plugins (and the free plugins they depend on) to your account without installing them.");
claim.Arguments.Add(claimArg); claim.Options.Add(offlineOpt);
claim.SetAction(async (parse, ct) =>
{
    if (!entitlements.IsSignedIn) { Console.Error.WriteLine($"  {AnonymousEntitlements.SignInMessage}  (forge login)"); return 4; }
    var (manifest, _) = await manifestClient.GetAsync(parse.GetValue(offlineOpt), ct);
    var ids = new List<string>();
    foreach (var name in parse.GetValue(claimArg) ?? [])
    {
        if (manifest.Set(name) is { } set) ids.AddRange(set.Members);
        else if (manifest.Plugin(name) is { } p) ids.Add(p.Id);
        else { Console.Error.WriteLine($"Unknown plugin or set: {name}"); return 2; }
    }
    try
    {
        var r = await entitlements.ClaimAsync(ids, ct);
        if (r.Added.Count > 0) Console.WriteLine($"  added to your account: {string.Join(", ", r.Added)}");
        if (r.AlreadyOwned.Count > 0) Console.WriteLine($"  already yours: {string.Join(", ", r.AlreadyOwned)}");
        if (r.Paid.Count > 0) Console.WriteLine($"  paid, so not added: {string.Join(", ", r.Paid)} — buy at {CloudConfig.PluginsUrl}");
        return r.Paid.Count == 0 ? 0 : 5;
    }
    catch (EntitlementException ex)
    {
        Console.Error.WriteLine($"  {ex.Message}");
        return 1;
    }
});
root.Subcommands.Add(claim);

// ── update ────────────────────────────────────────────────────────────────
var includePinnedOpt = new Option<bool>("--include-pinned") { Description = "Update pinned components too (Kovati Hub's Pin this version)." };
var update = new Command("update", "Update every installed plugin on a target to its latest version. Pinned components are left as they are.");
update.Options.Add(includePinnedOpt);
update.Options.Add(engineOpt); update.Options.Add(projectOpt); update.Options.Add(channelOpt); update.Options.Add(offlineOpt); update.Options.Add(yesOpt);
update.SetAction(async (parse, ct) =>
{
    var (manifest, _) = await manifestClient.GetAsync(parse.GetValue(offlineOpt), ct);
    var target = ResolveTarget(parse.GetValue(engineOpt), parse.GetValue(projectOpt));
    var channel = parse.GetValue(channelOpt)!;
    if (!GuardProject(target.target, parse.GetValue(yesOpt))) return 6;
    var pending = new List<(PluginInfo p, VersionInfo v)>();
    var pins = HubState.Load().Pins;
    foreach (var i in state.On(target.target))
    {
        var p = manifest.Plugin(i.Plugin); if (p is null) continue;
        var latest = p.Latest(i.Engine, channel);
        if (latest is null || latest.Version == i.Version) continue;
        if (pins.TryGetValue(p.Id, out var at) && !parse.GetValue(includePinnedOpt))
        {
            Console.WriteLine($"  pinned: {p.Id} at {at}, {latest.Version} is out (forge update --include-pinned updates it)");
            continue;
        }
        pending.Add((p, latest));
    }
    if (pending.Count == 0) { Console.WriteLine("Everything is current."); return 0; }
    if (!entitlements.IsSignedIn) { Console.Error.WriteLine($"  {AnonymousEntitlements.SignInMessage}  (forge login)"); return 4; }
    if (!Installer.IsWritable(target.target.Root) && !parse.GetValue(noElevate) && !Elevation.IsElevated())
    {
        var code = Elevation.RelaunchElevated(args);
        if (code is null) { Console.Error.WriteLine("Elevation was declined; nothing updated."); return 3; }
        return code.Value;
    }
    var failures = 0;
    foreach (var (p, v) in pending)
    {
        try { await installer.InstallAsync(new InstallRequest(p, v, target.target), force: false, null, ct); }
        catch (Exception ex) when (ex is EntitlementException or InvalidDataException or HttpRequestException)
        {
            Console.Error.WriteLine($"  failed: {p.Id} — {ex.Message}"); failures++;
        }
    }
    return failures == 0 ? 0 : 1;
});
root.Subcommands.Add(update);

// ── uninstall ─────────────────────────────────────────────────────────────
var uninstallArg = new Argument<string[]>("plugins") { Description = "Plugin ids to remove." };
var uninstall = new Command("uninstall", "Remove installed plugins from a target.");
uninstall.Arguments.Add(uninstallArg); uninstall.Options.Add(engineOpt); uninstall.Options.Add(projectOpt); uninstall.Options.Add(yesOpt);
uninstall.SetAction(parse =>
{
    var target = ResolveTarget(parse.GetValue(engineOpt), parse.GetValue(projectOpt));
    if (!GuardProject(target.target, parse.GetValue(yesOpt))) return 6;
    if (!Installer.IsWritable(target.target.Root) && !parse.GetValue(noElevate) && !Elevation.IsElevated())
    {
        var code = Elevation.RelaunchElevated(args);
        if (code is null) { Console.Error.WriteLine("Elevation was declined; nothing removed."); return 3; }
        return code.Value;
    }
    foreach (var id in parse.GetValue(uninstallArg) ?? [])
        if (!installer.Uninstall(target.target, id)) Console.WriteLine($"  not installed: {id}");
    return 0;
});
root.Subcommands.Add(uninstall);

// ── migrate ───────────────────────────────────────────────────────────────
// Hubs before 0.4.3 installed into Engine/Plugins/AutomationForge, which stops C++ builds on that engine;
// this moves those installs to Engine/Plugins/Marketplace/AutomationForge. Install, update and uninstall
// do the same on the way, so this is for an engine where nothing else is about to change.
var migrate = new Command("migrate", "Move plugins an older hub installed into an engine to the engine's Marketplace folder.");
migrate.Options.Add(engineOpt);
migrate.SetAction(parse =>
{
    var target = ResolveTarget(parse.GetValue(engineOpt), null);
    if (target.target.LegacyRoot is null || !Directory.Exists(target.target.LegacyRoot))
    {
        Console.WriteLine($"Nothing to move: {target.target.Label} has no {Paths.PluginFolder} folder outside Marketplace.");
        return 0;
    }
    if (!Installer.IsWritable(target.target.Root) && !parse.GetValue(noElevate) && !Elevation.IsElevated())
    {
        var code = Elevation.RelaunchElevated(args);
        if (code is null) { Console.Error.WriteLine("Elevation was declined; nothing moved."); return 3; }
        return code.Value;
    }
    var left = installer.MoveLegacyEngineInstalls(target.target);
    return left == 0 ? 0 : 1;
});
root.Subcommands.Add(migrate);

// ── enable / disable ──────────────────────────────────────────────────────
foreach (var (verb, enabled) in new[] { ("enable", true), ("disable", false) })
{
    var arg = new Argument<string[]>("plugins") { Description = "Plugin ids." };
    var projReq = new Option<string>("--project", "-p") { Description = "The project (.uproject or its folder).", Required = true };
    var cmd = new Command(verb, $"{(enabled ? "Enable" : "Disable")} plugins in a project — the same edit the Plugins window makes.");
    cmd.Arguments.Add(arg); cmd.Options.Add(projReq); cmd.Options.Add(yesOpt);
    cmd.SetAction(parse =>
    {
        var proj = new ProjectDescriptor(parse.GetValue(projReq)!);
        // The editor keeps its own copy of the descriptor and may write it back over this edit.
        if (!GuardUProject(proj.Path, parse.GetValue(yesOpt))) return 6;
        if (proj.IsReadOnly)
        {
            Console.Error.WriteLine($"  {Path.GetFileName(proj.Path)} is read-only. Check it out in your source control (or make it writable), then try again. Nothing changed.");
            return 1;
        }
        var changed = 0;
        foreach (var id in parse.GetValue(arg) ?? [])
        {
            var did = proj.SetEnabled(id, enabled);
            if (did) changed++;
            Console.WriteLine(did ? $"  {verb}d: {id} in {proj.Name}" : $"  unchanged: {id} already {verb}d in {proj.Name}");
        }
        if (changed > 0 && proj.IsCodeProject) Console.WriteLine($"  {proj.Name} is a C++ project: rebuild it in your IDE before opening it.");
        if (changed > 0 && ProjectInspector.SourceControlOf(proj.Directory) is { } vcs) Console.WriteLine($"  {proj.Name} is in {vcs}: {Path.GetFileName(proj.Path)} now shows as changed.");
        Console.WriteLine("  Restart the editor for the change to take effect.");
        return 0;
    });
    root.Subcommands.Add(cmd);
}

// ── channel ───────────────────────────────────────────────────────────────
var channelArg = new Argument<string?>("channel") { Description = "stable or nightly. Omit to show the current choice.", Arity = ArgumentArity.ZeroOrOne };
var channelCmd = new Command("channel", "Show or set the release channel Kovati Hub and the CLI follow by default.");
channelCmd.Arguments.Add(channelArg);
channelCmd.SetAction(parse =>
{
    var wanted = parse.GetValue(channelArg);
    if (wanted is null) { Console.WriteLine(settings.Channel); return 0; }
    if (wanted is not (Settings.Stable or Settings.Nightly)) { Console.Error.WriteLine("The channel is stable or nightly."); return 2; }
    settings.Channel = wanted;
    settings.Save();
    Console.WriteLine($"  channel: {wanted}  ({Settings.FilePath})");
    return 0;
});
root.Subcommands.Add(channelCmd);

// ── login / logout / whoami ───────────────────────────────────────────────
var noBrowser = new Option<bool>("--no-browser") { Description = "Print the sign-in link instead of opening the browser (remote sessions)." };
var login = new Command("login", "Sign in to your Kovati account in the browser; the session is kept on this machine.");
login.Options.Add(noBrowser);
login.SetAction(async (parse, ct) =>
{
    if (!CloudConfig.Configured) { Console.Error.WriteLine("Accounts are not configured in this build."); return 2; }
    if (entitlements.IsSignedIn) { Console.WriteLine($"  already signed in as {entitlements.AccountLabel}. Run `forge logout` first to switch."); return 0; }
    var openBrowser = !parse.GetValue(noBrowser);
    Console.WriteLine(openBrowser ? "  Opening your browser. Say yes there, and this window finishes by itself." : "  Open this link, say yes there, and this window finishes by itself:");
    var account = await Handshake.SignInAsync(
        url => { Console.WriteLine($"  {url}"); if (openBrowser) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); },
        TimeSpan.FromMinutes(5), ct);
    if (account is null) { Console.Error.WriteLine("  Nothing arrived in five minutes; nothing changed."); return 3; }
    entitlements.SignIn(account);
    Console.WriteLine($"  signed in as {account.Email}");
    // The licence comes with the sign-in, as in the Hub. Whatever the server says, the sign-in stands.
    var fetched = await LicenceFor().RefreshAsync(force: true, ct);
    if (fetched is LicenceFetchOutcome.Updated or LicenceFetchOutcome.Unchanged) Console.WriteLine($"  licence: {Paths.LicenceFile}");
    return 0;
});
root.Subcommands.Add(login);

var logout = new Command("logout", "Forget the sign-in kept on this machine.");
logout.SetAction(_ =>
{
    if (!entitlements.IsSignedIn) { Console.WriteLine("  not signed in"); return 0; }
    var who = entitlements.AccountLabel;
    entitlements.SignOut(); // the licence goes with it
    Console.WriteLine($"  signed out {who}");
    return 0;
});
root.Subcommands.Add(logout);

// ── licence ───────────────────────────────────────────────────────────────
// The signed licence paid plugins read (HUB_V2_NOTES.md, "The licence file"). A plugin calls `forge licence refresh`
// when the Hub is not running and the file is more than a day old, then reads the file again.
var licenceJsonOpt = new Option<bool>("--json") { Description = "Print it as JSON." };
var licenceCmd = new Command("licence", "Show the licence on this computer: which paid tools it covers, until when, and for which account.");
licenceCmd.Options.Add(licenceJsonOpt);
licenceCmd.SetAction(async (parse, ct) =>
{
    var snap = LicenceFor().Snapshot();
    // The paid tools in the cached list of tools (never the network), and whatever the licence lists.
    Manifest? cached = null;
    try { (cached, _) = await manifestClient.GetAsync(offline: true, ct); }
    catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or InvalidDataException or IOException or System.Text.Json.JsonException) { }
    var products = LicenceProducts(snap, cached);
    if (parse.GetValue(licenceJsonOpt))
    {
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(LicenceJson(snap, products), new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
    Console.WriteLine($"  file: {Paths.LicenceFile}");
    Console.WriteLine($"  account: {(entitlements.IsSignedIn ? $"{entitlements.AccountLabel} ({snap.AccountUid})" : "not signed in (forge login)")}");
    if (snap.Payload is not { } p)
    {
        Console.WriteLine(snap.ServerOff ? "  no licence: licences are not switched on at Kovati yet." : "  no licence on this computer.");
        return 0;
    }
    var inv = System.Globalization.CultureInfo.InvariantCulture;
    Console.WriteLine($"  issued {p.IssuedAt.ToLocalTime().ToString("d MMM yyyy HH:mm", inv)} · valid offline until {p.ValidUntil.ToLocalTime().ToString("d MMM yyyy HH:mm", inv)} · key {snap.Licence!.Envelope.Kid}{(snap.Licence.SignedWithDevKey ? " (development key)" : "")}{(p.Test ? " · test licence" : "")}");
    if (snap.Check.LastSuccessAt is { } last) Console.WriteLine($"  last refreshed {last.ToLocalTime().ToString("d MMM yyyy HH:mm", inv)}");
    if (products.Count == 0) Console.WriteLine("  covers no paid tools.");
    foreach (var (id, l) in products)
    {
        var detail = l.Standing switch
        {
            LicenceStanding.OfflineGrace => $"offline, {l.DaysLeft} days left",
            LicenceStanding.Expired when l.ClockBehind => "the clock is behind the licence: set the date, then `forge licence refresh`",
            LicenceStanding.Expired => "past its 30 days offline: `forge licence refresh`",
            _ => "",
        };
        var how = l.How is { Length: > 0 } h ? $"{h}{(l.Since is { } s ? " " + s.ToString("yyyy-MM-dd", inv) : "")}" : "";
        Console.WriteLine($"  {id,-24} {l.Standing,-13} {how,-18} {(l.MaxMajor is { } m ? $"up to {m}.x" : ""),-9} {detail}");
    }
    return 0;
});

var licenceRefresh = new Command("refresh",
    "Fetch the licence for the signed-in account now and store it if it is newer. Exit codes: 0 a current licence is on disk "
    + "(fetched now, or the same one again); 4 not signed in, or the account was refused (the licence was removed); "
    + "5 not refreshed now (offline, rate limited, server trouble, the file was held open): the file is as it was; "
    + "7 the answer did not verify or was for another account: nothing stored; 8 licences are not switched on at Kovati yet.");
licenceRefresh.Options.Add(licenceJsonOpt);
licenceRefresh.SetAction(async (parse, ct) =>
{
    var outcome = await LicenceFor().RefreshAsync(force: true, ct);
    var code = outcome switch
    {
        LicenceFetchOutcome.Updated or LicenceFetchOutcome.Unchanged => 0,
        LicenceFetchOutcome.NotSignedIn or LicenceFetchOutcome.Refused => 4,
        LicenceFetchOutcome.Invalid => 7,
        LicenceFetchOutcome.Off => 8,
        _ => 5,
    };
    var line = outcome switch
    {
        LicenceFetchOutcome.Updated => $"licence refreshed: {Paths.LicenceFile}",
        LicenceFetchOutcome.Unchanged => $"licence is current: {Paths.LicenceFile}",
        LicenceFetchOutcome.NotSignedIn => "not signed in (forge login); no licence",
        LicenceFetchOutcome.Refused => "the account was refused; the licence was removed. Sign in again (forge login)",
        LicenceFetchOutcome.Invalid => "the answer did not verify, or was for another account; nothing changed",
        LicenceFetchOutcome.Off => "licences are not switched on at Kovati yet; nothing changed",
        LicenceFetchOutcome.RateLimited => "asked too often; try again later. Nothing changed",
        LicenceFetchOutcome.WriteFailed => "the licence file is held open by another program; try again. Nothing changed",
        LicenceFetchOutcome.Offline => "could not reach Kovati; the licence on disk is kept",
        _ => "Kovati could not answer just now; the licence on disk is kept",
    };
    if (parse.GetValue(licenceJsonOpt))
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { outcome = outcome.ToString(), exitCode = code, path = Paths.LicenceFile, message = line }));
    else if (code == 0) Console.WriteLine($"  {line}");
    else Console.Error.WriteLine($"  {line}");
    return code;
});
licenceCmd.Subcommands.Add(licenceRefresh);

var licencePath = new Command("path", "Print where the licence file is (the data folder, or FORGE_DATA_DIR).");
licencePath.SetAction(_ => { Console.WriteLine(Paths.LicenceFile); return 0; });
licenceCmd.Subcommands.Add(licencePath);
root.Subcommands.Add(licenceCmd);

var whoami = new Command("whoami", "Who this machine is signed in as, and what the account holds.");
whoami.SetAction(async (parse, ct) =>
{
    if (!entitlements.IsSignedIn) { Console.WriteLine("  not signed in — `forge login`"); return 0; }
    Console.WriteLine($"  {entitlements.AccountLabel}  ({entitlements.Account!.Uid})");
    try
    {
        var owned = await entitlements.OwnedAsync(ct);
        Console.WriteLine(owned.Count == 0 ? $"  on the account: nothing yet — `forge claim <set>` or {CloudConfig.PluginsUrl}" : $"  on the account: {string.Join(", ", owned.OrderBy(x => x))}");
    }
    catch (Exception ex) when (ex is EntitlementException or HttpRequestException)
    {
        Console.Error.WriteLine($"  {ex.Message}");
        return 1;
    }
    return 0;
});
root.Subcommands.Add(whoami);

// A mistyped --project or --engine is the ordinary way to get this wrong, and
// it used to arrive as an unhandled stack trace that read like a broken hub.
// These are the exceptions that mean "what you asked for is not there"; a
// genuine defect still escapes, because a stack trace is the right answer to one.
try
{
    // Off, so the exceptions below reach the catch instead of being printed as a
    // stack trace with "Unhandled exception:" in front of them.
    var invocation = new InvocationConfiguration { EnableDefaultExceptionHandler = false };
    return await root.Parse(args).InvokeAsync(invocation);
}
catch (EntitlementException ex)
{
    // Anything about the account keeps the account's code, so a script branching
    // on 4 does not have to learn a second one for the same class of problem.
    Console.Error.WriteLine($"  {ex.Message}");
    return 4;
}
catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or InvalidOperationException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"  {ex.Message}");
    return 2;
}

// ── helpers ───────────────────────────────────────────────────────────────

// `forge install --track animation [--only motion,gameplay] [--engines motion=motionforge-uthana]
// [--agents | --no-agents] [--dry-run]`: the planner Kovati Hub's track page uses (spec 6.5), so the dry run
// is the window's install panel in text, and the acceptance checks run against it.
async Task<int> InstallTrackAsync(ParseResult parse, string trackName, CancellationToken ct)
{
    var catalogue = CatalogueLoader.Load();
    if (catalogue is null) { Console.Error.WriteLine($"No catalogue: {CatalogueLoader.BundledPath} is missing or unreadable. {CatalogueLoader.LastError} Set FORGE_CATALOGUE to a catalogue.json."); return 2; }
    var track = catalogue.Track(trackName);
    if (track is null) { Console.Error.WriteLine($"Unknown track: {trackName}. Tracks: {string.Join(", ", catalogue.TracksInOrder.Select(t => t.Slug))}."); return 2; }

    var offline = parse.GetValue(offlineOpt);
    var dryRun = parse.GetValue(dryRunOpt);
    var (manifest, fromCache) = await manifestClient.GetAsync(offline, ct);
    await new Forge.Core.Releases.ReleaseDiscovery(http).MergeIntoAsync(manifest, offline, ct);
    var channel = parse.GetValue(channelOpt)!;

    InstallTarget? target = null;
    string engineVersion = "5.8";
    try
    {
        var resolved = ResolveTarget(parse.GetValue(engineOpt), parse.GetValue(projectOpt));
        target = resolved.target;
        engineVersion = resolved.version;
    }
    catch (InvalidOperationException) when (dryRun)
    {
        // A dry run plans without an engine: what a machine with nothing installed would get.
    }

    var hubState = HubState.Load();
    var agents = parse.GetValue(agentsOpt) ? true : parse.GetValue(noAgentsOpt) ? false : hubState.AgentsOn;
    var installed = target is null
        ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        : state.On(target).GroupBy(i => i.Plugin, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Version, StringComparer.OrdinalIgnoreCase);
    var owned = await OwnedOrEmptyAsync(ct);
    var inputs = new PlanInputs
    {
        Catalogue = catalogue, Manifest = manifest, Engine = engineVersion, Channel = channel,
        TargetKind = target?.Kind ?? "engine", Owned = owned, SignedIn = entitlements.IsSignedIn,
        // Free tiers (0.5.0-beta.7): what the account holds licensed, and each paid plugin's tier and builds.
        Licensed = entitlements.IsSignedIn ? await entitlements.LicensedAsync(ct) : null,
        Offers = await AccountCatalogue.GetAsync(http, offline, ct),
        AgentAccess = agents, ShowLabs = hubState.ShowLabs, Installed = installed, Pins = hubState.Pins,
        // A project: what its engine (or a copy of its own) has stays there, and the foundation its engine lacks goes in.
        ProvidedElsewhere = (target is null ? null : ProvidedTo(target, manifest)) ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        FoundationInProject = true,
    };

    var selection = TrackPlanner.DefaultSelection(track, inputs);
    if (parse.GetValue(onlyOpt) is { Length: > 0 } only)
    {
        var ids = only.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var id in ids)
            if (track.Capability(id) is null) { Console.Error.WriteLine($"{track.Name} has no capability '{id}'. It has: {string.Join(", ", track.Capabilities.Select(c => c.Id))}."); return 2; }
        selection = selection with { Capabilities = ids };
    }
    foreach (var spec in parse.GetValue(enginesOpt) ?? [])
    {
        var eq = spec.IndexOf('=');
        var capId = eq > 0 ? spec[..eq] : "";
        if (track.Capability(capId) is not { } cap) { Console.Error.WriteLine($"--engines {spec}: name a capability of {track.Name}, as capability=engine,engine."); return 2; }
        var engines = spec[(eq + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var e in engines)
            if (!cap.Engines.Any(x => string.Equals(x.Product, e, StringComparison.OrdinalIgnoreCase))) { Console.Error.WriteLine($"{cap.Name} has no engine '{e}'. It has: {string.Join(", ", cap.Engines.Select(x => x.Product))}."); return 2; }
        var map = selection.Engines.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        map[cap.Id] = engines;
        selection = selection with { Engines = map };
    }

    var plan = TrackPlanner.Plan(track, selection, inputs);

    Console.WriteLine($"{track.Name} · {(target is null ? $"Unreal Engine {engineVersion} (no engine found)" : target.Label)} · {channel}"
                      + $" · agent access {(agents ? "on" : "off")}"
                      + (entitlements.IsSignedIn ? $" · {entitlements.AccountLabel}" : " · signed out")
                      + (fromCache ? " · cached list" : ""));
    foreach (var item in plan.Items)
    {
        var what = item.Role switch
        {
            PlanRole.Always => catalogue.FoundationName(item.Id) ?? (item.NeededBy is { } by ? $"needed by {by}" : "Kovati foundation"),
            PlanRole.Agent => $"agent access for {catalogue.Product(item.Product)?.Name}",
            PlanRole.Engine => $"engine for {track.Capability(item.Capability)?.Name}",
            PlanRole.Dependency => $"needed by {item.NeededBy}",
            _ => track.Capability(item.Capability)?.Name ?? "",
        };
        var action = item.Action switch { PlanAction.Install => "install", PlanAction.Update => "update ", _ => "here   " };
        if (item.IsFreeTier) what = $"{what} · {FreeTiers.Word(item.FreeTier)} (not licensed; Full once bought, no reinstall)";
        Console.WriteLine($"  {action}  {item.Id,-26} {item.Version,-8} {Words.Size(item.Size),8}   {what}");
    }
    foreach (var drop in plan.Drops) Console.WriteLine($"  left out  {drop.Id,-24} {drop.Message}");
    Console.WriteLine($"{plan.Summary} · {Words.Size(plan.Size)} · Free" + (plan.AlreadyHere > 0 ? $" · {plan.AlreadyHere} already here" : ""));

    if (dryRun) return 0;
    if (target is null) { Console.Error.WriteLine("No Unreal Engine installation found."); return 2; }
    if (plan.ComponentCount == 0) { Console.WriteLine("Nothing to install."); return 0; }
    if (!GuardProject(target, parse.GetValue(yesOpt))) return 6;
    if (!entitlements.IsSignedIn) { Console.Error.WriteLine($"  {AnonymousEntitlements.SignInMessage}  (forge login)"); return 4; }
    if (!Installer.IsWritable(target.Root) && !parse.GetValue(noElevate) && !Elevation.IsElevated())
    {
        Console.WriteLine($"Windows will ask for permission once, to write to {target.Root}.");
        var code = Elevation.RelaunchElevated(args);
        if (code is null) { Console.Error.WriteLine("Windows permission was declined. Nothing changed."); return 3; }
        return code.Value;
    }

    var failures = 0;
    foreach (var item in plan.ToChange)
    {
        var p = manifest.Plugin(item.Id)!;
        // The build the plan chose: for a free tier, its newest free-tier build.
        var version = p.Versions.FirstOrDefault(v => v.Version == item.Version && v.Engine == engineVersion) ?? p.Latest(engineVersion, channel)!;
        try { await installer.InstallAsync(new InstallRequest(p, version, target), parse.GetValue(forceOpt), null, ct); }
        catch (Exception ex) when (ex is EntitlementException or InvalidDataException or HttpRequestException or UnauthorizedAccessException or IOException)
        {
            Console.Error.WriteLine($"  failed: {p.Id} — {ex.Message}"); failures++;
        }
    }
    hubState = HubState.Load();
    hubState.RecordTrack(track.Slug, target.Root, plan.Capabilities, selection.Engines);
    hubState.LastTarget = target.Root;
    hubState.Save();
    AfterProjectChange(target);
    return failures == 0 ? 0 : 1;
}

// What a project already has from somewhere other than Kovati Hub's folder in it (its engine, a copy of its
// own): id to version. Null for an engine target.
Dictionary<string, string>? ProvidedTo(InstallTarget target, Manifest manifest)
{
    if (target.Kind != "project") return null;
    var uproject = ProjectLocator.UProjectOfReceiptRoot(target.Root);
    if (uproject is null) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var ids = new HashSet<string>(manifest.Plugins.Select(p => p.Id), StringComparer.OrdinalIgnoreCase);
    var deps = manifest.Plugins.ToDictionary(p => p.Id, p => (IReadOnlyList<string>)p.Dependencies, StringComparer.OrdinalIgnoreCase);
    var report = ProjectInspector.Inspect(new FoundProject(uproject, ProjectSource.None, null), EngineLocator.Find(), ids, deps, state.Items, EngineLocator.RegisteredBuilds());
    return ProjectService.ProvidedElsewhere(report);
}

// Never write into a project whose editor is open: Unreal holds its plugins and keeps its own copy of the .uproject.
bool GuardProject(InstallTarget target, bool yes) =>
    target.Kind != "project" || ProjectLocator.UProjectOfReceiptRoot(target.Root) is not { } u || GuardUProject(u, yes);

bool GuardUProject(string uproject, bool yes)
{
    switch (EditorProcesses.Check(uproject))
    {
        case EditorOnProject.Yes:
            Console.Error.WriteLine($"  Unreal Editor has {Path.GetFileNameWithoutExtension(uproject)} open. Close it, then try again. Nothing changed.");
            return false;
        case EditorOnProject.Unknown when !yes:
            Console.Error.WriteLine($"  Unreal Editor is running, and it cannot be told whether it has {Path.GetFileNameWithoutExtension(uproject)} open. Close it, or run again with --yes. Nothing changed.");
            return false;
        default:
            return true;
    }
}

// After an install into a project: what its owner should know. Kovati Hub never runs a source control command
// and never deletes Binaries or Intermediate.
void AfterProjectChange(InstallTarget target)
{
    if (target.Kind != "project" || ProjectLocator.UProjectOfReceiptRoot(target.Root) is not { } u) return;
    try
    {
        var d = new ProjectDescriptor(u);
        if (d.IsCodeProject) Console.WriteLine($"  {d.Name} is a C++ project: rebuild it in your IDE before opening it.");
        if (ProjectInspector.SourceControlOf(d.Directory) is { } vcs)
            Console.WriteLine($"  {d.Name} is in {vcs}: add Plugins/{Paths.PluginFolder} to its ignore file, or commit it for the team.");
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException) { }
}

// The licence service over this process's sign-in.
LicenceService LicenceFor() => new(http, () => entitlements.Account, (_, ct) => entitlements.IdTokenAsync(ct));

// Every paid tool the cached manifest knows, and every id the licence lists, each with its standing.
static List<(string Id, ProductLicence Licence)> LicenceProducts(LicenceSnapshot snap, Manifest? manifest)
{
    var ids = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var p in manifest?.Plugins ?? []) if (p.IsPaid) ids.Add(p.Id);
    foreach (var id in snap.Payload?.Products.Keys ?? Enumerable.Empty<string>()) ids.Add(id);
    return ids.Select(id => (id, snap.For(id))).ToList();
}

static object LicenceJson(LicenceSnapshot snap, List<(string Id, ProductLicence Licence)> products)
{
    var p = snap.Payload;
    return new
    {
        path = Paths.LicenceFile,
        exists = File.Exists(Paths.LicenceFile),
        account = snap.AccountUid,
        valid = p is not null,
        serverOff = snap.ServerOff,
        kid = p is null ? null : snap.Licence!.Envelope.Kid,
        devKey = p is null ? (bool?)null : snap.Licence!.SignedWithDevKey,
        test = p?.Test,
        issuedAt = p?.IssuedAt,
        offlineUntil = p?.OfflineUntil,
        validUntil = p?.ValidUntil,
        lastRefreshedAt = snap.Check.Sub == snap.AccountUid ? snap.Check.LastSuccessAt : null,
        lastOutcome = snap.Check.Sub == snap.AccountUid ? snap.Check.LastOutcome?.ToString() : null,
        products = products.Select(x => new
        {
            id = x.Id,
            standing = x.Licence.Standing.ToString(),
            how = x.Licence.How,
            since = x.Licence.Since?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            maxMajor = x.Licence.MaxMajor,
            test = x.Licence.Test,
            daysLeft = x.Licence.DaysLeft,
            clockBehind = x.Licence.ClockBehind,
        }),
    };
}

// What the free-tier rules read for a plain `forge install`: the account's licensed plugins and the account service's
// offers, with the catalogue only because the planner's inputs need one.
async Task<PlanInputs> AccessInputsAsync(Manifest manifest, string engineVersion, string channel, string kind, IReadOnlySet<string> owned, bool offline, CancellationToken ct) =>
    new()
    {
        Catalogue = CatalogueLoader.Load() ?? new Forge.Core.Catalogue.Catalogue(),
        Manifest = manifest, Engine = engineVersion, Channel = channel, TargetKind = kind,
        Owned = owned, SignedIn = entitlements.IsSignedIn,
        Licensed = entitlements.IsSignedIn ? await entitlements.LicensedAsync(ct) : null,
        Offers = await AccountCatalogue.GetAsync(http, offline, ct),
    };

async Task<IReadOnlySet<string>> OwnedOrEmptyAsync(CancellationToken ct)
{
    if (!entitlements.IsSignedIn) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    try { return await entitlements.OwnedAsync(ct); }
    catch (Exception ex) when (ex is EntitlementException or HttpRequestException)
    {
        Console.Error.WriteLine($"  (could not read your account: {ex.Message})");
        return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }
}

// The target, its engine when it is on this machine, and the engine version builds are chosen for. A project's
// version comes from its EngineAssociation: "5.8" as it is, a source build's GUID through the registry (never as
// a version), or --engine when that is given.
static (InstallTarget target, EngineInstall? engine, string version) ResolveTarget(string? engineSpec, string? project)
{
    if (!string.IsNullOrEmpty(project))
    {
        var t = InstallTarget.Project(project);
        var uproject = ProjectLocator.UProjectOfReceiptRoot(t.Root)!;
        var all = EngineLocator.Find();
        if (!string.IsNullOrEmpty(engineSpec))
        {
            var chosen = EngineLocator.Resolve(engineSpec, all) ?? throw new InvalidOperationException($"No engine matches '{engineSpec}'. Run `forge engines`.");
            return (t, chosen, chosen.Version);
        }
        var (version, problem) = ProjectService.EngineVersionOf(uproject, all);
        if (version is null) throw new InvalidOperationException(problem);
        return (t, all.FirstOrDefault(e => e.Version == version), version);
    }
    var engine = EngineLocator.Resolve(engineSpec)
                 ?? throw new InvalidOperationException(engineSpec is null ? "No Unreal Engine installation found." : $"No engine matches '{engineSpec}'. Run `forge engines`.");
    return (InstallTarget.Engine(engine), engine, engine.Version);
}
