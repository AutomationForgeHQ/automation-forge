using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Forge.Core;
using Forge.Core.Catalogue;
using Forge.Core.Projects;

namespace Forge.Hub.ViewModels.Pages;

/// <summary>Which projects the grid shows: all, those with Kovati tools, those with an update waiting.</summary>
public enum ProjectFilter { All, WithKovati, Updates }

/// <summary>
/// Projects (0.5.0-beta.4): every Unreal project Kovati Hub knows, as a grid of tiles like the Epic launcher's
/// (picture, name, engine, how many Kovati tools), with a search, an order and three filters. A tile opens the
/// project: what is in it (each tool on or off in the .uproject, its version, an update, Remove) and what is not
/// (the catalogue's products built for its engine, each with Install or View). "Install tools here" still makes the
/// project the install target for the track and product pages.
///
/// The open project is remembered by its path, so a change written from its page (the page is rebuilt from disk
/// after every write) stays on it. A top-level place beside Library, not a Library tab: Library is about the one
/// chosen target, and this page is about every project at once.
/// </summary>
public sealed partial class ProjectsPageViewModel : PageViewModel
{
    public static readonly string[] SortChoices = ["Recently opened", "Name", "Engine version"];

    private string? _openPath;

    public ProjectsPageViewModel(MainViewModel hub) : base(hub)
    {
        hub.PropertyChanged += (_, e) =>
        {
            if (Hub.CurrentPage == this && e.PropertyName is nameof(MainViewModel.ProjectsScanning) or nameof(MainViewModel.ProjectsCheckedAt))
            {
                OnPropertyChanged(nameof(Scanning));
                OnPropertyChanged(nameof(CheckedLine));
            }
        };
        Filters = [new(this, ProjectFilter.All, "All"), new(this, ProjectFilter.WithKovati, "With Kovati tools"), new(this, ProjectFilter.Updates, "Updates available")];
        Refresh();
    }

    public override string Title => "Projects";

    /// <summary>Every project found, in the order found; <see cref="Tiles"/> is what the grid shows of them.</summary>
    public ObservableCollection<ProjectCardModel> Cards { get; } = [];
    public ObservableCollection<ProjectCardModel> Tiles { get; } = [];
    public IReadOnlyList<ProjectFilterChip> Filters { get; }
    public IReadOnlyList<string> Sorts => SortChoices;

    [ObservableProperty] private string _summaryLine = "";
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _sortChoice = SortChoices[0];
    [ObservableProperty] private ProjectFilter _filter = ProjectFilter.All;

    /// <summary>The project open in the detail, or null for the grid.</summary>
    [ObservableProperty] private ProjectCardModel? _selected;

    public bool IsDetail => Selected is not null;
    public bool IsGrid => Selected is null;
    public bool Scanning => Hub.ProjectsScanning;
    public bool HasCards => Cards.Count > 0;
    public bool HasTiles => Tiles.Count > 0;
    public bool IsFilteredEmpty => HasCards && Tiles.Count == 0;
    public string NoMatchLine => SearchText.Trim() is { Length: > 0 } q ? $"No project matches “{q}”."
        : Filter == ProjectFilter.Updates ? "No project has an update waiting."
        : "No project has Kovati tools yet.";

    public string CheckedLine => Hub.ProjectsScanning ? "Looking for projects…"
        : string.IsNullOrEmpty(Hub.ProjectsCheckedAt) ? "" : $"Looked at {Hub.ProjectsCheckedAt}";
    public string SourcesLine =>
        "Found in Unreal's own list of recent projects and the folders it makes projects in"
        + (Hub.ProjectFolders.Count == 0 ? ". Keep projects somewhere else? Add the folder in Settings."
            : $", and in {Words.Count(Hub.ProjectFolders.Count, "folder")} of yours.");

    [RelayCommand] private Task CheckNow() => Hub.RefreshProjectsAsync();
    [RelayCommand] private void ProjectFolders() => Hub.ShowSettings = true;
    [RelayCommand] private Task AddFolder() => Hub.AddProjectFolderCommand.ExecuteAsync(null);

    [RelayCommand]
    private void ClearFilter()
    {
        SearchText = "";
        Filter = ProjectFilter.All;
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnSortChoiceChanged(string value) => ApplyFilter();
    partial void OnFilterChanged(ProjectFilter value) => ApplyFilter();

    partial void OnSelectedChanged(ProjectCardModel? value)
    {
        OnPropertyChanged(nameof(IsDetail));
        OnPropertyChanged(nameof(IsGrid));
    }

    // ── the detail ───────────────────────────────────────────────────────

    /// <summary>A tile was chosen: its project opens, with what is and is not in it.</summary>
    public void OpenProject(ProjectCardModel card)
    {
        _openPath = card.Report.Path;
        card.BuildMissing();
        Selected = card;
    }

    /// <summary>"← Projects": the grid again, as it was left (search, order, filter, scroll).</summary>
    [RelayCommand]
    private void CloseDetail()
    {
        _openPath = null;
        Selected = null;
    }

    public override bool BackInside()
    {
        if (!IsDetail) return false;
        CloseDetail();
        return true;
    }

    // ── reading ──────────────────────────────────────────────────────────

    public override void Refresh()
    {
        Cards.Clear();
        IReadOnlyList<EditorProcess> editors;
        try { editors = EditorProcesses.Running(); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { editors = []; }
        foreach (var r in Hub.ProjectReports) Cards.Add(new ProjectCardModel(this, r, EditorProcesses.Check(r.Path, editors)));
        ApplyFilter();

        // The open project, as it is on disk now; gone from the list, the grid.
        var open = _openPath is null ? null : Cards.FirstOrDefault(c => string.Equals(c.Report.Path, _openPath, StringComparison.OrdinalIgnoreCase));
        if (open is null) _openPath = null;
        else open.BuildMissing();
        Selected = open;

        OnPropertyChanged(nameof(HasCards));
        OnPropertyChanged(nameof(SourcesLine));
        OnPropertyChanged(nameof(CheckedLine));
    }

    private ProjectSort SortOrder => Array.IndexOf(SortChoices, SortChoice) switch
    {
        1 => ProjectSort.Name,
        2 => ProjectSort.EngineVersion,
        _ => ProjectSort.RecentlyOpened,
    };

    private bool Passes(ProjectCardModel c, ProjectFilter f) => f switch
    {
        ProjectFilter.WithKovati => c.HasRows,
        ProjectFilter.Updates => c.UpdateCount > 0,
        _ => true,
    };

    /// <summary>The tiles again from the cards: the search, the filter, the order; the counts on the chips and the summary.</summary>
    private void ApplyFilter()
    {
        var shown = ProjectGallery.Sort(Cards.Where(c => ProjectGallery.Matches(c.Report, SearchText) && Passes(c, Filter)), c => c.Report, SortOrder).ToList();
        Tiles.Clear();
        foreach (var c in shown) Tiles.Add(c);

        foreach (var chip in Filters)
        {
            chip.Selected = chip.Kind == Filter;
            chip.Count = Cards.Count(c => Passes(c, chip.Kind));
        }
        var kovati = Cards.Count(c => c.HasRows);
        var filtered = Filter != ProjectFilter.All || SearchText.Trim().Length > 0;
        SummaryLine = Cards.Count == 0 ? "No projects found yet."
            : filtered ? $"{Tiles.Count} of {Words.Count(Cards.Count, "project")}"
            : $"{Words.Count(Cards.Count, "project")} · {kovati} with Kovati tools";
        OnPropertyChanged(nameof(HasTiles));
        OnPropertyChanged(nameof(IsFilteredEmpty));
        OnPropertyChanged(nameof(NoMatchLine));
    }
}

/// <summary>One of the three filters above the grid: "All 32", "With Kovati tools 6", "Updates available 1".</summary>
public sealed partial class ProjectFilterChip(ProjectsPageViewModel page, ProjectFilter kind, string name) : ObservableObject
{
    public ProjectFilter Kind { get; } = kind;
    [ObservableProperty] private bool _selected;
    [ObservableProperty] private int _count;
    public string Label => $"{name} {Count}";
    partial void OnCountChanged(int value) => OnPropertyChanged(nameof(Label));

    [RelayCommand] private void Choose() => page.Filter = Kind;
}
