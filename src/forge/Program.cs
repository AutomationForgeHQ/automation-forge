using System.CommandLine;
using Forge.Core;
using Forge.Core.Catalogue;
using Forge.Core.Cloud;
using Forge.Core.Engines;
using Forge.Core.Entitlements;
using Forge.Core.Installs;
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

var root = new RootCommand("Kovati: install, update and manage Kovati's tools for Unreal Engine 5.8.");
root.Options.Add(noElevate);

// ── engines ───────────────────────────────────────────────────────────────
var engines = new Command("engines", "List the Unreal Engine installations found on this machine.");
engines.SetAction(_ =>
{
    var found = EngineLocator.Find();
    if (found.Count == 0) { Console.WriteLine("No Unreal Engine installations found."); return 1; }
    foreach (var e in found)
        Console.WriteLine($"  UE {e.FullVersion,-8} {e.Path}{(e.IsLauncherBuild ? "" : "  (source build)")}  plugins → {e.PluginRoot}");
    return 0;
});
root.Subcommands.Add(engines);

// ── list ──────────────────────────────────────────────────────────────────
var list = new Command("list", "Show the sets and plugins in the manifest, with what is installed.");
list.Options.Add(engineOpt); list.Options.Add(projectOpt); list.Options.Add(channelOpt); list.Options.Add(offlineOpt);
list.SetAction(async (parse, ct) =>
{
    var (manifest, fromCache) = await manifestClient.GetAsync(parse.GetValue(offlineOpt), ct);
    var target = ResolveTarget(parse.GetValue(engineOpt), parse.GetValue(projectOpt));
    var engineVersion = target.engine?.Version ?? "5.8";
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
install.SetAction(async (parse, ct) =>
{
    if (parse.GetValue(trackOpt) is { Length: > 0 } trackName)
        return await InstallTrackAsync(parse, trackName, ct);
    if ((parse.GetValue(whatArg) ?? []).Length == 0) { Console.Error.WriteLine("Name plugins or sets to install, or a track with --track."); return 2; }

    var (manifest, _) = await manifestClient.GetAsync(parse.GetValue(offlineOpt), ct);
    var target = ResolveTarget(parse.GetValue(engineOpt), parse.GetValue(projectOpt));
    var engineVersion = target.engine?.Version ?? new ProjectDescriptor(parse.GetValue(projectOpt)!).EngineAssociation ?? "5.8";
    var channel = parse.GetValue(channelOpt)!;

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
    // An engine install carries the editor plugin (menu, update badge) whenever the manifest has it.
    order = manifest.WithHubPlugin(order.Select(p => p.Id), target.target.Kind).Select(id => manifest.Plugin(id)!).ToList();

    if (!entitlements.IsSignedIn) { Console.Error.WriteLine($"  {AnonymousEntitlements.SignInMessage}  (forge login)"); return 4; }
    var owned = await OwnedOrEmptyAsync(ct);
    var unbought = order.Where(p => p.IsPaid && !owned.Contains(p.Id)).Select(p => p.Id).ToList();
    if (unbought.Count > 0)
    {
        Console.Error.WriteLine($"  not on your account: {string.Join(", ", unbought)} — buy at {CloudConfig.PluginsUrl}");
        order.RemoveAll(p => unbought.Contains(p.Id, StringComparer.OrdinalIgnoreCase));
        if (order.Count == 0) return 5;
    }

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
        var version = p.Latest(engineVersion, channel);
        if (version is null) { Console.WriteLine($"  skipped: {p.Id} has no {channel} release for UE {engineVersion}"); continue; }
        try
        {
            var lastPct = -1;
            var progress = new Progress<double>(f => { var pct = (int)(f * 100); if (pct / 10 != lastPct / 10) { lastPct = pct; Console.Write($"\r  {p.Id} {pct,3}%"); } });
            var result = await installer.InstallAsync(new InstallRequest(p, version, target.target), parse.GetValue(forceOpt), progress, ct);
            Console.Write("\r");
            if (result.Outcome == "already-current") Console.WriteLine($"  current: {p.Id} {version.Version}");
        }
        catch (Exception ex) when (ex is EntitlementException or InvalidDataException or HttpRequestException or UnauthorizedAccessException)
        {
            Console.Write("\r"); Console.Error.WriteLine($"  failed: {p.Id} — {ex.Message}"); failures++;
        }
    }
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
update.Options.Add(engineOpt); update.Options.Add(projectOpt); update.Options.Add(channelOpt); update.Options.Add(offlineOpt);
update.SetAction(async (parse, ct) =>
{
    var (manifest, _) = await manifestClient.GetAsync(parse.GetValue(offlineOpt), ct);
    var target = ResolveTarget(parse.GetValue(engineOpt), parse.GetValue(projectOpt));
    var channel = parse.GetValue(channelOpt)!;
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
uninstall.Arguments.Add(uninstallArg); uninstall.Options.Add(engineOpt); uninstall.Options.Add(projectOpt);
uninstall.SetAction(parse =>
{
    var target = ResolveTarget(parse.GetValue(engineOpt), parse.GetValue(projectOpt));
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
    cmd.Arguments.Add(arg); cmd.Options.Add(projReq);
    cmd.SetAction(parse =>
    {
        var proj = new ProjectDescriptor(parse.GetValue(projReq)!);
        foreach (var id in parse.GetValue(arg) ?? [])
            Console.WriteLine(proj.SetEnabled(id, enabled) ? $"  {verb}d: {id} in {proj.Name}" : $"  unchanged: {id} already {verb}d in {proj.Name}");
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
    return 0;
});
root.Subcommands.Add(login);

var logout = new Command("logout", "Forget the sign-in kept on this machine.");
logout.SetAction(_ =>
{
    if (!entitlements.IsSignedIn) { Console.WriteLine("  not signed in"); return 0; }
    var who = entitlements.AccountLabel;
    entitlements.SignOut();
    Console.WriteLine($"  signed out {who}");
    return 0;
});
root.Subcommands.Add(logout);

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
        engineVersion = resolved.engine?.Version ?? new ProjectDescriptor(parse.GetValue(projectOpt)!).EngineAssociation ?? "5.8";
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
        AgentAccess = agents, ShowLabs = hubState.ShowLabs, Installed = installed, Pins = hubState.Pins,
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
        Console.WriteLine($"  {action}  {item.Id,-26} {item.Version,-8} {Words.Size(item.Size),8}   {what}");
    }
    foreach (var drop in plan.Drops) Console.WriteLine($"  left out  {drop.Id,-24} {drop.Message}");
    Console.WriteLine($"{plan.Summary} · {Words.Size(plan.Size)} · Free" + (plan.AlreadyHere > 0 ? $" · {plan.AlreadyHere} already here" : ""));

    if (dryRun) return 0;
    if (target is null) { Console.Error.WriteLine("No Unreal Engine installation found."); return 2; }
    if (plan.ComponentCount == 0) { Console.WriteLine("Nothing to install."); return 0; }
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
        var version = p.Latest(engineVersion, channel)!;
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
    return failures == 0 ? 0 : 1;
}

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

static (InstallTarget target, EngineInstall? engine) ResolveTarget(string? engineSpec, string? project)
{
    if (!string.IsNullOrEmpty(project))
        return (InstallTarget.Project(project), null);
    var engine = EngineLocator.Resolve(engineSpec)
                 ?? throw new InvalidOperationException(engineSpec is null ? "No Unreal Engine installation found." : $"No engine matches '{engineSpec}'. Run `forge engines`.");
    return (InstallTarget.Engine(engine), engine);
}
