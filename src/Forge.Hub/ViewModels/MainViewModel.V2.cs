using System.Diagnostics;
using System.Text.RegularExpressions;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Forge.Core;
using Forge.Core.Catalogue;
using Forge.Core.Cloud;
using Forge.Core.Engines;
using Forge.Core.Entitlements;
using Forge.Core.Installs;
using Forge.Core.Projects;
using Forge.Hub.ViewModels.Pages;

namespace Forge.Hub.ViewModels;

/// <summary>What one component did during an install, as the install panel shows it (4.5).</summary>
public enum InstallEventKind { Waiting, Started, Progress, Done, Failed, Declined }

public sealed record InstallEvent(string Id, InstallEventKind Kind, double Fraction = 0, string? Message = null);

/// <summary>
/// Kovati Hub v2 on top of the 0.4.4 service: the catalogue, hub-state.json, the pages and the way between
/// them, and installing a plan component by component with progress. The drawers, the runners, the Hub's own
/// update and the per-component list stay where they were (spec 14.1: the old view model is the service).
/// </summary>
public partial class MainViewModel
{
    private bool _previewSignedIn;
    private bool _migrated;
    private bool? _targetWritable;
    private readonly Stack<PageViewModel> _back = new();

    /// <summary>hub-state.json: agent access, Show Labs, the last target, the tracks installed through a track page.</summary>
    public HubState HubState { get; } = HubState.Load();

    /// <summary>Tracks, capabilities, products, words and pictures. Null only when no copy could be read at all.</summary>
    public Catalogue? Catalogue { get; private set; }

    /// <summary>Prices and whether checkout is live, from the account service; null until read.</summary>
    public AccountCatalogue? AccountCatalogue { get; private set; }

    public Core.Manifest.Manifest? Manifest => _manifest;

    [ObservableProperty] private PageViewModel? _currentPage;

    public bool CanGoBack => _back.Count > 0;
    public string BackLabel => _back.Count == 0 ? "" : $"← {_back.Peek().Title}";

    public bool IsHomePlace => CurrentPage is HomePageViewModel;
    public bool IsLibraryPlace => CurrentPage is LibraryPageViewModel or ComponentsPageViewModel;
    public bool IsRunnersPlace => CurrentPage is RunnersPageViewModel;
    public bool IsBrowsePlace => CurrentPage is BrowsePageViewModel;
    public bool IsProjectsPlace => CurrentPage is ProjectsPageViewModel;

    partial void OnCurrentPageChanged(PageViewModel? value)
    {
        OnPropertyChanged(nameof(IsHomePlace));
        OnPropertyChanged(nameof(IsLibraryPlace));
        OnPropertyChanged(nameof(IsRunnersPlace));
        OnPropertyChanged(nameof(IsBrowsePlace));
        OnPropertyChanged(nameof(IsProjectsPlace));
        if (ShowSearch) SearchText = "";
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(BackLabel));
        if (value is RunnersPageViewModel) _ = RefreshRentedAsync();
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private void InitV2()
    {
        Catalogue = CatalogueLoader.Load();
        if (Catalogue is null) Say($"Could not read the list of tracks. {CatalogueLoader.LastError}");
        LoadCachedProjects();
        CurrentPage = new HomePageViewModel(this);
        _ = LoadAccountCatalogueAsync();
        _ = FetchCatalogueAsync();
        Dispatcher.UIThread.Post(OpenFromEnvironment);
    }

    private async Task LoadAccountCatalogueAsync()
    {
        AccountCatalogue = await AccountCatalogue.GetAsync(_http);
        CurrentPage?.Refresh();
    }

    private void RememberTarget()
    {
        _targetWritable = null;
        // Not while the first start is still choosing: the engine is set before the projects are known.
        if (!_targetsReady || Target is null) return;
        if (HubState.LastTarget is { } last && SamePath(last, Target.Root)) return;
        HubState.LastTarget = Target.Root;
        // Not before the first-start migration: saving first would make the file look as if v2 had run before.
        if (_migrated) TrySaveState();
    }

    internal void TrySaveState()
    {
        try { HubState.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Say($"Could not save your choices: {ex.Message}"); }
    }

    /// <summary>After the receipts, the manifest or the account changed: the 6.3 migration once, then the page again.</summary>
    private void AfterRebuild()
    {
        if (!_migrated && _manifest is not null)
        {
            _migrated = true;
            var manifest = _manifest;
            if (HubState.MigrateIfNew(_state.Items.Select(i => i.Plugin), id => manifest.Plugin(id)?.Role == "toolset"))
                Say(HubState.AgentsOn ? "Agent access is on: this machine already had agent components." : "Kovati Hub remembers your choices in hub-state.json.");
        }
        OnPropertyChanged(nameof(HasEngine));
        OnPropertyChanged(nameof(HasManyEngines));
        OnPropertyChanged(nameof(IsSingleEngine));
        OnPropertyChanged(nameof(EngineLabel));
        OnPropertyChanged(nameof(AgentAccessSetting));
        OnPropertyChanged(nameof(ShowLabsSetting));
        RunnerRow.MenuName = EditorMenuName();
        AnnounceAgentActions();
        RecountPill();
        OnPropertyChanged(nameof(IsSignedIn));
        OnPropertyChanged(nameof(AccountInitial));
        CurrentPage?.Refresh();
    }

    // ── the target ───────────────────────────────────────────────────────

    public bool HasEngine => SelectedEngine is not null;
    public bool HasManyEngines => Engines.Count > 1;

    /// <summary>The engine version the screens talk about; 5.8 when none was found, which is the one with builds.</summary>
    public string EngineVersion => SelectedEngine?.Version ?? "5.8";

    public string EngineLabel => SelectedEngine is null ? $"Unreal Engine {EngineVersion} not found" : $"Unreal Engine {SelectedEngine.FullVersion}";

    /// <summary>The engine is in Program Files: Windows will ask once.</summary>
    public bool TargetNeedsAdmin
    {
        get
        {
            if (Target is null) return false;
            _targetWritable ??= Installer.IsWritable(Target.Root);
            return _targetWritable == false;
        }
    }

    /// <summary>Unreal Editor is running from the chosen engine; for a project target, it has that project open.</summary>
    public bool EditorRunningOnTarget()
    {
        if (SelectedEngine is null) return false;
        if (Target?.Kind == "project" && TargetProject is { } project) return EditorProcesses.Check(project.Path) == EditorOnProject.Yes;
        var root = SelectedEngine.Path.TrimEnd('\\', '/');
        var running = false;
        foreach (var p in Process.GetProcessesByName("UnrealEditor"))
        {
            try { if (p.MainModule?.FileName?.StartsWith(root, StringComparison.OrdinalIgnoreCase) == true) running = true; }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            finally { p.Dispose(); }
        }
        return running;
    }

    /// <summary>
    /// The name of the editor plugin's menu inside Unreal (4.5): "Automation Forge" while the installed
    /// AutomationForgeHub is older than 0.3.0, "Kovati" from 0.3.0, so an instruction never names a menu that
    /// is not there.
    /// </summary>
    public string EditorMenuName()
    {
        var installed = InstalledOnTarget();
        return installed.TryGetValue(Core.Manifest.Manifest.HubPlugin, out var v) && SemVer.TryParse(v, out var sv) && sv >= new SemVer(0, 3, 0, null)
            ? "Kovati"
            : "Automation Forge";
    }

    /// <summary>
    /// Component id to version installed on the chosen target. For a project, also the folders in its
    /// Plugins\AutomationForge that have no receipt on this machine (a teammate installed and committed them).
    /// </summary>
    public IReadOnlyDictionary<string, string> InstalledOnTarget()
    {
        if (Target is null) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var map = _state.On(Target).GroupBy(i => i.Plugin, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Version, StringComparer.OrdinalIgnoreCase);
        if (Target.Kind == "project" && TargetProject is { } p)
            foreach (var (id, v) in ManagedInProject(p))
                if (!map.ContainsKey(id) && v.Length > 0) map[id] = v;
        return map;
    }

    /// <summary>Everything the planner reads, as the window knows it now. Null until the manifest and the catalogue are in.</summary>
    public PlanInputs? Inputs() =>
        _manifest is null || Catalogue is null ? null : new PlanInputs
        {
            Catalogue = Catalogue,
            Manifest = _manifest,
            Engine = EngineVersion,
            Channel = Channel,
            TargetKind = Target?.Kind ?? "engine",
            ProvidedElsewhere = ProvidedToTarget(),
            FoundationInProject = true,
            Owned = _owned,
            SignedIn = IsSignedIn,
            AgentAccess = HubState.AgentsOn,
            ShowLabs = HubState.ShowLabs,
            Installed = InstalledOnTarget(),
            Pins = HubState.Pins,
        };

    /// <summary>Receipts on every target but the chosen one: "On other targets" on the Components page (4.9).</summary>
    public IReadOnlyList<OtherReceipt> OtherTargetReceipts() =>
        _state.Items.Where(i => Target is null || !Target.Holds(i.TargetRoot))
            .OrderBy(i => i.TargetRoot, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Plugin)
            .Select(i => new OtherReceipt(i.Plugin, i.Version,
                i.TargetKind == "project" ? Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(i.TargetRoot))) ?? "a project" : $"Unreal Engine {i.Engine}",
                i.Path))
            .ToList();

    public bool PaymentsLive => AccountCatalogue?.PaymentsLive == true;

    /// <summary>"$79.99" for a product the account sells, or null.</summary>
    public string? PriceOf(Product product) => AccountCatalogue?.PriceOf(product.StoreKey);

    /// <summary>
    /// The engine chosen last time by its folder ("Choose its folder…") is a target again after a restart, even
    /// when discovery does not list it. Not under FORGE_ENGINES, which a test uses to see only its own engines.
    /// </summary>
    private void RestoreChosenEngine()
    {
        if (Environment.GetEnvironmentVariable(EngineLocator.OverrideVariable) is { Length: > 0 }) return;
        if (HubState.LastTarget is not { } last || Engines.Any(e => SamePath(e.PluginRoot, last))) return;
        // <engine>\Engine\Plugins\Marketplace\AutomationForge: four folders up is the engine.
        var engine = Path.GetFullPath(Path.Combine(last, "..", "..", "..", ".."));
        if (EngineLocator.FromPath(engine) is { } found) Engines.Add(found);
    }

    /// <summary>"Choose its folder…" (5.1, no engine): an engine folder the launcher and the registry do not list.</summary>
    [RelayCommand]
    private async Task ChooseEngineFolderAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime { MainWindow: { } w }) return;
        var picked = await w.StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions { Title = "Choose the Unreal Engine folder", AllowMultiple = false });
        var path = picked.FirstOrDefault()?.TryGetLocalPath();
        if (path is null) return;
        var engine = EngineLocator.FromPath(path) ?? (Path.GetDirectoryName(path) is { } up ? EngineLocator.FromPath(up) : null);
        if (engine is null) { Say($"No Unreal Engine in {path}. Choose the folder that holds Engine\\Build\\Build.version."); return; }
        if (!Engines.Any(e => SamePath(e.Path, engine.Path))) Engines.Add(engine);
        SelectedEngine = Engines.First(e => SamePath(e.Path, engine.Path));
        if (_migrated) TrySaveState();
        Say($"Installing into Unreal Engine {engine.FullVersion} at {engine.Path}.");
    }

    // ── choices ──────────────────────────────────────────────────────────

    public bool IsSingleEngine => HasEngine && !HasManyEngines;

    /// <summary>The Settings switch for "Use with AI agents": the same remembered choice as the track panel's.</summary>
    public bool AgentAccessSetting
    {
        get => HubState.AgentsOn;
        set { SetAgentAccess(value); OnPropertyChanged(); AnnounceAgentActions(); CurrentPage?.Refresh(); }
    }

    public bool ShowLabsSetting
    {
        get => HubState.ShowLabs;
        set { SetShowLabs(value); OnPropertyChanged(); }
    }

    public void SetAgentAccess(bool on)
    {
        if (HubState.AgentAccess == on) return;
        HubState.AgentAccess = on;
        TrySaveState();
        Say(on ? "Agent access is on for everything you install." : "Agent access is off. What is installed stays.");
    }

    public void SetShowLabs(bool on)
    {
        if (HubState.ShowLabs == on) return;
        HubState.ShowLabs = on;
        TrySaveState();
        CurrentPage?.Refresh();
    }

    public void RecordTrack(string track, TrackPlan plan, TrackSelection selection)
    {
        if (Target is null) return;
        HubState.RecordTrack(track, Target.Root, plan.Capabilities, selection.Engines);
        HubState.LastTarget = Target.Root;
        TrySaveState();
    }

    // ── the way between pages ────────────────────────────────────────────

    public void Navigate(PageViewModel page)
    {
        if (CurrentPage is not null) _back.Push(CurrentPage);
        CurrentPage = page;
    }

    [RelayCommand]
    public void GoBack()
    {
        if (_back.Count == 0) return;
        var page = _back.Pop();
        page.Refresh();
        CurrentPage = page;
    }

    [RelayCommand]
    public void GoHome()
    {
        _back.Clear();
        CurrentPage = new HomePageViewModel(this);
    }

    /// <summary>Library (4.8): Updates, Installed, On your account. "Everything on this machine" is one link away.</summary>
    [RelayCommand]
    public void ShowLibrary()
    {
        _back.Clear();
        CurrentPage = new LibraryPageViewModel(this);
    }

    [RelayCommand]
    public void ShowComponents() => Navigate(new ComponentsPageViewModel(this));

    [RelayCommand]
    public void ShowRunnersPage()
    {
        _back.Clear();
        CurrentPage = new RunnersPageViewModel(this);
    }

    [RelayCommand]
    public void OpenTrack(string slug)
    {
        if (Catalogue?.Track(slug) is { } track) Navigate(new TrackPageViewModel(this, track));
    }

    [RelayCommand]
    public void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try { HubUpdater.OpenInBrowser(url); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { Say($"Could not open {url}: {ex.Message}"); }
    }

    public string SiteUrl(string? path) => Catalogue is null || string.IsNullOrEmpty(path) ? "" : Catalogue.Links.Site.TrimEnd('/') + path;

    /// <summary>
    /// "Add to cart" (4.6). The Hub keeps no cart: slice 1 opens what works today, the account's library at the
    /// product (links.owned + "#" + store id). The cart address (links.cart) takes over in slice 6.
    /// </summary>
    public void AddToCart(Product product)
    {
        if (Catalogue is null || product.StoreKey is not { } id) return;
        OpenUrl($"{Catalogue.Links.Owned}#{id}");
    }

    [RelayCommand] private void OpenDocs() => OpenUrl(Catalogue?.Links.Docs ?? "https://docs.kovati.dev");
    [RelayCommand] private void OpenDiscord() => OpenUrl(Catalogue?.Links.Discord ?? "https://kovati.dev/discord");
    [RelayCommand] private void EmailUs() => OpenUrl("mailto:bojan@kovati.dev");
    [RelayCommand] private void GetUnrealEngine() => OpenUrl(Catalogue?.Links.UnrealEngine ?? "https://www.unrealengine.com/download");

    // ── the account ──────────────────────────────────────────────────────

    /// <summary>The first letter of the name, on the site's gradient, when the sign-in gave no photo.</summary>
    public string AccountInitial => string.IsNullOrWhiteSpace(AccountName) ? "" : AccountName.Trim()[..1].ToUpperInvariant();

    partial void OnAccountNameChanged(string value) => OnPropertyChanged(nameof(AccountInitial));

    /// <summary>
    /// For the screenshot harness only: show the window as a signed-in person would see it, without signing in
    /// to anything. Nothing installs in this state (the account service is never asked).
    /// </summary>
    internal void PreviewAccount(string name, string email, IEnumerable<string> owned)
    {
        _previewSignedIn = true;
        AccountName = name;
        AccountEmail = email;
        _owned = new HashSet<string>(owned, StringComparer.OrdinalIgnoreCase);
        Owned.Clear();
        foreach (var id in _owned.OrderBy(x => x)) Owned.Add(id);
        OwnedLine = $"{_owned.Count} tool{(_owned.Count == 1 ? "" : "s")} on this account.";
        OnPropertyChanged(nameof(IsSignedIn));
        Rebuild();
    }

    /// <summary>Sign in on the way to an install (4.5): true when an account arrived from the browser.</summary>
    internal async Task<bool> SignInForInstallAsync(CancellationToken ct)
    {
        if (IsSignedIn) return true;
        if (!CloudConfig.Configured) { Say("Accounts are not configured in this build."); return false; }
        Say("Opening your browser to sign in. Say yes there.");
        try
        {
            var account = await Handshake.SignInAsync(HubUpdater.OpenInBrowser, TimeSpan.FromMinutes(5), ct);
            if (account is null) { Say("Nothing arrived from the browser in five minutes. Nothing changed."); return false; }
            _entitlements.SignIn(account);
            Say($"Signed in as {account.Email}.");
            AccountChanged();
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Net.Sockets.SocketException)
        {
            Say($"Could not sign in: {ex.Message}");
            return false;
        }
    }

    // ── installing a plan ────────────────────────────────────────────────

    /// <summary>
    /// Install the components of a plan in order, reporting each one. A writable target installs here; an
    /// engine in Program Files goes through the elevated helper once for the whole list, and this follows its
    /// log as it writes. Cancelling stops after the component in progress; what is installed stays.
    /// </summary>
    internal async Task InstallItemsAsync(IReadOnlyList<PlanItem> items, Func<string, string> nameOf, Action<InstallEvent> report, CancellationToken ct)
    {
        if (_manifest is null || Target is null || SelectedEngine is null || items.Count == 0) return;
        void Post(InstallEvent e) => Dispatcher.UIThread.Post(() => report(e));
        var project = Target.Kind == "project" ? TargetProject : null;
        if (project is not null && !await MayWriteProjectAsync(project.Path, descriptorChange: false))
        {
            foreach (var i in items) Post(new InstallEvent(i.Id, InstallEventKind.Declined, Message: $"Close Unreal Editor on {project.Name}, then try again. Nothing changed."));
            return;
        }
        Busy = true;
        try
        {
            if (Installer.IsWritable(Target.Root))
            {
                var installer = new Installer(_http, _state, _entitlements, Say);
                foreach (var item in items)
                {
                    if (ct.IsCancellationRequested) break;
                    var plugin = _manifest.Plugin(item.Id);
                    var version = plugin?.Latest(SelectedEngine.Version, Channel);
                    if (plugin is null || version is null)
                    {
                        Post(new InstallEvent(item.Id, InstallEventKind.Failed, Message: InstallErrors.NoBuild(nameOf(item.Id), SelectedEngine.Version)));
                        continue;
                    }
                    Post(new InstallEvent(item.Id, InstallEventKind.Started));
                    Status = $"Downloading {item.Id} {version.Version}";
                    try
                    {
                        await installer.InstallAsync(new InstallRequest(plugin, version, Target),
                            progress: new Progress<double>(f => Post(new InstallEvent(item.Id, InstallEventKind.Progress, f))));
                        Post(new InstallEvent(item.Id, InstallEventKind.Done));
                    }
                    catch (Exception ex) when (ex is EntitlementException or InvalidDataException or HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException)
                    {
                        Say($"failed: {item.Id}: {ex.Message}");
                        Post(new InstallEvent(item.Id, InstallEventKind.Failed, Message: InstallErrors.Describe(ex, nameOf(item.Id), Target.Root)));
                    }
                }
            }
            else
            {
                await RunElevatedFollowingAsync(items.Select(i => i.Id).ToArray(), nameOf, Post, ct);
            }
        }
        finally
        {
            _state.Reload();
            if (IsSignedIn && !_previewSignedIn) await LoadOwnedAsync(); else Rebuild();
            Busy = false;
            Status = "Ready.";
        }
        if (project is not null)
        {
            SayAfterProjectChange(project, installed: true);
            await ReinspectProjectsAsync();
        }
    }

    private static readonly Regex LogLine = new(@"^(?<verb>start|installed|updated|current|failed|skipped|unknown plugin):\s*(?<id>[A-Za-z0-9]+)\s*(?:—\s*(?<msg>.*))?", RegexOptions.Compiled);

    /// <summary>One administrator prompt for the whole list; the panel ticks lines as the helper's log grows.</summary>
    private async Task RunElevatedFollowingAsync(string[] ids, Func<string, string> nameOf, Action<InstallEvent> post, CancellationToken ct)
    {
        var exe = Environment.ProcessPath;
        if (exe is null) { Say("Kovati Hub could not find its own program file."); return; }
        Say($"Windows will ask for permission once, to write to {Target!.Root}.");
        foreach (var id in ids) post(new InstallEvent(id, InstallEventKind.Waiting));
        Status = "Waiting for Windows permission…";
        var psi = new ProcessStartInfo { FileName = exe, UseShellExecute = true, Verb = "runas", CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        psi.ArgumentList.Add(Elevated.Flag);
        psi.ArgumentList.Add("install");
        foreach (var id in ids) psi.ArgumentList.Add(id);
        AddTargetArguments(psi);
        try
        {
            if (File.Exists(Elevated.LogPath)) File.Delete(Elevated.LogPath);
            using var p = Process.Start(psi);
            if (p is null) { Say("Could not start the elevated helper."); return; }
            Status = $"Installing {ids.Length} component{(ids.Length == 1 ? "" : "s")}…";
            var read = 0;
            void Follow()
            {
                if (!File.Exists(Elevated.LogPath)) return;
                string[] lines;
                try { lines = File.ReadAllLines(Elevated.LogPath); }
                catch (IOException) { return; }
                for (; read < lines.Length; read++)
                {
                    var line = lines[read];
                    Say(line);
                    var m = LogLine.Match(line);
                    if (!m.Success) continue;
                    var id = m.Groups["id"].Value;
                    post(m.Groups["verb"].Value switch
                    {
                        "start" => new InstallEvent(id, InstallEventKind.Started),
                        "failed" or "unknown plugin" => new InstallEvent(id, InstallEventKind.Failed, Message: InstallErrors.Describe(m.Groups["msg"].Value, nameOf(id), Target!.Root)),
                        "skipped" => new InstallEvent(id, InstallEventKind.Failed, Message: InstallErrors.NoBuild(nameOf(id), SelectedEngine!.Version)),
                        _ => new InstallEvent(id, InstallEventKind.Done),
                    });
                }
            }
            while (!p.HasExited)
            {
                Follow();
                await Task.Delay(500, CancellationToken.None);
            }
            Follow();
            if (p.ExitCode != 0) Say($"install finished with errors (code {p.ExitCode}).");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Say("Windows permission was declined. Nothing changed.");
            foreach (var id in ids) post(new InstallEvent(id, InstallEventKind.Declined, Message: "Windows permission was declined. Nothing changed."));
        }
    }
}

/// <summary>The errors of 5.2 as final text: the tool by its plain name, what did not change, one next step.</summary>
public static class InstallErrors
{
    public static string NoBuild(string name, string engine) => $"{name} has no build for Unreal Engine {engine} yet.";

    public static string Describe(Exception ex, string name, string folder) => ex switch
    {
        EntitlementException { Code: "unauthenticated" } => "Your sign-in ran out. Sign in again and the install continues.",
        EntitlementException { Code: "not-owned" } => $"{name} is not on your account.",
        EntitlementException { Code: "unreachable" } => "Could not reach Kovati. Check your connection and try again. What is installed keeps working.",
        _ => Describe(ex.Message, name, folder),
    };

    public static string Describe(string message, string name, string folder)
    {
        if (message.StartsWith("Checksum mismatch", StringComparison.Ordinal))
            return $"The download of {name} was damaged, so it was not installed. Try again. If it happens twice, tell us on Discord.";
        if (message.Contains("in use", StringComparison.OrdinalIgnoreCase) || message.Contains("has it loaded", StringComparison.OrdinalIgnoreCase))
            return $"{name} is in use by Unreal Editor. Close Unreal and try again. The version you had is untouched.";
        if (message.StartsWith("The package", StringComparison.Ordinal))
            return $"The package for {name} is not right, so it was not installed. This is on our side. Tell us on Discord.";
        if (message.Contains("not writable", StringComparison.OrdinalIgnoreCase) || message.Contains("denied", StringComparison.OrdinalIgnoreCase))
            return $"Could not write to {folder}. ({message})";
        if (message.StartsWith("Sign in to install", StringComparison.Ordinal))
            return "Your sign-in ran out. Sign in again and the install continues.";
        if (message.Contains("not on your account", StringComparison.OrdinalIgnoreCase) || message.Contains("not-owned", StringComparison.OrdinalIgnoreCase))
            return $"{name} is not on your account.";
        if (message.Contains("could not be reached", StringComparison.OrdinalIgnoreCase))
            return "Could not reach Kovati. Check your connection and try again. What is installed keeps working.";
        return $"Could not download {name}. Nothing changed for it. ({message})";
    }
}
