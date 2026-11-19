using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Forge.Core.Catalogue;

namespace Forge.Hub.ViewModels.Pages;

/// <summary>
/// Home (4.1, and the plain returning version of slice 1). First run: "What do you want to make?" and the four
/// track doors. With something installed: the updates line first (or "Everything is up to date."), then the
/// doors under "YOUR TRACKS" and "ADD A TRACK".
/// </summary>
public sealed partial class HomePageViewModel : PageViewModel
{
    public HomePageViewModel(MainViewModel hub) : base(hub) => Refresh();

    public override string Title => "Home";

    public ObservableCollection<TrackCardModel> Tracks { get; } = [];
    public ObservableCollection<TrackCardModel> YourTracks { get; } = [];
    public ObservableCollection<TrackCardModel> OtherTracks { get; } = [];

    [ObservableProperty] private bool _isFirstRun = true;
    [ObservableProperty] private bool _hasUpdates;
    [ObservableProperty] private string _updatesLine = "";
    [ObservableProperty] private string _quietLine = "";
    [ObservableProperty] private bool _noEngine;
    [ObservableProperty] private string _noEngineLine = "";

    public bool IsReturning => !IsFirstRun;
    public bool HasYourTracks => YourTracks.Count > 0;
    public bool HasOtherTracks => OtherTracks.Count > 0;
    partial void OnIsFirstRunChanged(bool value) => OnPropertyChanged(nameof(IsReturning));

    public override void Refresh()
    {
        var cat = Hub.Catalogue;
        Tracks.Clear();
        YourTracks.Clear();
        OtherTracks.Clear();
        NoEngine = !Hub.HasEngine;
        NoEngineLine = $"Unreal Engine {Hub.EngineVersion} was not found on this computer. You can look around now and install when it is there.";
        if (cat is null) return;

        var inputs = Hub.Inputs();
        var installed = Hub.InstalledOnTarget();
        IsFirstRun = installed.Count == 0;

        var n = 0;
        foreach (var t in cat.TracksInOrder)
        {
            n++;
            var card = new TrackCardModel(Hub, t, n, FreeToStart(t, cat, inputs));
            Tracks.Add(card);
            if (IsYours(t, cat, installed)) YourTracks.Add(card); else OtherTracks.Add(card);
        }

        HasUpdates = Hub.UpdatesCount > 0;
        UpdatesLine = Hub.UpdatesCount == 1 ? "1 update is ready." : $"{Hub.UpdatesCount} updates are ready.";
        QuietLine = string.IsNullOrEmpty(Hub.CheckedAt) ? "Everything is up to date." : $"Everything is up to date. Checked at {Hub.CheckedAt}.";
        OnPropertyChanged(nameof(HasYourTracks));
        OnPropertyChanged(nameof(HasOtherTracks));
    }

    /// <summary>
    /// A track is yours when it was installed through its page (hub-state.json), or when a product whose home
    /// track it is is installed (4.2). A shared product does not pull in its second track.
    /// </summary>
    private bool IsYours(Track t, Catalogue cat, IReadOnlyDictionary<string, string> installed)
    {
        if (Hub.SelectedEngine is { } e && Hub.HubState.Track(t.Slug, e.PluginRoot) is not null) return true;
        return cat.Products.Any(p => p.Track == t.Slug && p.MainComponent is { } main && installed.ContainsKey(main)
                                     && p.AddonKind is null);
    }

    /// <summary>"Free to start" when the track's default install has at least one free capability (4.1).</summary>
    private static bool FreeToStart(Track t, Catalogue cat, PlanInputs? inputs)
    {
        if (inputs is null) return t.Capabilities.Any(c => cat.Product(c.Product)?.Price == "free");
        var selection = TrackPlanner.DefaultSelection(t, inputs);
        return selection.Capabilities.Any(id => t.Capability(id) is { } c && cat.Product(c.Product) is { } p
                                               && ProductFacts.PriceKey(p, inputs.Manifest) == "free");
    }

    [RelayCommand] private Task UpdateAll() => Hub.UpdateAllCommand.ExecuteAsync(null);
    [RelayCommand] private void SeeEverything() => Hub.ShowComponents();
}

/// <summary>One door on Home: number, chip, picture, subtitle, name; the promise on hover.</summary>
public sealed partial class TrackCardModel(MainViewModel hub, Track track, int number, bool freeToStart) : ObservableObject
{
    public Track Track { get; } = track;
    public string Number { get; } = number.ToString("00");
    public string Name => Track.Name;
    public string Subtitle => Track.Subtitle.ToUpperInvariant();
    public string Promise => Track.Promise;
    public string? Door => Track.Media.Door;
    public bool FreeToStart { get; } = freeToStart;
    public string AccessibleName => FreeToStart ? $"{Track.Name} track. Free to start." : $"{Track.Name} track.";

    [RelayCommand] private void Open() => hub.OpenTrack(Track.Slug);
}
