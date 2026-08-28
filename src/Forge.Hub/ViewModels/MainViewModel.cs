using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Forge.Core;
using Forge.Core.Cloud;
using Forge.Core.Engines;
using Forge.Core.Entitlements;
using Forge.Core.Installs;
using Forge.Core.Manifest;
using Forge.Core.Releases;

namespace Forge.Hub.ViewModels;

/// <summary>
/// The hub over Forge.Core. Three sources, one view: the manifest (what each
/// plugin is), the releases repository (what is actually published), and the
/// install receipts (what is on this machine). Privileged writes relaunch the
/// hub itself elevated, headless, and read its log back. The hub also watches
/// its own releases and replaces itself through the installer.
/// </summary>
public partial class MainViewModel : ViewModelBase
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly ManifestClient _manifestClient;
    private readonly ReleaseDiscovery _releases;
    private readonly HubReleases _hubReleases;
    private readonly InstallState _state = new();
    private readonly FirebaseEntitlements _entitlements;
    private readonly Settings _settings;
    private readonly UpdateWatcher? _watcher;
    private Manifest? _manifest;
    private HubRelease? _hubUpdate;

    public ObservableCollection<EngineInstall> Engines { get; } = [];
    public ObservableCollection<SetGroup> Sets { get; } = [];
    public ObservableCollection<string> Details { get; } = [];

    [ObservableProperty] private EngineInstall? _selectedEngine;
    [ObservableProperty] private SetGroup? _selectedSet;
    [ObservableProperty] private string _sourceStamp = "";
    [ObservableProperty] private string _status = "Ready.";
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private bool _showDetails;
    [ObservableProperty] private bool _showSettings;
    [ObservableProperty] private int _updatesCount;
    [ObservableProperty] private int _installedCount;
    [ObservableProperty] private bool _showAccount;
    [ObservableProperty] private Avalonia.Media.Imaging.Bitmap? _avatar;
    [ObservableProperty] private string _accountName = "";
    [ObservableProperty] private string _accountEmail = "";
    [ObservableProperty] private string _accountProviders = "";
    [ObservableProperty] private string _accountUid = "";
    [ObservableProperty] private string _ownedLine = "";
    public ObservableCollection<string> Owned { get; } = [];
    [ObservableProperty] private bool _isNightly;
    [ObservableProperty] private bool _checkForUpdates;
    [ObservableProperty] private bool _runInBackground;
    [ObservableProperty] private bool _notifyOnUpdates;
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private string _hubUpdateLine = "";
    [ObservableProperty] private bool _hasHubUpdate;

    public bool CanAutostart => Autostart.Supported;
    public string WatchLine => _watcher?.Last is { } last
        ? $"Looks every {UpdateWatcher.Interval.TotalHours:0} hours across every engine and project it installed into. Last look {last.CheckedAt:HH:mm}. Nothing installs by itself."
        : $"Looks every {UpdateWatcher.Interval.TotalHours:0} hours across every engine and project it installed into. Nothing installs by itself.";

    public bool HasUpdates => UpdatesCount > 0;
    public string UpdatesLabel => UpdatesCount == 1 ? "1 update available" : $"{UpdatesCount} updates available";

    public string HubVersion => $"hub {AppInfo.Version}";
    public string DataDir => Paths.DataDir;
    public string InstallLocation => HubUpdater.IsInstalled
        ? $"Installed at {HubUpdater.InstallDir}"
        : $"Running from {Path.GetDirectoryName(Environment.ProcessPath)} (not installed)";

    /// <summary>The Stable radio: setting it true is the only way it changes anything.</summary>
    public bool IsStable
    {
        get => !IsNightly;
        set { if (value) IsNightly = false; }
    }

    public string Channel => IsNightly ? Settings.Nightly : Settings.Stable;
    public string HubUpdateLabel => _hubUpdate is null ? "" : $"hub {_hubUpdate.Version} available — {(HubUpdater.IsInstalled ? "update" : "download")}";
    public string HubUpdateAction => HubUpdater.IsInstalled ? "Update now" : "Open release";

    /// <summary>The designer's constructor; the app passes its shared settings and watcher.</summary>
    public MainViewModel() : this(Settings.Load(), null) { }

    public MainViewModel(Settings settings, UpdateWatcher? watcher)
    {
        _settings = settings;
        _watcher = watcher;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(AppInfo.UserAgent("hub"));
        _manifestClient = new ManifestClient(_http);
        _releases = new ReleaseDiscovery(_http);
        _hubReleases = new HubReleases(_http);
        _entitlements = new FirebaseEntitlements(_http);
        if (_entitlements.IsSignedIn) _ = LoadProfileAsync();
        _isNightly = _settings.IsNightly;
        _checkForUpdates = _settings.CheckForUpdates;
        _runInBackground = _settings.RunInBackground;
        _notifyOnUpdates = _settings.NotifyOnUpdates;
        _startWithWindows = Autostart.Enabled;
        foreach (var e in EngineLocator.Find()) Engines.Add(e);
        var preferred = App.PreferredEngine is { } spec ? EngineLocator.Resolve(spec) : null;
        SelectedEngine = Engines.FirstOrDefault(e => preferred is not null && string.Equals(e.Path.TrimEnd('\\', '/'), preferred.Path.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                         ?? Engines.FirstOrDefault();
        _ = RefreshAsync();
        if (_settings.CheckForUpdates) _ = CheckHubUpdateAsync(quiet: true);
    }

    partial void OnRunInBackgroundChanged(bool value) { if (_settings.RunInBackground != value) { _settings.RunInBackground = value; _settings.Save(); } }
    partial void OnNotifyOnUpdatesChanged(bool value) { if (_settings.NotifyOnUpdates != value) { _settings.NotifyOnUpdates = value; _settings.Save(); } }
    partial void OnStartWithWindowsChanged(bool value)
    {
        if (Autostart.Enabled == value) return;
        Autostart.Enabled = value;
        var actual = Autostart.Enabled;
        if (actual != value) StartWithWindows = actual;
    }

    /// <summary>Re-read receipts and releases without a word in the status line — the watcher found something.</summary>
    public async Task RefreshQuietlyAsync()
    {
        try
        {
            var (manifest, _) = await _manifestClient.GetAsync();
            await _releases.MergeIntoAsync(manifest);
            _manifest = manifest;
            _state.Reload();
            Rebuild();
            OnPropertyChanged(nameof(WatchLine));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or InvalidDataException) { }
    }

    partial void OnSelectedEngineChanged(EngineInstall? value) => Rebuild();
    partial void OnUpdatesCountChanged(int value) { OnPropertyChanged(nameof(HasUpdates)); OnPropertyChanged(nameof(UpdatesLabel)); }

    partial void OnIsNightlyChanged(bool value)
    {
        OnPropertyChanged(nameof(IsStable));
        OnPropertyChanged(nameof(Channel));
        if (_settings.Channel == Channel) return;
        _settings.Channel = Channel;
        _settings.Save();
        Rebuild();
        Say(value ? "Nightly channel: the newest build of everything, stable or not." : "Stable channel: tagged releases only.");
        _ = CheckHubUpdateAsync(quiet: true);
    }

    partial void OnCheckForUpdatesChanged(bool value)
    {
        if (_settings.CheckForUpdates == value) return;
        _settings.CheckForUpdates = value;
        _settings.Save();
    }

    private InstallTarget? Target => SelectedEngine is null ? null : InstallTarget.Engine(SelectedEngine);

    [RelayCommand]
    private async Task RefreshAsync()
    {
        Busy = true;
        try
        {
            var (manifest, manifestCached) = await _manifestClient.GetAsync();
            var found = await _releases.MergeIntoAsync(manifest);
            _manifest = manifest;
            SourceStamp = $"{found.Releases} releases · checked {found.CheckedAt.ToLocalTime():HH:mm}{(found.FromCache || manifestCached ? " · offline copy" : "")}";
            _state.Reload();
            Rebuild();
            Say(found.Merged > 0 ? $"Found {found.Merged} release{(found.Merged == 1 ? "" : "s")} the manifest did not list yet." : "Up to date with the releases repository.");
        }
        catch (Exception ex)
        {
            Say($"Could not load the catalogue: {ex.Message}");
        }
        finally { Busy = false; }
    }

    public bool IsSignedIn => _entitlements.IsSignedIn;
    public bool HasAvatar => Avatar is not null;
    public bool ShowAnyPanel => ShowSettings || ShowAccount;

    partial void OnAvatarChanged(Avalonia.Media.Imaging.Bitmap? value) => OnPropertyChanged(nameof(HasAvatar));
    partial void OnShowSettingsChanged(bool value) { if (value) ShowAccount = false; OnPropertyChanged(nameof(ShowAnyPanel)); }
    partial void OnShowAccountChanged(bool value) { if (value) ShowSettings = false; OnPropertyChanged(nameof(ShowAnyPanel)); }

    [RelayCommand] private void OpenAccount() => ShowAccount = true;
    [RelayCommand] private void CloseAccount() => ShowAccount = false;
    public void ClosePanels() { ShowSettings = false; ShowAccount = false; }

    /// <summary>The profile as Firebase knows it now, the photo, and what the account owns.</summary>
    private async Task LoadProfileAsync()
    {
        var account = await _entitlements.RefreshProfileAsync() ?? _entitlements.Account;
        if (account is null) return;
        AccountName = account.Name;
        AccountEmail = account.Email;
        AccountUid = account.Uid;
        AccountProviders = account.Providers.Count == 0 ? "email" : string.Join(", ", account.Providers.Select(p => p.Replace(".com", "")));
        if (account.PhotoUrl is { } url)
        {
            try
            {
                var bytes = await _http.GetByteArrayAsync(url);
                Avatar = new Avalonia.Media.Imaging.Bitmap(new MemoryStream(bytes));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or ArgumentException) { Avatar = null; }
        }
        try
        {
            var owned = await _entitlements.OwnedAsync();
            Owned.Clear();
            foreach (var id in owned.OrderBy(x => x)) Owned.Add(id);
            OwnedLine = owned.Count == 0 ? "Nothing paid yet. Every free set installs without an account." : $"{owned.Count} paid plugin{(owned.Count == 1 ? "" : "s")} on this account.";
        }
        catch (Exception ex) when (ex is EntitlementException or HttpRequestException)
        {
            OwnedLine = ex.Message;
        }
    }

    [RelayCommand]
    private async Task SignInAsync()
    {
        if (!CloudConfig.Configured) { Say("Accounts are not configured in this build."); return; }
        if (_entitlements.IsSignedIn) return;
        Say("Opening your browser to sign in — say yes there.");
        try
        {
            var account = await Handshake.SignInAsync(HubUpdater.OpenInBrowser, TimeSpan.FromMinutes(5));
            if (account is null) { Say("Nothing arrived from the browser in five minutes; nothing changed."); return; }
            _entitlements.SignIn(account);
            Say($"Signed in as {account.Email}.");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Net.Sockets.SocketException)
        {
            Say($"Could not sign in: {ex.Message}");
        }
        AccountChanged();
    }

    [RelayCommand]
    private void SignOut()
    {
        if (!_entitlements.IsSignedIn) return;
        var who = _entitlements.AccountLabel;
        _entitlements.SignOut();
        ShowAccount = false;
        Say($"Signed out {who}.");
        AccountChanged();
    }

    [RelayCommand]
    private void OpenAccountPage() => HubUpdater.OpenInBrowser(CloudConfig.AppUrl);

    private void AccountChanged()
    {
        OnPropertyChanged(nameof(IsSignedIn));
        if (_entitlements.IsSignedIn) _ = LoadProfileAsync();
        else { Avatar = null; AccountName = AccountEmail = AccountUid = AccountProviders = OwnedLine = ""; Owned.Clear(); }
        Rebuild();
    }

    [RelayCommand]
    private void ToggleDetails() => ShowDetails = !ShowDetails;

    [RelayCommand]
    private void ToggleSettings() => ShowSettings = !ShowSettings;

    [RelayCommand]
    private void OpenDataFolder()
    {
        Directory.CreateDirectory(Paths.DataDir);
        Process.Start(new ProcessStartInfo { FileName = Paths.DataDir, UseShellExecute = true });
    }

    [RelayCommand]
    private Task CheckHubUpdate() => CheckHubUpdateAsync(quiet: false);

    private async Task CheckHubUpdateAsync(bool quiet)
    {
        try
        {
            var newer = await _hubReleases.NewerThanAsync(AppInfo.SemVer, Channel);
            _hubUpdate = newer;
            HasHubUpdate = newer is not null;
            OnPropertyChanged(nameof(HubUpdateLabel));
            HubUpdateLine = newer is null
                ? $"{AppInfo.Version} is the newest on the {Channel} channel."
                : $"{newer.Version} is available{(newer.Prerelease ? " (nightly)" : "")}.";
            if (!quiet || newer is not null) Say(newer is null ? "This hub is current." : $"A newer hub is available: {newer.Version}.");
        }
        catch (Exception ex)
        {
            HubUpdateLine = $"Could not check: {ex.Message}";
            if (!quiet) Say(HubUpdateLine);
        }
    }

    [RelayCommand]
    private async Task ApplyHubUpdateAsync()
    {
        if (_hubUpdate is null) return;
        if (!HubUpdater.IsInstalled || _hubUpdate.InstallerUrl is null)
        {
            HubUpdater.OpenInBrowser(_hubUpdate.HtmlUrl);
            return;
        }
        Busy = true;
        try
        {
            Status = $"Downloading hub {_hubUpdate.Version}…";
            var file = await HubUpdater.DownloadAsync(_http, _hubUpdate, new Progress<double>(f => Status = $"Downloading hub {_hubUpdate.Version} — {(int)(f * 100)}%"));
            Say($"Installing hub {_hubUpdate.Version}; the hub restarts by itself.");
            HubUpdater.RunInstaller(file);
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown();
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or IOException or InvalidOperationException)
        {
            Say($"Hub update failed: {ex.Message}");
            Busy = false;
        }
    }

    [RelayCommand]
    private async Task UpdateAllAsync()
    {
        var ids = Sets.SelectMany(s => s.Plugins).Where(r => r.HasUpdate).Select(r => r.Id).ToArray();
        if (ids.Length > 0) await RunAsync(ids);
    }

    private void Rebuild()
    {
        var keep = SelectedSet?.Id;
        Sets.Clear();
        if (_manifest is null || Target is null) return;
        var engine = SelectedEngine!.Version;
        foreach (var set in _manifest.Sets)
        {
            var group = new SetGroup(set.Id, set.Name, this);
            foreach (var id in set.Members)
            {
                if (_manifest.Plugin(id) is not { } p) continue;
                group.Plugins.Add(new PluginRow(p, p.Latest(engine, Channel), _state.Find(Target, p.Id), this));
            }
            group.Refresh();
            Sets.Add(group);
        }
        SelectedSet = Sets.FirstOrDefault(s => s.Id == keep) ?? Sets.FirstOrDefault();
        UpdatesCount = Sets.Sum(s => s.UpdateCount);
        InstalledCount = Sets.Sum(s => s.InstalledCount);
    }

    internal Task InstallAsync(PluginRow row) =>
        _manifest is null || row.Latest is null ? Task.CompletedTask
        : RunAsync(_manifest.Closure(row.Plugin).Select(p => p.Id).ToArray());

    internal Task InstallSetAsync(SetGroup set)
    {
        var ids = set.Plugins.Where(r => r.Latest is not null && !r.IsPaid).Select(r => r.Id).ToArray();
        return ids.Length > 0 ? RunAsync(ids) : Task.CompletedTask;
    }

    internal Task UninstallAsync(PluginRow row) => RunAsync([row.Id], uninstall: true);

    private async Task RunAsync(string[] ids, bool uninstall = false)
    {
        if (_manifest is null || Target is null || SelectedEngine is null) return;
        if (!uninstall) ids = _manifest.WithHubPlugin(ids, Target.Kind).ToArray();
        Busy = true;
        try
        {
            WarnIfEditorRuns();
            if (Installer.IsWritable(Target.Root))
            {
                var installer = new Installer(_http, _state, _entitlements, Say);
                foreach (var id in ids)
                {
                    if (uninstall) { if (!installer.Uninstall(Target, id)) Say($"not installed: {id}"); continue; }
                    var p = _manifest.Plugin(id)!;
                    var v = p.Latest(SelectedEngine.Version, Channel);
                    if (v is null) { Say($"skipped: {id} has no release for UE {SelectedEngine.Version}"); continue; }
                    try
                    {
                        Status = $"Downloading {id} {v.Version}…";
                        var r = await installer.InstallAsync(new InstallRequest(p, v, Target), progress: new Progress<double>(f => Status = $"Downloading {id} {v.Version} — {(int)(f * 100)}%"));
                        if (r.Outcome == "already-current") Say($"current: {id} {v.Version}");
                    }
                    catch (Exception ex) when (ex is EntitlementException or InvalidDataException or HttpRequestException or IOException)
                    {
                        Say($"failed: {id} — {ex.Message}");
                    }
                }
            }
            else
            {
                await RunElevatedAsync(uninstall ? "uninstall" : "install", ids);
            }
        }
        finally
        {
            _state.Reload();
            Rebuild();
            Busy = false;
        }
    }

    /// <summary>A plugin the editor has loaded cannot be replaced; say so before trying, not after.</summary>
    private void WarnIfEditorRuns()
    {
        if (SelectedEngine is null || Target?.Kind != "engine") return;
        var root = SelectedEngine.Path.TrimEnd('\\', '/');
        var running = 0;
        foreach (var p in Process.GetProcessesByName("UnrealEditor"))
        {
            try { if (p.MainModule?.FileName?.StartsWith(root, StringComparison.OrdinalIgnoreCase) == true) running++; }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            finally { p.Dispose(); }
        }
        if (running > 0)
            Say($"Unreal Editor is running from this engine. A plugin it has loaded cannot be replaced — if the install fails, close the editor and try again.");
    }

    /// <summary>Relaunch this executable elevated, headless, then read back what it did.</summary>
    private async Task RunElevatedAsync(string verb, string[] ids)
    {
        var exe = Environment.ProcessPath;
        if (exe is null) { Say("Cannot determine the hub executable to relaunch."); return; }
        Say($"{Target!.Root} needs administrator rights — asking once.");
        Status = "Waiting for administrator approval…";
        var psi = new ProcessStartInfo { FileName = exe, UseShellExecute = true, Verb = "runas", CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        psi.ArgumentList.Add(Elevated.Flag);
        psi.ArgumentList.Add(verb);
        foreach (var id in ids) psi.ArgumentList.Add(id);
        psi.ArgumentList.Add("--engine"); psi.ArgumentList.Add(SelectedEngine!.Path);
        psi.ArgumentList.Add("--channel"); psi.ArgumentList.Add(Channel);
        try
        {
            if (File.Exists(Elevated.LogPath)) File.Delete(Elevated.LogPath);
            using var p = Process.Start(psi);
            if (p is null) { Say("Could not start the elevated helper."); return; }
            Status = $"{(verb == "install" ? "Installing" : "Removing")} {string.Join(", ", ids)}…";
            await p.WaitForExitAsync();
            if (File.Exists(Elevated.LogPath))
                foreach (var line in await File.ReadAllLinesAsync(Elevated.LogPath)) Say(line);
            if (p.ExitCode != 0) Say($"{verb} finished with errors (code {p.ExitCode}).");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Say("Administrator approval was declined; nothing changed.");
        }
    }

    internal void Say(string line)
    {
        Dispatcher.UIThread.Post(() =>
        {
            Status = line;
            Details.Add($"{DateTime.Now:HH:mm:ss}  {line}");
            while (Details.Count > 400) Details.RemoveAt(0);
        });
    }
}

public sealed partial class SetGroup(string id, string name, MainViewModel owner) : ObservableObject
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public ObservableCollection<PluginRow> Plugins { get; } = [];

    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private int _updateCount;
    [ObservableProperty] private int _installedCount;
    [ObservableProperty] private bool _canInstallSet;

    public bool HasUpdates => UpdateCount > 0;

    public void Refresh()
    {
        InstalledCount = Plugins.Count(p => p.Installed is not null);
        UpdateCount = Plugins.Count(p => p.HasUpdate);
        CanInstallSet = Plugins.Any(p => p.CanAct);
        var released = Plugins.Count(p => p.Latest is not null);
        Summary = $"{Plugins.Count} plugins · {InstalledCount} installed"
                  + (released < Plugins.Count ? $" · {released} released" : "")
                  + (UpdateCount > 0 ? $" · {UpdateCount} to update" : "");
        OnPropertyChanged(nameof(HasUpdates));
    }

    [RelayCommand] private Task InstallSet() => owner.InstallSetAsync(this);
}

public sealed partial class PluginRow(PluginInfo plugin, VersionInfo? latest, InstalledPlugin? installed, MainViewModel owner) : ObservableObject
{
    public PluginInfo Plugin { get; } = plugin;
    public VersionInfo? Latest { get; } = latest;
    public InstalledPlugin? Installed { get; } = installed;

    public string Id => Plugin.Id;
    public string Meta => $"{Plugin.Role} · {Plugin.Distribution}" + (Plugin.Dependencies.Count > 0 ? $" · needs {string.Join(", ", Plugin.Dependencies)}" : "");
    public bool IsPaid => Plugin.IsPaid;
    public bool HasUpdate => Installed is not null && Latest is not null && Installed.Version != Latest.Version;

    private string LatestLabel => Latest is null ? "" : Latest.Channel == Settings.Nightly ? $"{Latest.Version} · nightly" : Latest.Version;

    public string VersionLine =>
        Latest is null ? "no release yet"
        : Installed is null ? $"{LatestLabel} available"
        : HasUpdate ? $"{Installed.Version} installed → {LatestLabel}"
        : $"{Installed.Version} installed";

    public string Status =>
        Latest is null ? "not released"
        : IsPaid && Latest.Url is null ? "paid · sign in"
        : Installed is null ? "not installed"
        : HasUpdate ? "update available"
        : "up to date";

    public string ActionLabel =>
        Latest is null ? "—"
        : IsPaid && Latest.Url is null ? "Sign in"
        : Installed is null ? "Install"
        : HasUpdate ? "Update"
        : "Installed";

    public bool CanAct => Latest is not null && !(IsPaid && Latest.Url is null) && (Installed is null || HasUpdate);
    public bool CanUninstall => Installed is not null;
    public bool IsCurrent => Installed is not null && !HasUpdate;

    [RelayCommand(CanExecute = nameof(CanAct))] private Task Act() => owner.InstallAsync(this);
    [RelayCommand(CanExecute = nameof(CanUninstall))] private Task Uninstall() => owner.UninstallAsync(this);
}
