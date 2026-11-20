using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Forge.Core;
using Forge.Core.Catalogue;
using Forge.Core.Projects;

namespace Forge.Hub.ViewModels.Pages;

/// <summary>
/// Projects (0.5.0-beta.3): every Unreal project Kovati Hub knows, with its engine and its Kovati tools: in the
/// project or from the engine, on or off in the .uproject, which version, whether an update is out. A tool is
/// turned on or off here (the one .uproject entry changes), Kovati Hub's own copy in a project is updated or
/// removed, and "Install tools here" makes the project the install target for the track and product pages.
///
/// A top-level place beside Library, not a Library tab: Library is about the one chosen target, and this page is
/// about every project at once; a project is also something to choose as the target, which the Library is read for.
/// </summary>
public sealed partial class ProjectsPageViewModel : PageViewModel
{
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
        Refresh();
    }

    public override string Title => "Projects";

    public ObservableCollection<ProjectCardModel> Cards { get; } = [];
    [ObservableProperty] private string _summaryLine = "";

    public bool Scanning => Hub.ProjectsScanning;
    public bool HasCards => Cards.Count > 0;
    public string CheckedLine => Hub.ProjectsScanning ? "Looking for projects…"
        : string.IsNullOrEmpty(Hub.ProjectsCheckedAt) ? "" : $"Looked at {Hub.ProjectsCheckedAt}";
    public string SourcesLine =>
        "Found in Unreal's own list of recent projects and the folders it makes projects in"
        + (Hub.ProjectFolders.Count == 0 ? ". Keep projects somewhere else? Add the folder in Settings."
            : $", and in {Words.Count(Hub.ProjectFolders.Count, "folder")} of yours.");

    [RelayCommand] private Task CheckNow() => Hub.RefreshProjectsAsync();
    [RelayCommand] private void ProjectFolders() => Hub.ShowSettings = true;
    [RelayCommand] private Task AddFolder() => Hub.AddProjectFolderCommand.ExecuteAsync(null);

    public override void Refresh()
    {
        Cards.Clear();
        IReadOnlyList<EditorProcess> editors;
        try { editors = EditorProcesses.Running(); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { editors = []; }
        foreach (var r in Hub.ProjectReports) Cards.Add(new ProjectCardModel(this, r, EditorProcesses.Check(r.Path, editors)));
        var kovati = Cards.Count(c => c.Rows.Count > 0);
        SummaryLine = Cards.Count == 0 ? "No projects found yet."
            : $"{Words.Count(Cards.Count, "project")} · {kovati} with Kovati tools";
        OnPropertyChanged(nameof(HasCards));
        OnPropertyChanged(nameof(SourcesLine));
        OnPropertyChanged(nameof(CheckedLine));
    }
}

/// <summary>One project: its name, where it is, its engine, what to know before changing it, and its Kovati tools.</summary>
public sealed partial class ProjectCardModel : ObservableObject
{
    private const int Folded = 8;
    private readonly List<ProjectToolRowModel> _all;

    public ProjectCardModel(ProjectsPageViewModel page, ProjectReport report, EditorOnProject editor)
    {
        Page = page;
        Report = report;
        Editor = editor;
        var hub = page.Hub;

        Chips.Add(new ChipModel(report.IsCodeProject ? "C++" : "BLUEPRINT", "stage"));
        if (report.SourceControl is { } vcs) Chips.Add(new ChipModel(vcs.ToUpperInvariant(), "stage"));
        if (report.IsReadOnly) Chips.Add(new ChipModel("READ-ONLY", "soon"));
        if (editor == EditorOnProject.Yes) Chips.Add(new ChipModel("OPEN IN UNREAL", "labs"));

        if (report.Error is { } err) Notes.Add(err);
        if (!report.Engine.IsInstalled) Notes.Add($"It uses {report.Engine.Label}, so Kovati Hub can show it but not install into it.");
        if (editor == EditorOnProject.Yes) Notes.Add("Unreal Editor has it open. Close it to change its tools: Unreal keeps its own copy of the project.");
        if (report.IsReadOnly) Notes.Add($"{Path.GetFileName(report.Path)} is read-only. Check it out in your source control to turn tools on or off.");
        if (report.Plugins.Any(p => p.Overrides)) Notes.Add("A copy in the project overrides the engine's copy of the same tool.");

        var manifest = hub.Manifest;
        var cat = hub.Catalogue;
        var writable = editor != EditorOnProject.Yes && !report.IsReadOnly && report.Error is null;
        _all = report.Plugins
            .Select(p => new ProjectToolRowModel(this, p, NameOf(cat, p.Id), Latest(manifest, report, p.Id, hub.Channel), writable))
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var r in _all.Take(Folded)) Rows.Add(r);
    }

    public ProjectsPageViewModel Page { get; }
    public ProjectReport Report { get; }
    public EditorOnProject Editor { get; }
    public string Name => Report.Name;
    public string Folder => Report.Found.Folder;
    public string EngineLine => Report.Engine.IsInstalled ? Report.Engine.Label
        : Report.Engine.Label.Length > 0 ? char.ToUpperInvariant(Report.Engine.Label[0]) + Report.Engine.Label[1..] : "";
    public string FoundLine => ProjectService.SourcesLine(Report.Found.Sources) is { Length: > 0 } s ? $"Found in {s}" : "";
    public string ToolsLine
    {
        get
        {
            var on = Report.Plugins.Count(p => p.Enabled);
            return (Report.Plugins.Count == 0 ? "No Kovati tools in this project yet"
                : $"{Words.Count(Report.Plugins.Count, "Kovati tool")} · {on} on · {Report.Plugins.Count(p => p.InProject)} in the project · {Report.Plugins.Count(p => !p.InProject && p.InEngine)} from the engine").ToUpperInvariant();
        }
    }

    public ObservableCollection<ChipModel> Chips { get; } = [];
    public ObservableCollection<string> Notes { get; } = [];
    public ObservableCollection<ProjectToolRowModel> Rows { get; } = [];
    public bool HasNotes => Notes.Count > 0;
    public bool HasRows => Rows.Count > 0;

    public bool IsTarget => Page.Hub.TargetProject is { } t && string.Equals(t.Path, Report.Path, StringComparison.OrdinalIgnoreCase);
    public bool CanInstallHere => Report.Engine.IsInstalled && !IsTarget;
    public bool CanOpen => Report.Engine.IsInstalled;

    [ObservableProperty] private bool _expanded;
    public bool CanExpand => _all.Count > Folded;
    public string ExpandLabel => Expanded ? "Show fewer" : $"Show all {_all.Count} tools";

    partial void OnExpandedChanged(bool value)
    {
        Rows.Clear();
        foreach (var r in value ? _all : _all.Take(Folded)) Rows.Add(r);
        OnPropertyChanged(nameof(ExpandLabel));
    }

    [RelayCommand] private void ToggleExpanded() => Expanded = !Expanded;
    [RelayCommand] private void InstallHere() => Page.Hub.UseProjectAsTarget(Report);
    [RelayCommand] private void OpenInUnreal() => Page.Hub.OpenProject(Report.Path);

    [RelayCommand]
    private void OpenFolder()
    {
        try { Process.Start(new ProcessStartInfo { FileName = Folder, UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { Page.Hub.Say($"Could not open {Folder}: {ex.Message}"); }
    }

    /// <summary>A component by the name a person knows: the tool, "{vendor} engine", "Agent access for …", the foundation's name.</summary>
    private static string NameOf(Catalogue? cat, string id)
    {
        if (cat is null) return id;
        if (cat.FoundationName(id) is { } f) return f;
        if (cat.Always.FirstOrDefault(a => a.AgentPlugins.Contains(id, StringComparer.OrdinalIgnoreCase)) is { } fa) return $"Agent access for {fa.Name}";
        if (cat.ProductOfComponent(id) is not { } p) return id;
        if (p.AgentPlugins.Contains(id, StringComparer.OrdinalIgnoreCase)) return $"Agent access for {p.Name}";
        return p.IsEngine ? $"{cat.ShortName(p)} engine" : p.Name;
    }

    private static string? Latest(Core.Manifest.Manifest? m, ProjectReport r, string id, string channel) =>
        r.Engine.Version is { } v ? m?.Plugin(id)?.Latest(v, channel)?.Version : null;
}

/// <summary>One Kovati tool in one project: its name and id, version, where it is, on or off, and its actions.</summary>
public sealed partial class ProjectToolRowModel : ObservableObject
{
    private readonly ProjectCardModel _card;
    private bool _ignore;

    public ProjectToolRowModel(ProjectCardModel card, ProjectPluginState state, string name, string? latest, bool writable)
    {
        _card = card;
        State = state;
        Name = name;
        Latest = latest;
        var engine = card.Report.Engine.Engine?.FullVersion ?? card.Report.Engine.Version ?? "";
        WhereLine = state.Overrides ? $"In the project, over the copy in Unreal Engine {engine}"
            : state.ProjectCopy is { ManagedByHub: true } ? "In the project"
            : state.ProjectCopy is not null ? "In the project, its own copy"
            : state.EngineCopy is not null ? $"From Unreal Engine {engine}"
            : "Named in the project, not found";
        _isOn = state.Enabled;
        CanToggle = writable && state.Loaded is not null;
        var version = state.Loaded?.Version;
        HasUpdate = latest is not null && version is { Length: > 0 } && TrackPlanner.IsNewer(latest, version);
        CanUpdateHere = HasUpdate && state.ProjectCopy is { ManagedByHub: true } && writable && card.Report.Engine.IsInstalled;
        UpdateElsewhereLine = HasUpdate && !CanUpdateHere
            ? state.ProjectCopy is null ? $"{latest} is out: update it for Unreal Engine {engine} in Library" : $"{latest} is out"
            : "";
        CanRemove = state.ProjectCopy is { ManagedByHub: true } && writable && card.Report.Engine.IsInstalled;
    }

    public ProjectPluginState State { get; }
    public string Name { get; }
    public string Id => State.Id;
    public bool ShowId => !string.Equals(Name, Id, StringComparison.Ordinal);
    public string Version => State.Loaded?.Version ?? "";
    public string? Latest { get; }
    public string WhereLine { get; }
    public bool CanToggle { get; }
    public bool HasUpdate { get; }
    public bool CanUpdateHere { get; }
    public string UpdateLabel => $"Update to {Latest}";
    public string UpdateElsewhereLine { get; }
    public bool HasUpdateElsewhere => UpdateElsewhereLine.Length > 0;
    public bool CanRemove { get; }

    /// <summary>"On", "Off", "On by default", "On: MotionForge needs it".</summary>
    public string StateLine => State.Reason switch
    {
        EnabledReason.Explicit => IsOn ? "On" : "Off",
        EnabledReason.ProjectDefault => IsOn ? "On by default" : "Off by default",
        EnabledReason.EngineDefault => IsOn ? "On by default" : "Not turned on",
        EnabledReason.NeededByEnabled => $"On: {State.NeededBy} needs it",
        _ => "Missing",
    };

    [ObservableProperty] private bool _isOn;

    partial void OnIsOnChanged(bool value)
    {
        if (_ignore) return;
        _ = Apply(value);
    }

    private async Task Apply(bool on)
    {
        await _card.Page.Hub.SetProjectPluginAsync(_card.Report, Id, on);
        // The page is rebuilt from the file as it is now; this row only goes back if nothing was written.
        _ignore = true;
        IsOn = State.Enabled;
        _ignore = false;
    }

    [RelayCommand] private Task Update() => _card.Page.Hub.UpdateInProjectAsync(_card.Report, Id);
    [RelayCommand] private Task Remove() => _card.Page.Hub.RemoveFromProjectAsync(_card.Report, Id);
}
