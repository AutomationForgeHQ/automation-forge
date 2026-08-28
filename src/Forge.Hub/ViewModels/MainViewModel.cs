using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Forge.Core;
using Forge.Core.Engines;
using Forge.Core.Entitlements;
using Forge.Core.Installs;
using Forge.Core.Manifest;

namespace Forge.Hub.ViewModels;

/// <summary>
/// The hub over Forge.Core. Everything the window shows is derived from three
/// things: the manifest, the engines on this machine, and the install receipts.
/// Privileged writes go through the forge CLI relaunched elevated, so the hub
/// itself never needs to run as administrator.
/// </summary>
public partial class MainViewModel : ViewModelBase
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly ManifestClient _manifestClient;
    private readonly InstallState _state = new();
    private readonly IEntitlementProvider _entitlements = new AnonymousEntitlements();
    private Manifest? _manifest;

    public ObservableCollection<EngineInstall> Engines { get; } = [];
    public ObservableCollection<SetGroup> Sets { get; } = [];
    public ObservableCollection<string> Log { get; } = [];

    [ObservableProperty] private EngineInstall? _selectedEngine;
    [ObservableProperty] private string _manifestStamp = "";
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string _accountLabel = "Not signed in";

    public MainViewModel()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("forge-hub/0.1");
        _manifestClient = new ManifestClient(_http);
        foreach (var e in EngineLocator.Find()) Engines.Add(e);
        SelectedEngine = Engines.FirstOrDefault();
        _ = RefreshAsync();
    }

    partial void OnSelectedEngineChanged(EngineInstall? value) => Rebuild();

    private InstallTarget? Target => SelectedEngine is null ? null : InstallTarget.Engine(SelectedEngine);

    [RelayCommand]
    private async Task RefreshAsync()
    {
        Busy = true;
        try
        {
            var (manifest, fromCache) = await _manifestClient.GetAsync();
            _manifest = manifest;
            ManifestStamp = $"manifest {manifest.GeneratedAt}{(fromCache ? " · cached" : "")}";
            Rebuild();
        }
        catch (Exception ex)
        {
            Say($"Could not load the manifest: {ex.Message}");
        }
        finally { Busy = false; }
    }

    [RelayCommand]
    private void Login() => Say("Accounts arrive with the Pro backend. Every free set installs without one.");

    private void Rebuild()
    {
        Sets.Clear();
        if (_manifest is null || Target is null) return;
        var engine = SelectedEngine!.Version;
        foreach (var set in _manifest.Sets)
        {
            var group = new SetGroup(set.Name, this);
            foreach (var id in set.Members)
            {
                if (_manifest.Plugin(id) is not { } p) continue;
                var latest = p.Latest(engine);
                var installed = _state.Find(Target, p.Id);
                group.Plugins.Add(new PluginRow(p, latest, installed, this));
            }
            Sets.Add(group);
        }
    }

    internal async Task InstallAsync(PluginRow row)
    {
        if (_manifest is null || Target is null || row.Latest is null) return;
        var order = _manifest.Closure(row.Plugin);
        await RunAsync(order.Select(p => p.Id).ToArray());
    }

    internal async Task InstallSetAsync(SetGroup set)
    {
        var ids = set.Plugins.Where(r => r.Latest is not null && !r.IsPaid).Select(r => r.Plugin.Id).ToArray();
        if (ids.Length > 0) await RunAsync(ids);
    }

    internal async Task UninstallAsync(PluginRow row)
    {
        if (Target is null) return;
        await RunAsync([row.Plugin.Id], uninstall: true);
    }

    /// <summary>Install or uninstall through Core when the target is writable; through the elevated CLI when it is not.</summary>
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
                    if (uninstall) { installer.Uninstall(Target, id); continue; }
                    var p = _manifest.Plugin(id)!;
                    var v = p.Latest(SelectedEngine.Version);
                    if (v is null) { Say($"skipped: {id} has no release for UE {SelectedEngine.Version}"); continue; }
                    try
                    {
                        var r = await installer.InstallAsync(new InstallRequest(p, v, Target));
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
                await RunCliElevatedAsync(uninstall ? "uninstall" : "install", ids);
                _state.Reload();
            }
        }
        finally
        {
            Busy = false;
            Rebuild();
        }
    }

    private async Task RunCliElevatedAsync(string verb, string[] ids)
    {
        var exe = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "forge.exe" : "forge");
        if (!File.Exists(exe))
        {
            Say($"{Target!.Root} needs administrator rights, and the forge CLI is not beside the hub ({exe}). Run the hub from a published build, or run: forge {verb} {string.Join(' ', ids)} --engine \"{SelectedEngine!.Path}\"");
            return;
        }
        Say($"{Target!.Root} needs administrator rights — asking once.");
        var psi = new ProcessStartInfo { FileName = exe, UseShellExecute = true, Verb = "runas" };
        psi.ArgumentList.Add(verb);
        foreach (var id in ids) psi.ArgumentList.Add(id);
        psi.ArgumentList.Add("--engine"); psi.ArgumentList.Add(SelectedEngine!.Path);
        psi.ArgumentList.Add(Elevation.NoElevateFlag);
        try
        {
            using var p = Process.Start(psi);
            if (p is null) { Say("Could not start the CLI."); return; }
            await p.WaitForExitAsync();
            Say(p.ExitCode == 0 ? $"{verb} finished." : $"{verb} exited with code {p.ExitCode} — see the CLI window.");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Say("Elevation was declined; nothing changed.");
        }
    }

    internal void Say(string line)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Log.Add($"{DateTime.Now:HH:mm:ss}  {line}");
            while (Log.Count > 400) Log.RemoveAt(0);
        });
    }
}

public sealed partial class SetGroup(string name, MainViewModel owner) : ObservableObject
{
    public string Name { get; } = name;
    public ObservableCollection<PluginRow> Plugins { get; } = [];

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
    public string LatestLabel => Latest?.Version ?? "no release yet";
    public string InstalledLabel => Installed?.Version ?? "—";

    public string Status =>
        Latest is null ? "not released"
        : IsPaid && Latest.Url is null ? "paid — sign in to download"
        : Installed is null ? "not installed"
        : Installed.Version == Latest.Version ? "installed"
        : $"update available";

    public string ActionLabel =>
        Latest is null ? "—"
        : IsPaid && Latest.Url is null ? "Sign in"
        : Installed is null ? "Install"
        : Installed.Version == Latest.Version ? "Installed"
        : "Update";

    public bool CanAct => Latest is not null && !(IsPaid && Latest.Url is null) && (Installed is null || Installed.Version != Latest.Version);
    public bool CanUninstall => Installed is not null;

    [RelayCommand(CanExecute = nameof(CanAct))] private Task Act() => owner.InstallAsync(this);
    [RelayCommand(CanExecute = nameof(CanUninstall))] private Task Uninstall() => owner.UninstallAsync(this);
}
