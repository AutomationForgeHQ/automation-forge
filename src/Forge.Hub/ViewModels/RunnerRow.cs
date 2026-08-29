using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Forge.Core.Machine;

namespace Forge.Hub.ViewModels;

/// <summary>
/// One thing a plugin can run on this machine, as the hub can see and drive it.
///
/// **What it does and does not do**, because the difference is the whole design. Docker and a
/// rented GPU are outside Unreal — a command line and a REST API — so the hub can look at them
/// itself, and starting the runner before opening the editor is exactly the thing somebody wants
/// from a hub. What it cannot do is *create* a container: the compose file interpolates a model
/// cache, a token and a signature that only the plugin knows, and guessing at them would produce a
/// container that looked right and meant something else. First set-up happens in the editor, once.
///
/// Nothing here is cached across a check. Either surface can start or stop a container while the
/// other is watching, so both ask Docker rather than trusting what they last did.
/// </summary>
public partial class RunnerRow : ViewModelBase
{
    private readonly DeclaredRunner _runner;
    private string _containerId = "";

    public RunnerRow(string plugin, string pluginDir, DeclaredRunner runner)
    {
        Plugin = plugin;
        PluginDir = pluginDir;
        _runner = runner;
        Refresh();
    }

    public string Plugin { get; }
    public string PluginDir { get; }

    public string Title => string.IsNullOrWhiteSpace(_runner.DisplayName) ? _runner.Id : _runner.DisplayName;

    [ObservableProperty] private string _dockerLine = "";
    [ObservableProperty] private string _stateLine = "";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _busy;

    [ObservableProperty] private bool _canStart;
    [ObservableProperty] private bool _canStop;

    /// <summary>Green only when a container is genuinely running.</summary>
    [ObservableProperty] private bool _isRunning;

    /// <summary>Amber: something a person can act on. Red is reserved for actually broken.</summary>
    [ObservableProperty] private bool _needsAttention;

    [RelayCommand]
    private void Start()
    {
        Act(() => DockerCli.Start(_containerId, out var e) ? "" : e, "Started. It may need a minute before it is ready.");
    }

    [RelayCommand]
    private void Stop()
    {
        Act(() => DockerCli.Stop(_containerId, out var e) ? "" : e, "Stopped.");
    }

    [RelayCommand]
    private void Refresh() => Look();

    private void Act(Func<string> action, string success)
    {
        if (Busy) return;

        Busy = true;
        Message = "";

        var error = action();

        Message = string.IsNullOrEmpty(error) ? success : error;
        Busy = false;

        Look();
    }

    private void Look()
    {
        var docker = DockerCli.Probe();

        DockerLine = docker switch
        {
            DockerState.NotInstalled => "Docker is not installed",
            DockerState.NotRunning => "Docker is installed but not running",
            _ => DockerCli.Version() is { Length: > 0 } v ? v : "Docker is running",
        };

        if (docker != DockerState.Running)
        {
            _containerId = "";
            IsRunning = false;
            CanStart = false;
            CanStop = false;
            NeedsAttention = true;

            StateLine = docker == DockerState.NotInstalled
                ? "Install Docker Desktop, then set this runner up once inside the editor."
                : "Start Docker Desktop, then this runner can be started from here.";
            return;
        }

        var state = DockerCli.Container(_runner.ComposeProject, _runner.Service, out _containerId);

        IsRunning = state == ContainerState.Running;
        CanStart = state == ContainerState.Stopped;
        CanStop = state == ContainerState.Running;
        NeedsAttention = state is ContainerState.Missing or ContainerState.Unknown;

        StateLine = state switch
        {
            // Said plainly, because the alternative is somebody waiting for a Start button that is
            // never coming. Creating it needs settings that live in the plugin.
            ContainerState.Missing =>
                "No container yet. Set it up once in the editor — Tools > Automation Forge > "
                + $"{Title} Runner — and it can be started from here afterwards.",

            ContainerState.Stopped => "Stopped. Starting it builds nothing and downloads nothing.",
            ContainerState.Running => "Running.",
            _ => "Docker did not answer when asked about this container.",
        };
    }
}
