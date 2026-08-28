using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
/// hub itself elevated, headless, and read its log back.
/// </summary>
public partial class MainViewModel : ViewModelBase
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly ManifestClient _manifestClient;
    private readonly ReleaseDiscovery _releases;
    private readonly InstallState _state = new();
    private readonly IEntitlementProvider _entitlements = new AnonymousEntitlements();
    private Manifest? _manifest;

    public ObservableCollection<EngineInstall> Engines { get; } = [];
    public ObservableCollection<SetGroup> Sets { get; } = [];
    public ObservableCollection<string> Details { get; } = [];

    [ObservableProperty] private EngineInstall? _selectedEngine;
    [ObservableProperty] private SetGroup? _selectedSet;
    [ObservableProperty] private string _sourceStamp = "";
    [ObservableProperty] private string _status = "Ready.";
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private bool _showDetails;
    [ObservableProperty] private int _updatesCount;
    [ObservableProperty] private int _installedCount;
    [ObservableProperty] private string _accountLabel = "Sign in";

    public bool HasUpdates => UpdatesCount > 0;
    public string UpdatesLabel => UpdatesCount == 1 ? "1 update available" : $"{UpdatesCount} updates available";

    public MainViewModel()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("forge-hub/0.1");
        _manifestClient = new ManifestClient(_http);
        _releases = new ReleaseDiscovery(_http);
        foreach (var e in EngineLocator.Find()) Engines.Add(e);
        SelectedEngine = Engines.FirstOrDefault();
        _ = RefreshAsync();
    }

    partial void OnSelectedEngineChanged(EngineInstall? value) => Rebuild();
    partial void OnUpdatesCountChanged(int value) { OnPropertyChanged(nameof(HasUpdates)); OnPropertyChanged(nameof(UpdatesLabel)); }

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

    [RelayCommand]
    private void Login() => Say("Accounts arrive with the Pro backend. Every free set installs without one.");

    [RelayCommand]
    private void ToggleDetails() => ShowDetails = !ShowDetails;

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
                group.Plugins.Add(new PluginRow(p, p.Latest(engine), _state.Find(Target, p.Id), this));
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
        Busy = true;
        try
        {
            if (Installer.IsWritable(Target.Root))
            {
                var installer = new Installer(_http, _state, _entitlements, Say);
                foreach (var id in ids)
                {
                    if (uninstall) { if (!installer.Uninstall(Target, id)) Say($"not installed: {id}"); continue; }
                    var p = _manifest.Plugin(id)!;
                    var v = p.Latest(SelectedEngine.Version);
                    if (v is null) { Say($"skipped: {id} has no release for UE {SelectedEngine.Version}"); continue; }
                    try
                    {
                        Status = $"Downloading {id} {v.Version}…";
                        var r = await installer.InstallAsync(new InstallRequest(p, v, Target), progress: new Progress<double>(f => Status = $"Downloading {id} {v.Version} — {(int)(f * 100)}%"));
                        if (r.Outcome == "already-current") Say($"current: {id} {v.Version}");
                    }
                    catch (Exception ex) when (ex is EntitlementException or InvalidDataException or HttpRequestException)
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

    public string VersionLine =>
        Latest is null ? "no release yet"
        : Installed is null ? $"{Latest.Version} available"
        : HasUpdate ? $"{Installed.Version} installed → {Latest.Version}"
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
