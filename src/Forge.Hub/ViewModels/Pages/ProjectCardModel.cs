using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Forge.Core;
using Forge.Core.Catalogue;
using Forge.Core.Projects;

namespace Forge.Hub.ViewModels.Pages;

/// <summary>
/// One project: a tile in the grid (picture, name, engine, how many Kovati tools) and its page when opened (where
/// it is, what to know before changing it, its Kovati tools, and the products it does not have yet).
/// </summary>
public sealed partial class ProjectCardModel : ObservableObject
{
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
        Writable = editor != EditorOnProject.Yes && !report.IsReadOnly && report.Error is null;
        foreach (var r in report.Plugins
                     .Select(p => new ProjectToolRowModel(this, p, NameOf(cat, p.Id), Latest(manifest, report, p.Id, hub.Channel), Writable))
                     .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
            Rows.Add(r);
        UpdateCount = Rows.Count(r => r.HasUpdate);
        Picture = ProjectGallery.PictureOf(report.Path);
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
            return Report.Plugins.Count == 0 ? ""
                : $"{Words.Count(Report.Plugins.Count, "Kovati tool")} · {on} on · {Report.Plugins.Count(p => p.InProject)} in the project · {Report.Plugins.Count(p => !p.InProject && p.InEngine)} from the engine";
        }
    }

    public ObservableCollection<ChipModel> Chips { get; } = [];
    public ObservableCollection<string> Notes { get; } = [];
    public ObservableCollection<ProjectToolRowModel> Rows { get; } = [];
    public bool HasNotes => Notes.Count > 0;
    public bool HasRows => Rows.Count > 0;

    /// <summary>Nothing stops a write: Unreal does not have it open, the .uproject can be written, it could be read.</summary>
    public bool Writable { get; }
    public bool IsTarget => Page.Hub.TargetProject is { } t && string.Equals(t.Path, Report.Path, StringComparison.OrdinalIgnoreCase);
    public bool CanInstallHere => Report.Engine.IsInstalled && !IsTarget;
    public bool CanOpen => Report.Engine.IsInstalled;
    public bool IsOpenInUnreal => Editor == EditorOnProject.Yes;

    // ── the tile ─────────────────────────────────────────────────────────

    /// <summary>The project's own picture or Unreal's auto screenshot; null for the initials.</summary>
    public string? Picture { get; }
    public string Initials => ProjectGallery.Initials(Name);
    public string EngineBadge => ProjectGallery.EngineBadge(Report.Engine);
    public bool IsEngineMissing => !Report.Engine.IsInstalled;
    public int UpdateCount { get; }
    public string ToolsCountLine => Rows.Count == 0 ? "No Kovati tools" : Words.Count(Rows.Count, "Kovati tool");
    public string UpdatesPart => UpdateCount > 0 ? $" · {Words.Count(UpdateCount, "update")}" : "";
    public string AutomationName => $"{Name}, {(Report.Engine.Version is { } v ? $"Unreal Engine {v}" : "engine unknown")}{(IsEngineMissing ? ", not installed" : "")}, {ToolsCountLine}{UpdatesPart}";

    [RelayCommand] private void Open() => Page.OpenProject(this);
    [RelayCommand] private void InstallHere() => Page.Hub.UseProjectAsTarget(Report);
    [RelayCommand] private void OpenInUnreal() => Page.Hub.OpenProject(Report.Path);

    [RelayCommand]
    private void OpenFolder()
    {
        try { Process.Start(new ProcessStartInfo { FileName = Folder, UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { Page.Hub.Say($"Could not open {Folder}: {ex.Message}"); }
    }

    // ── not in this project ──────────────────────────────────────────────

    public ObservableCollection<MissingProductRowModel> Missing { get; } = [];
    public bool HasMissing => Missing.Count > 0;
    [ObservableProperty] private string _missingLine = "";
    public bool HasMissingLine => MissingLine.Length > 0;
    partial void OnMissingLineChanged(string value) => OnPropertyChanged(nameof(HasMissingLine));

    /// <summary>Why "Install" cannot be pressed here now, or empty.</summary>
    public string InstallBlockedLine =>
        !Report.Engine.IsInstalled ? (Report.Engine.Version is { } v
            ? $"Unreal Engine {v} is not on this computer: install it to install into this project."
            : "Its Unreal Engine is not on this computer, so Kovati Hub does not install into it.")
        : Editor == EditorOnProject.Yes ? "Close Unreal Editor to install into this project: it has it open."
        : Report.IsReadOnly ? $"{Path.GetFileName(Report.Path)} is read-only. Check it out in your source control to install into it."
        : Report.Error is not null ? "Kovati Hub could not read this project, so it does not install into it."
        : "";
    public bool CanInstallMissing => InstallBlockedLine.Length == 0;
    public bool HasInstallBlockedLine => !CanInstallMissing && HasMissing;

    /// <summary>
    /// The catalogue's products built for this project's engine that are neither in the project nor in its engine,
    /// as Browse lists them: listed, on a shelf (no engines, no agent access, no foundation), Labs only with Show Labs on.
    /// Read when the project is opened, not for every tile.
    /// </summary>
    public void BuildMissing()
    {
        Missing.Clear();
        var hub = Page.Hub;
        var cat = hub.Catalogue;
        var inputs = hub.InputsFor(Report);
        if (cat is null || hub.Manifest is null) MissingLine = "The list of tools has not arrived yet.";
        else if (inputs is null) MissingLine = "Kovati Hub cannot tell which Unreal Engine this project uses, so it cannot say what is built for it.";
        else
        {
            var present = new HashSet<string>(Report.Plugins.Where(p => p.Loaded is not null).Select(p => p.Id), StringComparer.OrdinalIgnoreCase);
            var trackOrder = cat.TracksInOrder.Select(t => t.Slug).ToList();
            var built = cat.Products
                .Where(p => p.IsListed && !p.IsEngine && cat.Categories.Contains(p.Type) && p.MainComponent is not null)
                .Where(p => inputs.Manifest.Plugin(p.MainComponent!)?.Latest(inputs.Engine, inputs.Channel) is not null)
                .Where(p => hub.HubState.ShowLabs || TrackPlanner.StageOf(p, inputs) != Stage.Labs)
                .ToList();
            var products = built
                .Where(p => !present.Contains(p.MainComponent!))
                .OrderBy(p => cat.Categories.IndexOf(p.Type))
                .ThenBy(p => trackOrder.IndexOf(p.Track) is var i and >= 0 ? i : 99)
                .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var p in products) Missing.Add(new MissingProductRowModel(this, p, inputs));
            MissingLine = Missing.Count > 0 ? ""
                : built.Count == 0 ? $"Nothing from Kovati is built for Unreal Engine {inputs.Engine} yet."
                : $"Every Kovati product built for Unreal Engine {inputs.Engine} is in this project already.";
        }
        OnPropertyChanged(nameof(HasMissing));
        OnPropertyChanged(nameof(HasInstallBlockedLine));
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

/// <summary>
/// A product the project does not have, built for its engine: its name, its line, the latest version, and one
/// action. Free or owned: Install, into this project. To buy, or not out for this engine: View, its product page.
/// </summary>
public sealed partial class MissingProductRowModel : ObservableObject
{
    private readonly ProjectCardModel _card;

    public MissingProductRowModel(ProjectCardModel card, Product product, PlanInputs inputs)
    {
        _card = card;
        Product = product;
        var hub = card.Page.Hub;
        var cat = hub.Catalogue!;
        var status = TrackPlanner.StatusOf(product, inputs);
        Version = inputs.Manifest.Plugin(product.MainComponent!)?.Latest(inputs.Engine, inputs.Channel)?.Version ?? "";
        IsView = status is ProductStatus.NeedsPurchase or ProductStatus.Coming;
        var owned = product.Plugins.Any(id => inputs.Manifest.Plugin(id)?.IsPaid == true)
                    && product.Plugins.All(id => inputs.Owned.Contains(id) || inputs.Manifest.Plugin(id)?.IsPaid != true);
        var key = ProductFacts.PriceKey(product, inputs.Manifest);
        Chip = owned ? new ChipModel("OWNED", "owned")
            : key != "free" ? ChipModel.Price(cat, key)
            : status == ProductStatus.Labs ? new ChipModel(cat.StageLabel(Stage.Labs).ToUpperInvariant(), "labs")
            : null;
        ActionText = IsView ? "View" : hub.IsSignedIn ? "Install" : "Sign in to install";
        CanAct = IsView || card.CanInstallMissing;
        Tip = IsView ? $"Open {product.Name}'s page" : card.CanInstallMissing ? $"Install {product.Name} into {card.Name} only, in its Plugins\\{Paths.PluginFolder} folder" : card.InstallBlockedLine;
    }

    public Product Product { get; }
    public string Name => Product.Name;
    public string Line => Product.Line;
    public string Version { get; }
    public ChipModel? Chip { get; }
    public bool HasChip => Chip is not null;
    public bool IsView { get; }
    public string ActionText { get; }
    public bool CanAct { get; }
    public string Tip { get; }
    public string AutomationName => $"{ActionText} {Name}";

    [RelayCommand]
    private Task Act()
    {
        var hub = _card.Page.Hub;
        if (IsView) { hub.OpenProduct(Product.Slug); return Task.CompletedTask; }
        if (!hub.IsSignedIn) return hub.SignInCommand.ExecuteAsync(null);
        return hub.InstallIntoProjectAsync(_card.Report, Product);
    }
}
