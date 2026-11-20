using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Forge.Core.Catalogue;

namespace Forge.Hub.ViewModels.Pages;

/// <summary>
/// Home. First run (4.1): "What do you want to make?" and the four track doors. With something installed (4.2),
/// in this order, each block only when it has something to say: the updates, what is left to set up, your
/// tracks, the other tracks, and what the account holds that is not installed here.
/// </summary>
public sealed partial class HomePageViewModel : PageViewModel
{
    private int _generation;

    public HomePageViewModel(MainViewModel hub) : base(hub) => Refresh();

    public override string Title => "Home";

    public ObservableCollection<TrackCardModel> Tracks { get; } = [];
    public ObservableCollection<WideTrackModel> YourTracks { get; } = [];
    public ObservableCollection<TrackCardModel> OtherTracks { get; } = [];
    public ObservableCollection<SetupLineModel> SetupLines { get; } = [];
    public ObservableCollection<OwnedRowModel> NewOnAccount { get; } = [];

    [ObservableProperty] private bool _isFirstRun = true;
    [ObservableProperty] private bool _hasUpdates;
    [ObservableProperty] private string _updatesLine = "";
    [ObservableProperty] private string _updatesByTrack = "";
    [ObservableProperty] private string _quietLine = "";
    [ObservableProperty] private bool _noEngine;
    [ObservableProperty] private string _noEngineLine = "";

    public bool IsReturning => !IsFirstRun;
    public bool HasYourTracks => YourTracks.Count > 0;
    public bool HasOtherTracks => OtherTracks.Count > 0;
    public bool HasSetup => SetupLines.Count > 0;
    public bool HasNewOnAccount => NewOnAccount.Count > 0;
    public bool ShowQuiet => IsReturning && !HasUpdates && !HasSetup;
    partial void OnIsFirstRunChanged(bool value) { OnPropertyChanged(nameof(IsReturning)); OnPropertyChanged(nameof(ShowQuiet)); }
    partial void OnHasUpdatesChanged(bool value) => OnPropertyChanged(nameof(ShowQuiet));

    public override void Refresh()
    {
        var cat = Hub.Catalogue;
        Tracks.Clear();
        YourTracks.Clear();
        OtherTracks.Clear();
        NewOnAccount.Clear();
        NoEngine = !Hub.HasEngine;
        NoEngineLine = $"Unreal Engine {Hub.EngineVersion} was not found on this computer. You can look around now and install when it is there.";
        if (cat is null) return;

        var inputs = Hub.Inputs();
        var installed = Hub.InstalledOnTarget();
        IsFirstRun = installed.Count == 0;

        // Updates, counted per product (8.1), grouped under each product's home track.
        var updates = Hub.ProductUpdates();
        var count = updates.Count + (Hub.HasHubUpdate ? 1 : 0);
        HasUpdates = count > 0;
        UpdatesLine = count == 1 ? "1 update is ready." : $"{count} updates are ready.";
        UpdatesByTrack = string.Join(" · ", updates
            .GroupBy(p => cat.Track(p.Track)?.Name ?? "Other tools")
            .Select(g => $"{g.Key} {g.Count()}")
            .Concat(Hub.HasHubUpdate ? ["Kovati Hub 1"] : []));
        QuietLine = string.IsNullOrEmpty(Hub.CheckedAt) ? "Everything is up to date." : $"Everything is up to date. Checked at {Hub.CheckedAt}.";

        var n = 0;
        foreach (var t in cat.TracksInOrder)
        {
            n++;
            var card = new TrackCardModel(Hub, t, n, FreeToStart(t, cat, inputs));
            Tracks.Add(card);
            if (IsYours(t, cat, installed))
            {
                var caps = Hub.InstalledCapabilities(t);
                var trackUpdates = updates.Count(p => p.Track == t.Slug);
                YourTracks.Add(new WideTrackModel(Hub, t, caps, trackUpdates));
            }
            else OtherTracks.Add(card);
        }

        foreach (var p in Hub.OwnedNotInstalled()) NewOnAccount.Add(new OwnedRowModel(Hub, p, cat));

        OnPropertyChanged(nameof(HasYourTracks));
        OnPropertyChanged(nameof(HasOtherTracks));
        OnPropertyChanged(nameof(HasNewOnAccount));
        _ = RefreshSetupAsync();
    }

    /// <summary>"Finish setting up Animation." for each track installed with v2 whose checklist is not done. Checks off the UI thread.</summary>
    private async Task RefreshSetupAsync()
    {
        var gen = ++_generation;
        if (Hub.Catalogue is not { } cat || Hub.SelectedEngine is null) { SetupLines.Clear(); return; }
        var tracks = cat.TracksInOrder.Where(t => Hub.HubState.Track(t.Slug, Hub.TargetRoot) is { ChecklistHidden: false }).ToList();
        var results = await Task.Run(() => tracks.Select(t => (t, list: Hub.BuildChecklist(t))).ToList());
        if (gen != _generation) return;
        Dispatcher.UIThread.Post(() =>
        {
            SetupLines.Clear();
            foreach (var (t, list) in results)
            {
                if (list is null || list.AllDone) continue;
                SetupLines.Add(new SetupLineModel(Hub, t, list));
                if (YourTracks.FirstOrDefault(y => y.Track.Slug == t.Slug) is { } wide) wide.StepsLeft = list.Left;
            }
            OnPropertyChanged(nameof(HasSetup));
            OnPropertyChanged(nameof(ShowQuiet));
        });
    }

    /// <summary>
    /// A track is yours when it was installed through its page (hub-state.json), or when a tool whose home
    /// track it is is installed (4.2). A shared product does not pull in its second track.
    /// </summary>
    private bool IsYours(Track t, Catalogue cat, IReadOnlyDictionary<string, string> installed)
    {
        if (Hub.SelectedEngine is not null && Hub.HubState.Track(t.Slug, Hub.TargetRoot) is not null) return true;
        return cat.Products.Any(p => p.Track == t.Slug && p.MainComponent is { } main && installed.ContainsKey(main) && p.AddonKind is null);
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
    [RelayCommand] private void SeeWhatChanged() => Hub.ShowUpdates();
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

/// <summary>A wide card under "YOUR TRACKS" (4.2): 548 x 150, the door at the left, the state at the right.</summary>
public sealed partial class WideTrackModel(MainViewModel hub, Track track, IReadOnlyList<Capability> installed, int updates) : ObservableObject
{
    public Track Track { get; } = track;
    public string Name => Track.Name;
    public string? Door => Track.Media.Door;
    public string CapabilitiesLine => $"{installed.Count} of {Words.Count(Track.Capabilities.Count, "capability", "capabilities")} installed";
    public string InstalledNames => string.Join(" · ", installed.Select(c => c.Name));

    [ObservableProperty] private int _stepsLeft;

    /// <summary>"Up to date" (mute), "2 updates" (amber), "1 step left to set up" (amber).</summary>
    public string StateLine => updates > 0 ? Words.Count(updates, "update")
        : StepsLeft > 0 ? $"{Words.Count(StepsLeft, "step")} left to set up"
        : "Up to date";
    public bool StateIsAmber => updates > 0 || StepsLeft > 0;
    partial void OnStepsLeftChanged(int value) { OnPropertyChanged(nameof(StateLine)); OnPropertyChanged(nameof(StateIsAmber)); }

    [RelayCommand] private void Open() => hub.OpenTrack(Track.Slug);
}

/// <summary>"Finish setting up Animation. 2 of 4 done: next, add your Hugging Face token." with Continue.</summary>
public sealed partial class SetupLineModel(MainViewModel hub, Track track, Checklist list)
{
    public string Title => $"Finish setting up {track.Name}.";
    public string Line => $"{list.DoneCount} of {list.Total} done: next, {list.Next?.ShortName}.";

    [RelayCommand] private void Continue() => hub.OpenTrack(track.Slug);
}

/// <summary>A product the account holds that is not installed on this target (4.2, NEW ON YOUR ACCOUNT).</summary>
public sealed partial class OwnedRowModel(MainViewModel hub, Product product, Catalogue cat)
{
    public string Name => product.Name;
    public string? Picture => product.Media.Card ?? product.Media.Hero;
    public string Line => "Not installed on this computer";
    public IReadOnlyList<ChipModel> Chips { get; } = [new("OWNED", "owned")];

    [RelayCommand] private Task Install() => hub.InstallProductAsync(product);
}
