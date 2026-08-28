using System.CommandLine;
using Forge.Core;
using Forge.Core.Cloud;
using Forge.Core.Engines;
using Forge.Core.Entitlements;
using Forge.Core.Installs;
using Forge.Core.Manifest;
using Forge.Core.Projects;

// forge — the Automation Forge installer, as a command line.
//
// What CI, scripts and agents use, and what the hub runs when it needs to write
// somewhere privileged. Free plugins need no account; `forge login` arrives with
// the Pro backend and unlocks paid ones.

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
    Console.WriteLine($"manifest {manifest.GeneratedAt}{(fromCache ? " (cached)" : "")} · target {target.target.Label} · channel {channel}");
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
            var avail = latest is null ? "no release yet" : (p.IsPaid && latest.Url is null ? $"{latest.Version} · paid" : latest.Version);
            Console.WriteLine($"  {p.Id,-28} {p.Role,-9} {p.Distribution,-5} {avail,-24} {status}");
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
    return failures == 0 ? 0 : 1;
});
root.Subcommands.Add(install);

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
    if (!Installer.IsWritable(target.target.Root) && !parse.GetValue(noElevate) && !Elevation.IsElevated())
    {
        var code = Elevation.RelaunchElevated(args);
        if (code is null) { Console.Error.WriteLine("Elevation was declined; nothing updated."); return 3; }
        return code.Value;
    }
    foreach (var (p, v) in pending)
        await installer.InstallAsync(new InstallRequest(p, v, target.target), force: false, null, ct);
    return 0;
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

var whoami = new Command("whoami", "Who this machine is signed in as, and what the account owns.");
whoami.SetAction(async (parse, ct) =>
{
    if (!entitlements.IsSignedIn) { Console.WriteLine("  not signed in — `forge login`"); return 0; }
    Console.WriteLine($"  {entitlements.AccountLabel}  ({entitlements.Account!.Uid})");
    try
    {
        var owned = await entitlements.OwnedAsync(ct);
        Console.WriteLine(owned.Count == 0 ? "  owns: nothing paid yet" : $"  owns: {string.Join(", ", owned)}");
    }
    catch (Exception ex) when (ex is EntitlementException or HttpRequestException)
    {
        Console.Error.WriteLine($"  {ex.Message}");
        return 1;
    }
    return 0;
});
root.Subcommands.Add(whoami);

return await root.Parse(args).InvokeAsync();

// ── helpers ───────────────────────────────────────────────────────────────
static (InstallTarget target, EngineInstall? engine) ResolveTarget(string? engineSpec, string? project)
{
    if (!string.IsNullOrEmpty(project))
        return (InstallTarget.Project(project), null);
    var engine = EngineLocator.Resolve(engineSpec)
                 ?? throw new InvalidOperationException(engineSpec is null ? "No Unreal Engine installation found." : $"No engine matches '{engineSpec}'. Run `forge engines`.");
    return (InstallTarget.Engine(engine), engine);
}
