using System.Collections.ObjectModel;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Forge.Core;
using Forge.Core.Engines;
using Forge.Core.Installs;
using Forge.Core.Projects;
using Forge.Hub.ViewModels.Pages;

namespace Forge.Hub.ViewModels;

/// <summary>
/// One place a person can install into: an Unreal Engine (every project on it can use what is there) or one
/// project (only that project, in its Plugins\AutomationForge). The footer, a track's "Install into" and a product
/// page all choose from the same list (spec 3, footer: "a project the CLI installed into" is a target too).
/// </summary>
public sealed class TargetOption
{
    private TargetOption(string kind, EngineInstall engine, InstallTarget install, string label, string menuLabel, ProjectReport? project, bool hasBuilds)
    {
        Kind = kind; Engine = engine; Install = install; Label = label; MenuLabel = menuLabel; Project = project; HasBuilds = hasBuilds;
    }

    public string Kind { get; }
    public bool IsProject => Kind == "project";
    public EngineInstall Engine { get; }
    public InstallTarget Install { get; }
    public ProjectReport? Project { get; }
    public bool HasBuilds { get; }
    /// <summary>"Unreal Engine 5.8.4 (all projects)", "Colony_NP24 (Unreal Engine 5.8.4)".</summary>
    public string Label { get; }
    /// <summary>The line in a menu: the label, with "no Kovati builds yet" for an engine without any.</summary>
    public string MenuLabel { get; }
    public string Root => Install.Root;

    public static TargetOption ForEngine(EngineInstall e, int built) => new("engine", e, InstallTarget.Engine(e),
        $"{e.DisplayName} (all projects)",
        built > 0 ? $"{e.DisplayName} (all projects)" : $"{e.DisplayName} (all projects) · no Kovati builds yet",
        null, built > 0);

    public static TargetOption ForProject(ProjectReport p, EngineInstall e, int built)
    {
        var install = InstallTarget.Project(p.Path);
        return new("project", e, install with { Label = p.Name }, $"{p.Name} (Unreal Engine {e.FullVersion})",
            $"{p.Name} · project on Unreal Engine {e.FullVersion}" + (built > 0 ? "" : " · no Kovati builds yet"), p, built > 0);
    }

    public override string ToString() => MenuLabel;
}

/// <summary>
/// 0.5.0-beta.3: the Unreal Engines and the projects. Projects are found the way the command line finds them
/// (<see cref="ProjectLocator"/>: Unreal's and the launcher's records, the person's folders, the receipts), off the
/// UI thread, shown from the last discovery (hub-state.json) at once and refreshed at start and on "Check now".
/// Every write into a project waits for its editor to be closed.
/// </summary>
public partial class MainViewModel
{
    public ObservableCollection<TargetOption> Targets { get; } = [];
    public ObservableCollection<ProjectReport> ProjectReports { get; } = [];
    public ObservableCollection<ProjectFolderRow> ProjectFolders { get; } = [];

    [ObservableProperty] private TargetOption? _selectedTarget;
    [ObservableProperty] private bool _projectsScanning;
    [ObservableProperty] private string _projectsCheckedAt = "";

    private bool _syncingTarget;
    private int _discoveryGeneration;

    public bool HasManyTargets => Targets.Count > 1;
    public bool IsSingleTarget => HasEngine && Targets.Count <= 1;
    public bool TargetIsProject => SelectedTarget?.IsProject == true;
    public string TargetRoot => Target?.Root ?? "";

    /// <summary>"Unreal Engine 5.8.4" for an engine, "Colony_NP24 (Unreal Engine 5.8.4)" for a project.</summary>
    public string TargetLabel => SelectedTarget is { IsProject: true } t ? t.Label : EngineLabel;

    /// <summary>The project the window installs into, or null for an engine.</summary>
    public ProjectReport? TargetProject => SelectedTarget?.Project;

    /// <summary>
    /// The target is the truth; the engine follows it. Choosing a project selects its engine too, so builds are
    /// chosen for the Unreal Engine version the project uses.
    /// </summary>
    partial void OnSelectedTargetChanged(TargetOption? value)
    {
        if (value is null || _syncingTarget) return;
        _syncingTarget = true;
        try
        {
            var engine = Engines.FirstOrDefault(e => SamePath(e.Path, value.Engine.Path));
            if (engine is not null && !ReferenceEquals(SelectedEngine, engine)) SelectedEngine = engine; // rebuilds and remembers
            else { Rebuild(); RememberTarget(); }
        }
        finally { _syncingTarget = false; }
        AnnounceTarget();
    }

    /// <summary>The engine changed from elsewhere (the engine folder chooser): its "all projects" target follows.</summary>
    private void SyncTargetToEngine()
    {
        if (_syncingTarget || SelectedEngine is null) return;
        if (SelectedTarget is { } t && SamePath(t.Engine.Path, SelectedEngine.Path)) return;
        if (!Targets.Any(o => !o.IsProject && SamePath(o.Engine.Path, SelectedEngine.Path))) { RebuildTargetsQuietly(); }
        _syncingTarget = true;
        try { SelectedTarget = Targets.FirstOrDefault(o => !o.IsProject && SamePath(o.Engine.Path, SelectedEngine.Path)); }
        finally { _syncingTarget = false; }
        AnnounceTarget();
    }

    /// <summary>True once the targets of the first start are known: before that, nothing is remembered as the last target.</summary>
    private bool _targetsReady;

    private void AnnounceTarget()
    {
        OnPropertyChanged(nameof(HasManyTargets));
        OnPropertyChanged(nameof(IsSingleTarget));
        OnPropertyChanged(nameof(TargetIsProject));
        OnPropertyChanged(nameof(TargetLabel));
        OnPropertyChanged(nameof(TargetProject));
        OnPropertyChanged(nameof(TargetRoot));
    }

    // ── the list of targets ──────────────────────────────────────────────

    /// <summary>Engines first, newest first, then the projects whose engine is on this computer, as last found.</summary>
    private void RebuildTargets()
    {
        var before = SelectedTarget?.Root;
        var keep = before ?? HubState.LastTarget;
        RebuildTargetsQuietly();
        var chosen = (keep is null ? null : Targets.FirstOrDefault(o => SamePath(o.Root, keep)))
                     ?? Targets.FirstOrDefault(o => !o.IsProject && SelectedEngine is not null && SamePath(o.Engine.Path, SelectedEngine.Path))
                     ?? Targets.FirstOrDefault();
        _syncingTarget = true;
        try { SelectedTarget = chosen; }
        finally { _syncingTarget = false; }
        var engine = chosen is null ? null : Engines.FirstOrDefault(e => SamePath(e.Path, chosen.Engine.Path));
        if (engine is not null && !ReferenceEquals(SelectedEngine, engine))
        {
            _syncingTarget = true;
            try { SelectedEngine = engine; }
            finally { _syncingTarget = false; }
        }
        else if (before is null || chosen is null || !SamePath(before, chosen.Root)) Rebuild();
        AnnounceTarget();
    }

    /// <summary>The list again, from the engines and the project reports, without choosing.</summary>
    private void RebuildTargetsQuietly()
    {
        var options = new List<TargetOption>();
        foreach (var e in Engines) options.Add(TargetOption.ForEngine(e, _manifest?.BuiltFor(e.Version, Channel) ?? 1));
        foreach (var p in ProjectReports)
            if (p.Engine.Engine is { } pe && Engines.FirstOrDefault(e => SamePath(e.Path, pe.Path)) is { } engine)
                options.Add(TargetOption.ForProject(p, engine, _manifest?.BuiltFor(engine.Version, Channel) ?? 1));
        var current = SelectedTarget?.Root;
        _syncingTarget = true;
        try
        {
            Targets.Clear();
            foreach (var o in options) Targets.Add(o);
            // The same target, as its new object (a fresh report), so the ComboBoxes keep showing it.
            if (current is not null) SelectedTarget = Targets.FirstOrDefault(o => SamePath(o.Root, current)) ?? SelectedTarget;
        }
        finally { _syncingTarget = false; }
    }

    /// <summary>The window starts with the projects found last time, so a remembered project target is there at once.</summary>
    private void LoadCachedProjects()
    {
        var cached = ProjectService.FromCache(HubState.ProjectCache);
        foreach (var f in HubState.ProjectFolders) ProjectFolders.Add(new ProjectFolderRow(this, f, HubState.ProjectCache?.Folders.FirstOrDefault(c => SamePath(c.Folder, f))?.Line ?? ""));
        ProjectReports.Clear();
        foreach (var r in InspectAll(cached)) ProjectReports.Add(r);
        RebuildTargets();
        _targetsReady = true;
        TargetFromEnvironment();
    }

    private IReadOnlyList<ProjectReport> InspectAll(IReadOnlyList<FoundProject> found)
    {
        var engines = Engines.ToList();
        var ids = new HashSet<string>(_manifest?.Plugins.Select(p => p.Id) ?? KnownIds(), StringComparer.OrdinalIgnoreCase);
        var deps = (_manifest?.Plugins ?? []).ToDictionary(p => p.Id, p => (IReadOnlyList<string>)p.Dependencies, StringComparer.OrdinalIgnoreCase);
        var receipts = _state.Items.ToList();
        var registry = EngineLocator.RegisteredBuilds();
        return found.Select(p => ProjectInspector.Inspect(p, engines, ids, deps, receipts, registry)).ToList();
    }

    /// <summary>Before the manifest is in: the components the catalogue names, so a cached project still shows its tools.</summary>
    private IEnumerable<string> KnownIds() =>
        Catalogue is null ? [] : Catalogue.Products.SelectMany(p => p.Plugins.Concat(p.AgentPlugins)).Concat(Catalogue.Always.SelectMany(a => a.AgentPlugins.Append(a.Plugin)));

    /// <summary>Find the projects again, off the UI thread: at start, on "Check now", and when a folder is added or removed.</summary>
    public async Task RefreshProjectsAsync()
    {
        var gen = ++_discoveryGeneration;
        ProjectsScanning = true;
        try
        {
            var inputs = ProjectService.InputsFor(HubState, _state);
            var (discovery, reports) = await Task.Run(() =>
            {
                var d = ProjectLocator.Discover(inputs);
                return (d, InspectAll(d.Projects));
            });
            if (gen != _discoveryGeneration) return;
            foreach (var s in discovery.Sources) Details.Add($"{DateTime.Now:HH:mm:ss}  projects: {s.Source}: {s.Found}{(s.Note is null ? "" : $" ({s.Note})")}");
            ApplyReports(reports);
            HubState.ProjectCache = ProjectService.ToCache(discovery, DateTime.Now);
            foreach (var row in ProjectFolders)
                row.Line = discovery.Scans.FirstOrDefault(s => SamePath(s.Folder, row.Folder))?.Line ?? row.Line;
            if (_migrated) TrySaveState();
            ProjectsCheckedAt = DateTime.Now.ToString("HH:mm");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Say($"Could not look for projects: {ex.Message}");
        }
        finally
        {
            if (gen == _discoveryGeneration) ProjectsScanning = false;
        }
    }

    /// <summary>Re-read the projects already listed (after a write), without looking for new ones.</summary>
    public async Task ReinspectProjectsAsync()
    {
        var found = ProjectReports.Select(r => r.Found).ToList();
        var reports = await Task.Run(() => InspectAll(found));
        ApplyReports(reports);
    }

    private void ApplyReports(IReadOnlyList<ProjectReport> reports)
    {
        ProjectReports.Clear();
        foreach (var r in reports) ProjectReports.Add(r);
        RebuildTargets();
        TargetFromEnvironment();
        if (CurrentPage is ProjectsPageViewModel or TrackPageViewModel or ProductPageViewModel or LibraryPageViewModel) CurrentPage.Refresh();
    }

    /// <summary>
    /// What a project target already has from elsewhere (its engine, its own copies): the planner leaves them
    /// there. Empty for an engine target.
    /// </summary>
    internal IReadOnlyDictionary<string, string> ProvidedToTarget() =>
        TargetProject is { } p ? ProjectService.ProvidedElsewhere(p) : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Kovati Hub's own copies in a project, by its receipts and by the folders in Plugins\AutomationForge.</summary>
    private Dictionary<string, string> ManagedInProject(ProjectReport p)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pl in p.Plugins)
            if (pl.ProjectCopy is { ManagedByHub: true } c) map[pl.Id] = c.Version ?? "";
        return map;
    }

    // ── choosing a target from a page ────────────────────────────────────

    /// <summary>"Install tools here" on the Projects page: the project becomes the target, and Home shows what to install.</summary>
    public void UseProjectAsTarget(ProjectReport report)
    {
        if (Targets.FirstOrDefault(t => t.IsProject && SamePath(t.Project!.Path, report.Path)) is not { } option)
        {
            Say($"{report.Name} uses {report.Engine.Label}, so Kovati Hub cannot install into it here.");
            return;
        }
        SelectedTarget = option;
        Say($"Installing into {report.Name} only, in its Plugins\\{Paths.PluginFolder} folder.");
        GoHome();
    }

    // ── writing into a project, safely ───────────────────────────────────

    /// <summary>
    /// Whether a project may be written now. Its editor open: no, and the reason is said. An editor whose command line
    /// cannot be read: the person is asked. Read-only .uproject: no (only for a .uproject change).
    /// </summary>
    internal async Task<bool> MayWriteProjectAsync(string uproject, bool descriptorChange)
    {
        var name = Path.GetFileNameWithoutExtension(uproject);
        switch (await Task.Run(() => EditorProcesses.Check(uproject)))
        {
            case EditorOnProject.Yes:
                Say($"Unreal Editor has {name} open. Close it, then try again. Nothing changed.");
                return false;
            case EditorOnProject.Unknown:
                if (!await ConfirmAsync($"Is {name} open in Unreal?",
                        $"Unreal Editor is running, and Kovati Hub cannot tell which project it has open. If it has {name} open, close it first: Unreal keeps its own copy of the project and its tools.",
                        "It is closed, go ahead"))
                {
                    Say("Nothing changed.");
                    return false;
                }
                break;
        }
        if (descriptorChange && File.Exists(uproject) && new FileInfo(uproject).IsReadOnly)
        {
            Say($"{Path.GetFileName(uproject)} is read-only. Check it out in your source control (or make it writable), then try again. Nothing changed.");
            return false;
        }
        return true;
    }

    /// <summary>After a project changed: what its owner should know. No source control command, no Binaries or Intermediate deleted.</summary>
    internal void SayAfterProjectChange(ProjectReport p, bool installed)
    {
        if (p.IsCodeProject) Say($"{p.Name} is a C++ project: rebuild it in your IDE before opening it.");
        if (p.SourceControl is { } vcs)
            Say(installed
                ? $"{p.Name} is in {vcs}: add Plugins/{Paths.PluginFolder} to its ignore file, or commit it for the team."
                : $"{p.Name} is in {vcs}: {Path.GetFileName(p.Path)} now shows as changed.");
    }

    /// <summary>Turn one Kovati tool on or off in a project's .uproject: the one entry changes, nothing else.</summary>
    public async Task SetProjectPluginAsync(ProjectReport p, string id, bool on)
    {
        if (!await MayWriteProjectAsync(p.Path, descriptorChange: true)) { await ReinspectProjectsAsync(); return; }
        try
        {
            var d = new ProjectDescriptor(p.Path);
            if (d.SetEnabled(id, on))
            {
                Say($"{(on ? "Turned on" : "Turned off")} {id} in {p.Name}. Restart Unreal if it is open.");
                SayAfterProjectChange(p, installed: false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            Say($"Could not change {Path.GetFileName(p.Path)}. It may be read-only or checked in. Nothing was turned {(on ? "on" : "off")}. ({ex.Message})");
        }
        await ReinspectProjectsAsync();
    }

    /// <summary>Update or remove Kovati Hub's own copy of a tool in a project, as the project target.</summary>
    public async Task UpdateInProjectAsync(ProjectReport p, string id) => await WithProjectTarget(p, () => RunAsync([id]));
    public async Task RemoveFromProjectAsync(ProjectReport p, string id) => await WithProjectTarget(p, () => RunAsync([id], uninstall: true));

    /// <summary>
    /// "Install" on a project's page, under "Not in this project": the product page's own install (its default
    /// engines, its agent access, the foundation and every dependency, through the planner), with the project as
    /// the target for that one run. The window's chosen target comes back afterwards, as for Update and Remove.
    /// </summary>
    public async Task InstallIntoProjectAsync(ProjectReport p, Forge.Core.Catalogue.Product product) =>
        await WithProjectTarget(p, () => InstallProductWithEnginesAsync(product));

    /// <summary>
    /// What the planner would read with this project as the target, without choosing it: its engine's version,
    /// what Kovati Hub installed into it (receipts and its Plugins\AutomationForge folders), and what it has from
    /// its engine or a copy of its own. Null until the manifest and the catalogue are in, or when the project's
    /// engine version cannot be told.
    /// </summary>
    public Forge.Core.Catalogue.PlanInputs? InputsFor(ProjectReport p)
    {
        if (Inputs() is not { } inputs || p.Engine.Version is not { } version) return null;
        var installed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in _state.Items.Where(r => SamePath(r.TargetRoot, p.PluginRoot))) installed.TryAdd(r.Plugin, r.Version);
        foreach (var (id, v) in ManagedInProject(p))
            if (!installed.ContainsKey(id) && v.Length > 0) installed[id] = v;
        return inputs with
        {
            Engine = p.Engine.Engine?.Version ?? version,
            TargetKind = "project",
            ProvidedElsewhere = ProjectService.ProvidedElsewhere(p),
            Installed = installed,
        };
    }

    private async Task WithProjectTarget(ProjectReport p, Func<Task> action)
    {
        var before = SelectedTarget;
        if (Targets.FirstOrDefault(t => t.IsProject && SamePath(t.Project!.Path, p.Path)) is not { } option)
        {
            Say($"{p.Name} uses {p.Engine.Label}, so Kovati Hub cannot change it here.");
            return;
        }
        SelectedTarget = option;
        try { await action(); }
        finally
        {
            if (before is not null && Targets.FirstOrDefault(t => SamePath(t.Root, before.Root)) is { } back) SelectedTarget = back;
            await ReinspectProjectsAsync();
        }
    }

    // ── Settings: project folders ────────────────────────────────────────

    [RelayCommand]
    private async Task AddProjectFolderAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime { MainWindow: { } w }) return;
        var picked = await w.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose a folder where you keep Unreal projects", AllowMultiple = true });
        var added = 0;
        foreach (var f in picked)
            if (f.TryGetLocalPath() is { } path && AddProjectFolder(path)) added++;
        if (added > 0) await RefreshProjectsAsync();
    }

    /// <summary>Add a folder to look in. False when it is there already.</summary>
    public bool AddProjectFolder(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd('\\', '/');
        if (HubState.ProjectFolders.Any(f => SamePath(f, full))) return false;
        HubState.ProjectFolders.Add(full);
        ProjectFolders.Add(new ProjectFolderRow(this, full, "Looking…"));
        TrySaveState();
        return true;
    }

    public async Task RemoveProjectFolderAsync(string folder)
    {
        HubState.ProjectFolders.RemoveAll(f => SamePath(f, folder));
        if (ProjectFolders.FirstOrDefault(r => SamePath(r.Folder, folder)) is { } row) ProjectFolders.Remove(row);
        TrySaveState();
        await RefreshProjectsAsync();
    }

    public bool HasProjectFolders => ProjectFolders.Count > 0;

    [RelayCommand]
    public void ShowProjects()
    {
        _back.Clear();
        CurrentPage = new ProjectsPageViewModel(this);
    }

    // ── asking once ──────────────────────────────────────────────────────

    [ObservableProperty] private bool _showConfirm;
    [ObservableProperty] private string _confirmTitle = "";
    [ObservableProperty] private string _confirmLine = "";
    [ObservableProperty] private string _confirmOk = "Go ahead";
    private TaskCompletionSource<bool>? _confirm;

    /// <summary>A question over the window, answered with its button or "Cancel".</summary>
    public Task<bool> ConfirmAsync(string title, string line, string ok)
    {
        _confirm?.TrySetResult(false);
        _confirm = new TaskCompletionSource<bool>();
        ConfirmTitle = title;
        ConfirmLine = line;
        ConfirmOk = ok;
        ShowConfirm = true;
        return _confirm.Task;
    }

    [RelayCommand] private void ConfirmYes() { ShowConfirm = false; _confirm?.TrySetResult(true); }
    [RelayCommand] private void ConfirmNo() { ShowConfirm = false; _confirm?.TrySetResult(false); }

    /// <summary>FORGE_HUB_TARGET=&lt;project name or .uproject&gt;: start with that project as the target (local checks only).</summary>
    private void TargetFromEnvironment()
    {
        if (_targetFromEnvironment || Environment.GetEnvironmentVariable("FORGE_HUB_TARGET") is not { Length: > 0 } want) return;
        if (Targets.FirstOrDefault(t => t.IsProject && (string.Equals(t.Project!.Name, want, StringComparison.OrdinalIgnoreCase) || SamePath(t.Project.Path, want))) is { } option)
        {
            _targetFromEnvironment = true;
            SelectedTarget = option;
        }
    }

    private bool _targetFromEnvironment;
}

/// <summary>A folder in Settings, "Project folders", with what its last scan found.</summary>
public sealed partial class ProjectFolderRow(MainViewModel hub, string folder, string line) : ObservableObject
{
    public string Folder { get; } = folder;
    [ObservableProperty] private string _line = line;

    [RelayCommand] private Task Remove() => hub.RemoveProjectFolderAsync(Folder);
}
