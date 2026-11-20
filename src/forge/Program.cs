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

// forge — the Automation Forge installer, as a command line.
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

var root = new RootCommand("Automation Forge — install, update and manage the plugin sets for Unreal Engine 5.8.");
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
var whatArg = new Argument<string[]>("plugins") { Description = "Plugin ids or set names (a set installs every member)." };
var install = new Command("install", "Download, verify and install plugins or whole sets, dependencies first.");
install.Arguments.Add(whatArg);
install.Options.Add(engineOpt); install.Options.Add(projectOpt); install.Options.Add(channelOpt); install.Options.Add(offlineOpt); install.Options.Add(forceOpt);
install.SetAction(async (parse, ct) =>
{
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
    // Free tiers (as in Kovati Hub 0.5.0-beta.7): a paid plugin the account does not hold licensed installs as its free
    // tier when its offer has one and a build for this engine is at or above the floor. Without those fields, as before.
    var access = await AccessInputsAsync(engineVersion, channel, owned, parse.GetValue(offlineOpt), ct);
    var unbought = order.Where(p => FreeTiers.AccessOf(p, access) == PaidAccess.NotOwned).Select(p => p.Id).ToList();
    var belowFloor = order.Where(p => FreeTiers.AccessOf(p, access) == PaidAccess.NeedsLicence).ToList();
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
        var version = FreeTiers.BuildFor(p, access);
        if (version is null) { Console.WriteLine($"  skipped: {p.Id} has no {channel} release for UE {engineVersion}"); continue; }
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
var update = new Command("update", "Update every installed plugin on a target to its latest version.");
update.Options.Add(engineOpt); update.Options.Add(projectOpt); update.Options.Add(channelOpt); update.Options.Add(offlineOpt);
update.SetAction(async (parse, ct) =>
{
    var (manifest, _) = await manifestClient.GetAsync(parse.GetValue(offlineOpt), ct);
    var target = ResolveTarget(parse.GetValue(engineOpt), parse.GetValue(projectOpt));
    var channel = parse.GetValue(channelOpt)!;
    var pending = new List<(PluginInfo p, VersionInfo v)>();
    foreach (var i in state.On(target.target))
    {
        var p = manifest.Plugin(i.Plugin); if (p is null) continue;
        var latest = p.Latest(i.Engine, channel);
        if (latest is not null && latest.Version != i.Version) pending.Add((p, latest));
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
var channelCmd = new Command("channel", "Show or set the release channel the hub and the CLI follow by default.");
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
var login = new Command("login", "Sign in to your Automation Forge account in the browser; the session is kept on this machine.");
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
    // The licence comes with the sign-in, as in the hub. Whatever the server says, the sign-in stands.
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

// What the free-tier rules read for a plain `forge install`: the account's licensed plugins (GET me `licensed`, and what
// the licence on this computer lists as current) and the account service's offers (GET catalogue, kept a day).
async Task<AccessInputs> AccessInputsAsync(string engineVersion, string channel, IReadOnlySet<string> owned, bool offline, CancellationToken ct) =>
    new()
    {
        Engine = engineVersion, Channel = channel, Owned = owned,
        Licensed = entitlements.IsSignedIn ? FreeTiers.LicensedNow(await entitlements.LicensedAsync(ct), LicenceFor().Snapshot(), owned) : null,
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

static (InstallTarget target, EngineInstall? engine) ResolveTarget(string? engineSpec, string? project)
{
    if (!string.IsNullOrEmpty(project))
        return (InstallTarget.Project(project), null);
    var engine = EngineLocator.Resolve(engineSpec)
                 ?? throw new InvalidOperationException(engineSpec is null ? "No Unreal Engine installation found." : $"No engine matches '{engineSpec}'. Run `forge engines`.");
    return (InstallTarget.Engine(engine), engine);
}
