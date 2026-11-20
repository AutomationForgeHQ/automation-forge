using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Forge.Core.Catalogue;

namespace Forge.Hub.ViewModels.Pages;

/// <summary>
/// The set-up checklist (4.5, slice 2). It replaces the install panel on this track page after an install
/// through the page, until every step is done or the person hides it. It checks itself when the page opens
/// and every 5 seconds while the page is in front: steps that tick themselves are trusted.
/// </summary>
public sealed partial class TrackPageViewModel
{
    private Checklist? _checklist;
    private DispatcherTimer? _timer;
    private bool _checking;

    public ObservableCollection<StepModel> Steps { get; } = [];
    public ObservableCollection<string> RememberedProjects { get; } = [];

    [ObservableProperty] private string _checklistCount = "";
    [ObservableProperty] private bool _checklistReady;
    [ObservableProperty] private bool _hasChecklistLink;
    [ObservableProperty] private string _checklistLink = "";
    [ObservableProperty] private string _projectMessage = "";

    public bool IsChecklist => Mode == PanelMode.Checklist;
    public bool HasProjectMessage => ProjectMessage.Length > 0;
    public bool HasRememberedProjects => RememberedProjects.Count > 0;
    public string ReadyLine => $"{Track.Name} is ready.";
    partial void OnProjectMessageChanged(string value) => OnPropertyChanged(nameof(HasProjectMessage));

    /// <summary>Called after every Refresh: builds the checklist off the UI thread and decides whether it shows.</summary>
    private async Task RefreshChecklistAsync(bool openIfDue)
    {
        if (_checking || !Hub.HasEngine || Mode is PanelMode.Installing or PanelMode.SigningIn) return;
        if (Hub.InstalledCapabilities(Track).Count == 0)
        {
            HasChecklistLink = false;
            if (Mode == PanelMode.Checklist) Mode = PanelMode.Ready;
            return;
        }
        _checking = true;
        Checklist? list;
        try { list = await Task.Run(() => Hub.BuildChecklist(Track)); }
        finally { _checking = false; }
        if (list is null) return;
        _checklist = list;
        ApplyChecklist(list);

        var record = Hub.SelectedEngine is not null ? Hub.HubState.Track(Track.Slug, Hub.TargetRoot) : null;
        HasChecklistLink = Mode != PanelMode.Checklist && !list.AllDone;
        ChecklistLink = record is null ? "Set-up checklist" : $"Set-up checklist ({list.Left} left)";

        if (Mode == PanelMode.Checklist && list.AllDone && !ChecklistReady)
        {
            // Shown once; from then on the normal installed panel.
            ChecklistReady = true;
            Hub.SetChecklistHidden(Track, true);
        }
        else if (openIfDue && Mode == PanelMode.Ready && record is { ChecklistHidden: false } && !list.AllDone)
        {
            ShowChecklistPanel();
        }
    }

    private void ApplyChecklist(Checklist list)
    {
        Steps.Clear();
        foreach (var s in list.Steps) Steps.Add(new StepModel(this, s));
        ChecklistCount = list.CountText;
        RememberedProjects.Clear();
        foreach (var p in Hub.RememberedProjects()) RememberedProjects.Add(p);
        OnPropertyChanged(nameof(HasRememberedProjects));
    }

    private void ShowChecklistPanel()
    {
        ChecklistReady = false;
        Mode = PanelMode.Checklist;
        HasChecklistLink = false;
        _timer ??= new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background, (_, _) => Tick());
        _timer.Start();
    }

    private void Tick()
    {
        // Only while this page is in front and the list is on screen.
        if (Hub.CurrentPage != this || Mode != PanelMode.Checklist) { _timer?.Stop(); return; }
        _ = RefreshChecklistAsync(openIfDue: false);
    }

    [RelayCommand]
    private async Task OpenChecklist()
    {
        Hub.SetChecklistHidden(Track, false);
        ShowChecklistPanel();
        await RefreshChecklistAsync(openIfDue: false);
    }

    /// <summary>"Hide this list": the panel goes back to the install panel, with a quiet link to come back.</summary>
    [RelayCommand]
    private void HideChecklist()
    {
        Hub.SetChecklistHidden(Track, true);
        _timer?.Stop();
        Mode = PanelMode.Ready;
        Recompute();
        HasChecklistLink = _checklist is { AllDone: false };
        ChecklistLink = _checklist is null ? "Set-up checklist" : $"Set-up checklist ({_checklist.Left} left)";
    }

    [RelayCommand]
    private Task FinishSettingUp() => OpenChecklist();

    [RelayCommand]
    private async Task ChooseProject()
    {
        var path = await Hub.PickProjectAsync();
        if (path is null) return;
        UseProject(path);
    }

    [RelayCommand]
    private void UseProject(string path)
    {
        ProjectMessage = Hub.EnableTrackInProject(Track, path) ?? "";
        _ = RefreshChecklistAsync(openIfDue: false);
    }

    [RelayCommand]
    private void AddKey(string? keyId) => Hub.ShowKeysCommand.Execute(null);

    [RelayCommand]
    private void GetDocker() => Hub.OpenUrl("https://www.docker.com/products/docker-desktop/");

    /// <summary>"Open the guide ↗": the product's docs. Pressing it is what ticks the last step.</summary>
    [RelayCommand]
    private void OpenFirstGuide(StepModel step)
    {
        var cap = Track.Capability(step.Step.Capability);
        var docs = cap is null ? null : Hub.Catalogue?.Product(cap.Product)?.Docs;
        Hub.OpenUrl(docs ?? Hub.Catalogue?.Links.GettingStarted);
        Hub.MarkFirstResultOpened(Track);
        _ = RefreshChecklistAsync(openIfDue: false);
    }

    [RelayCommand]
    private void OpenRememberedProject()
    {
        if (RememberedProjects.FirstOrDefault() is { } p) Hub.OpenProject(p);
    }

    public string OpenProjectLabel => RememberedProjects.FirstOrDefault() is { } p ? $"Open {Path.GetFileNameWithoutExtension(p)}" : "";
}

/// <summary>One step of the checklist, as the panel draws it.</summary>
public sealed class StepModel(TrackPageViewModel page, ChecklistStep step)
{
    public TrackPageViewModel Page { get; } = page;
    public ChecklistStep Step { get; } = step;
    public string Title => Step.Title;
    public string? Line => Step.Done && Step.DoneLine is not null ? Step.DoneLine : Step.Line;
    public bool HasLine => !string.IsNullOrEmpty(Line);
    public bool Done => Step.Done;
    public bool IsProject => Step.Kind == StepKind.Project;
    public bool ShowChooseProject => IsProject && !Step.Done;
    public bool ShowAnotherProject => IsProject && Step.Done;
    public bool IsFirstResult => Step.Kind == StepKind.FirstResult;
    public bool ShowOpenProject => IsFirstResult && Page.RememberedProjects.Count > 0;
    public string OpenProjectLabel => Page.OpenProjectLabel;
    public IReadOnlyList<EngineBlockModel> Engines { get; } = step.Engines.Select(b => new EngineBlockModel(page, b)).ToList();
    public IReadOnlyList<LineModel> Lines { get; } = step.Lines.Select(l => new LineModel(page, l)).ToList();
    public bool HasEngines => Engines.Count > 0;
    public bool HasLines => Lines.Count > 0;
}

public sealed class EngineBlockModel(TrackPageViewModel page, EngineBlock block)
{
    public string Title => block.Title;
    public IReadOnlyList<LineModel> Lines { get; } = block.Lines.Select(l => new LineModel(page, l)).ToList();
    public string OptionalLine => block.Optional.Count == 0 ? "" : $"Optional: {string.Join(", ", block.Optional.Select(o => o.Text))}";
    public bool HasOptional => block.Optional.Count > 0;
}

public sealed class LineModel(TrackPageViewModel page, ChecklistLine line)
{
    public TrackPageViewModel Page { get; } = page;
    public string Text => line.Text;
    public bool Met => line.Met;
    public bool Info => line.Info;
    public bool Open => !line.Met && !line.Info;
    public bool ShowAddKey => !line.Met && line.Action == LineAction.AddKey;
    public bool ShowGetDocker => !line.Met && line.Action == LineAction.GetDocker;
    public string? KeyId => line.KeyId;
}
