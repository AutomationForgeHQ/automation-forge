using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Forge.Core.Machine;

namespace Forge.Hub.ViewModels;

/// <summary>
/// One rented machine on the account.
///
/// Everything here is about money and about stopping. The hub knows nothing about which card suits
/// a model and does not need to: by the time a pod exists, somebody has decided that in the editor,
/// where the model is. What is left is the part worth having in a tray application — seeing that a
/// GPU is running at three dollars an hour, and turning it off.
/// </summary>
public partial class RentedRow(
    Pod pod,
    bool isOurs,
    RunpodClient client,
    Func<string> apiKey,
    Func<Task> refresh) : ViewModelBase
{
    public string Id => pod.Id;

    /// <summary>Named the way the plugin names what it creates, so ours stand out from anything else.</summary>
    public bool IsOurs { get; } = isOurs;

    public string Title => string.IsNullOrWhiteSpace(pod.Name) ? pod.Id : pod.Name;
    public string Gpu => string.IsNullOrWhiteSpace(pod.Gpu) ? "unknown card" : pod.Gpu;

    public bool IsRunning => pod.IsRunning;
    public bool CanStop => pod.IsRunning;
    public bool CanStart => !pod.IsRunning;

    public string StateLine => $"{pod.State.ToLowerInvariant()} · {Gpu}";

    /// <summary>
    /// What it is costing, in the present tense.
    ///
    /// A running pod bills the card's full rate whether or not anything is generating on it, and
    /// that is the number worth putting on screen unprompted rather than leaving to an invoice.
    /// </summary>
    public string CostLine => pod.IsRunning
        ? pod.HourlyPrice > 0
            ? $"${pod.HourlyPrice:0.00} an hour, billing right now"
            : "Running, and billing"
        : "Stopped. Its disk still costs a few cents an hour until the pod is released.";

    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _busy;

    [RelayCommand]
    private Task Stop() => Act((c, k) => c.StopAsync(k, pod.Id), "Stopped. The GPU is free; the disk is still there.");

    [RelayCommand]
    private Task Start() => Act((c, k) => c.StartAsync(k, pod.Id), "Starting. It takes a minute or two to answer.");

    [RelayCommand]
    private Task Release() => Act((c, k) => c.ReleaseAsync(k, pod.Id),
        "Released. Nothing is billing for this machine; a network volume, if there is one, stays.");

    private async Task Act(Func<RunpodClient, string, Task<(bool Ok, string Error)>> call, string success)
    {
        if (Busy) return;

        Busy = true;
        Message = "";

        var key = apiKey();
        var (ok, error) = await call(client, key);

        Message = ok ? success : error;
        Busy = false;

        // Ask Runpod again rather than assuming it did what was asked. A pod can also be stopped
        // from the editor, or from Runpod's own console, while this window is open.
        await refresh();
    }
}
