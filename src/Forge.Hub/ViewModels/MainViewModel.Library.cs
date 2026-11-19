using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Forge.Core.Catalogue;
using Forge.Hub.ViewModels.Pages;

namespace Forge.Hub.ViewModels;

/// <summary>Slice 3: the header pill, updating by product, pins, and the way to the Updates tab.</summary>
public partial class MainViewModel
{
    /// <summary>Products with an update on the target, without pinned and without Labs, plus Kovati Hub itself (8.1).</summary>
    [ObservableProperty] private int _pillCount;

    public bool HasPill => PillCount > 0;
    public string PillLabel => PillCount == 1 ? "1 update" : $"{PillCount} updates";
    partial void OnPillCountChanged(int value) { OnPropertyChanged(nameof(HasPill)); OnPropertyChanged(nameof(PillLabel)); }

    public string HubUpdateVersion => _hubUpdate?.Version.ToString() ?? "";

    private void RecountPill()
    {
        var products = Inputs() is { } inputs ? UpdatePlanner.Build(inputs).Count : 0;
        PillCount = products + (HasHubUpdate ? 1 : 0);
    }

    partial void OnHasHubUpdateChanged(bool value) => RecountPill();

    /// <summary>The pill, a notification, or "Check for updates now" when it found some: Library, on Updates.</summary>
    [RelayCommand]
    public void ShowUpdates()
    {
        _back.Clear();
        CurrentPage = new LibraryPageViewModel(this, LibraryTab.Updates);
    }

    public void ShowComponentsFor(string id)
    {
        var page = new ComponentsPageViewModel(this) { Filter = id };
        Navigate(page);
    }

    /// <summary>Update these components (dependencies are already installed), with one administrator prompt when the engine needs it.</summary>
    public Task UpdateComponentsAsync(string[] ids) => RunAsync(ids);

    public Task RemoveComponentAsync(string id) => RunAsync([id], uninstall: true);

    /// <summary>"Pin this version": every installed component of the product, at its installed version (8.3).</summary>
    public void Pin(Product product)
    {
        foreach (var (id, v) in UpdatePlanner.PinsFor(product, InstalledOnTarget())) HubState.Pins[id] = v;
        TrySaveState();
        Say($"{product.Name} is pinned. It is left out of Update all until you unpin it.");
        Rebuild();
    }

    public void Unpin(Product product)
    {
        foreach (var id in product.Plugins.Concat(product.AgentPlugins)) HubState.Pins.Remove(id);
        TrySaveState();
        Say($"{product.Name} is no longer pinned.");
        Rebuild();
    }

    public void PinComponent(string id)
    {
        if (InstalledOnTarget().TryGetValue(id, out var v)) HubState.Pins[id] = v;
        TrySaveState();
        Rebuild();
    }

    public void UnpinComponent(string id)
    {
        HubState.Pins.Remove(id);
        TrySaveState();
        Rebuild();
    }
}
