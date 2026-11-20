using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Forge.Core;
using Forge.Core.Catalogue;

namespace Forge.Hub.ViewModels.Pages;

public enum PanelMode { Ready, SigningIn, Installing, Done, Checklist }

/// <summary>
/// A track page (4.3): the track's picture and words, its capabilities in the site's order, and the install
/// panel that always says what will happen. The page is the installer: what is ticked on the left is what the
/// panel counts on the right, through the same planner the command line uses.
/// </summary>
public sealed partial class TrackPageViewModel : PageViewModel
{
    private TrackSelection _selection = new([], new Dictionary<string, IReadOnlyCollection<string>>());
    private bool _selectionSet;
    private TrackPlan? _plan;
    /// <summary>The plan as it stands, for the screenshot harness.</summary>
    internal TrackPlan? CurrentPlan => _plan;
    private TrackPlan? _planWithAgents;
    private CancellationTokenSource? _cts;
    private bool _refreshing;

    public TrackPageViewModel(MainViewModel hub, Track track) : base(hub)
    {
        Track = track;
        _agentsOn = hub.HubState.AgentsOn;
        Refresh();
    }

    public Track Track { get; }
    public override string Title => Track.Name;
    public Catalogue? Cat => Hub.Catalogue;

    // ── the left column ──────────────────────────────────────────────────

    public string? Hero => Track.Media.Hero;
    public string Eyebrow => Track.Eyebrow.ToUpperInvariant();
    public string Promise => Track.Promise;
    public string Lede => Track.Lede;
    public bool HasFilm => Track.Film?.Src is not null;
    public string NextLine => Track.Next?.Line ?? "";
    public string NextName => Track.Next is { } n && Cat?.Track(n.Track) is { } t ? $"See {t.Name} →" : "";
    public bool HasNext => Track.Next is { } n && Cat?.Track(n.Track) is not null;
    public IReadOnlyList<NameLine> Needs => Track.Needs;

    public ObservableCollection<CapabilityRowModel> Rows { get; } = [];

    [ObservableProperty] private string _countLine = "";
    [ObservableProperty] private bool _hasHiddenLabs;
    [ObservableProperty] private string _accountNews = "";

    [RelayCommand] private void OpenSite() => Hub.OpenUrl(Hub.SiteUrl(Track.Site));

    /// <summary>"The film": slices 1 to 4 open the track on kovati.dev; the overlay with the film comes in slice 5 (12.3).</summary>
    [RelayCommand] private void OpenFilm() => Hub.OpenUrl(Hub.SiteUrl(Track.Site));

    [RelayCommand] private void OpenNext() { if (Track.Next is { } n) Hub.OpenTrack(n.Track); }
    [RelayCommand] private void ShowLabs() => Hub.SetShowLabs(true);

    // ── the install panel ────────────────────────────────────────────────

    [ObservableProperty] private bool _agentsOn;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _sizeLine = "";
    [ObservableProperty] private string _alreadyHereLine = "";
    [ObservableProperty] private string _agentsLine = "";
    [ObservableProperty] private string _buttonText = "";
    [ObservableProperty] private bool _buttonEnabled;
    [ObservableProperty] private bool _buttonShowsTick;
    [ObservableProperty] private bool _showComponents;
    [ObservableProperty] private PanelMode _mode = PanelMode.Ready;

    public ObservableCollection<string> Notes { get; } = [];
    public ObservableCollection<string> LaterLines { get; } = [];
    public ObservableCollection<ComponentGroupModel> ComponentGroups { get; } = [];
    public ObservableCollection<NotIncludedModel> NotIncluded { get; } = [];

    public bool HasAlreadyHere => AlreadyHereLine.Length > 0;
    public bool HasNotIncluded => NotIncluded.Count > 0;
    public bool HasManyNotIncluded => NotIncluded.Count > 1;
    public string AddAllLabel => Hub.PaymentsLive ? $"Add {NotIncluded.Count} to cart{AddAllPrice}" : "Buying opens soon";
    private string AddAllPrice
    {
        get
        {
            var cents = NotIncluded.Sum(n => n.Cents);
            return cents > 0 ? $" · {Words.Price(cents)}" : "";
        }
    }
    public bool PaymentsLive => Hub.PaymentsLive;
    public bool NoEngine => !Hub.HasEngine;
    public bool HasManyEngines => Hub.HasManyTargets;
    public string TargetLabel => Hub.TargetLabel;
    public string ShowComponentsLabel => ShowComponents ? "Hide components" : "Show components";

    public bool IsReady => Mode == PanelMode.Ready;
    public bool IsSigningIn => Mode == PanelMode.SigningIn;
    public bool IsInstalling => Mode == PanelMode.Installing;
    public bool IsDone => Mode == PanelMode.Done;
    public bool RowsLocked => Mode is PanelMode.Installing or PanelMode.SigningIn;

    partial void OnModeChanged(PanelMode value)
    {
        OnPropertyChanged(nameof(IsReady));
        OnPropertyChanged(nameof(IsSigningIn));
        OnPropertyChanged(nameof(IsInstalling));
        OnPropertyChanged(nameof(IsDone));
        OnPropertyChanged(nameof(RowsLocked));
        OnPropertyChanged(nameof(IsChecklist));
        foreach (var r in Rows) r.Locked = RowsLocked;
    }

    partial void OnShowComponentsChanged(bool value) => OnPropertyChanged(nameof(ShowComponentsLabel));

    partial void OnAgentsOnChanged(bool value)
    {
        if (_refreshing) return;
        Hub.SetAgentAccess(value);
        Recompute();
    }

    [RelayCommand] private void ToggleComponents() => ShowComponents = !ShowComponents;
    [RelayCommand] private void EverythingOnThisMachine() => Hub.ShowComponents();
    [RelayCommand] private void GetUnrealEngine() => Hub.OpenUrl(Hub.Catalogue?.Links.UnrealEngine);

    [RelayCommand]
    private void AddAllToCart()
    {
        if (!Hub.PaymentsLive || Hub.Catalogue is null) return;
        Hub.OpenUrl(Hub.Catalogue.Links.Owned);
    }

    // ── installing ───────────────────────────────────────────────────────

    public ObservableCollection<InstallLineModel> InstallLines { get; } = [];
    public ObservableCollection<FailureModel> Failures { get; } = [];
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string _statusLine = "";
    [ObservableProperty] private string _doneLabel = "INSTALLED";
    [ObservableProperty] private string _doneTitle = "";
    [ObservableProperty] private string _doneLine = "";
    [ObservableProperty] private string _panelMessage = "";
    [ObservableProperty] private bool _stopped;

    public bool HasFailures => Failures.Count > 0;
    public bool HasPanelMessage => PanelMessage.Length > 0;
    partial void OnPanelMessageChanged(string value) => OnPropertyChanged(nameof(HasPanelMessage));

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void OpenGuide() => Hub.OpenUrl(Hub.Catalogue?.Links.GettingStarted);

    [RelayCommand]
    private void BackToPanel()
    {
        Mode = PanelMode.Ready;
        Recompute();
    }

    [RelayCommand]
    private Task Install() => InstallAsync(null);

    /// <summary>The whole selection; or, from a row's "Update to …", that one capability's updates.</summary>
    internal async Task InstallAsync(string? onlyCapability)
    {
        if (!Hub.HasEngine || Mode is PanelMode.Installing or PanelMode.SigningIn) return;
        _cts = new CancellationTokenSource();
        PanelMessage = "";

        if (!Hub.IsSignedIn)
        {
            Mode = PanelMode.SigningIn;
            var ok = await Hub.SignInForInstallAsync(_cts.Token);
            if (!ok)
            {
                Mode = PanelMode.Ready;
                PanelMessage = _cts.IsCancellationRequested ? "" : "Nothing arrived from the browser in five minutes. Nothing changed.";
                Recompute();
                return;
            }
            Recompute(); // ownership may have changed what can be installed
        }

        if (_plan is null) { Mode = PanelMode.Ready; return; }
        var items = _plan.ToChange.Where(i => onlyCapability is null || i.Capability == onlyCapability || i.Role == PlanRole.Always).ToList();
        if (onlyCapability is not null && items.All(i => i.Role == PlanRole.Always)) items.Clear();
        if (items.Count == 0) { Mode = PanelMode.Ready; Recompute(); return; }

        var plan = _plan;
        var selection = _selection;
        StartInstalling(items);
        await Hub.InstallItemsAsync(items, NameOf, OnEvent, _cts.Token);
        Hub.RecordTrack(Track.Slug, plan, selection);
        FinishInstalling(items);
    }

    internal void StartInstalling(IReadOnlyList<PlanItem> items)
    {
        InstallLines.Clear();
        Failures.Clear();
        foreach (var i in items) InstallLines.Add(new InstallLineModel(i.Id, NameOf(i.Id), i.Version, i.Size));
        Stopped = false;
        Progress = 0;
        ProgressText = $"0 of {items.Count}";
        StatusLine = Hub.TargetNeedsAdmin ? "Waiting for Windows permission…" : "";
        Mode = PanelMode.Installing;
    }

    /// <summary>The bar moves by bytes: finished components plus the part of the current one (4.5).</summary>
    internal void OnEvent(InstallEvent e)
    {
        var line = InstallLines.FirstOrDefault(l => string.Equals(l.Id, e.Id, StringComparison.OrdinalIgnoreCase));
        if (line is null) return;
        switch (e.Kind)
        {
            case InstallEventKind.Waiting: StatusLine = "Waiting for Windows permission…"; break;
            case InstallEventKind.Started: line.Running = true; StatusLine = $"Downloading {line.Id} {line.Version}"; break;
            case InstallEventKind.Progress: line.Fraction = e.Fraction; line.Running = true; break;
            case InstallEventKind.Done: line.Running = false; line.Done = true; line.Fraction = 1; break;
            case InstallEventKind.Failed:
            case InstallEventKind.Declined:
                line.Running = false; line.Failed = true;
                if (e.Kind == InstallEventKind.Declined) PanelMessage = e.Message ?? "";
                else Failures.Add(new FailureModel(line.Name, e.Message ?? "", this));
                OnPropertyChanged(nameof(HasFailures));
                break;
        }
        var total = InstallLines.Sum(l => Math.Max(1, l.Size));
        var done = InstallLines.Sum(l => Math.Max(1, l.Size) * (l.Done ? 1 : l.Fraction));
        Progress = 100.0 * done / total;
        ProgressText = $"{InstallLines.Count(l => l.Done)} of {InstallLines.Count}";
    }

    /// <summary>The done state, for the screenshot harness: it feeds the events an install would send.</summary>
    internal void ShowDone(IReadOnlyList<PlanItem> items) => FinishInstalling(items);

    private void FinishInstalling(IReadOnlyList<PlanItem> items)
    {
        var done = InstallLines.Count(l => l.Done);
        var declined = InstallLines.Count > 0 && InstallLines.All(l => l.Failed) && PanelMessage.Length > 0;
        if (declined) { Mode = PanelMode.Ready; Recompute(); return; }

        Stopped = _cts?.IsCancellationRequested == true && done < InstallLines.Count;
        DoneLabel = Stopped ? "STOPPED" : "INSTALLED";
        if (Stopped)
        {
            DoneTitle = $"Stopped. {done} of {InstallLines.Count} components are installed.";
            DoneLine = "";
        }
        else if (done == InstallLines.Count)
        {
            DoneTitle = $"{Track.Name} is on this computer.";
            DoneLine = $"{Words.Count(done, "component")} · {Words.Size(items.Sum(i => i.Size))}";
        }
        else
        {
            var failed = InstallLines.Where(l => l.Failed).Select(l => l.Name).ToList();
            DoneTitle = failed.Count == 1 ? $"{Track.Name} is on this computer, except {failed[0]}." : $"{Track.Name} is partly on this computer.";
            DoneLine = $"{done} of {InstallLines.Count} components";
        }
        Mode = PanelMode.Done;
        Refresh();
    }

    /// <summary>A component by its plain name (4.5): the tool, "{vendor} engine", "Agent access for …", "Kovati Hub link".</summary>
    internal string NameOf(string id)
    {
        var cat = Hub.Catalogue;
        var item = _plan?.Items.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase));
        var product = item?.Product is { } slug ? cat?.Product(slug) : cat?.ProductOfComponent(id);
        return item?.Role switch
        {
            PlanRole.Always => cat?.FoundationName(id) ?? cat?.ProductOfComponent(id)?.Name ?? id,
            PlanRole.Agent => $"Agent access for {product?.Name ?? id}",
            PlanRole.Engine => $"{(product is null || cat is null ? id : cat.ShortName(product))} engine",
            PlanRole.Dependency => cat?.ProductOfComponent(id)?.Name ?? id,
            _ => product?.Name ?? id,
        };
    }

    // ── recomputing ──────────────────────────────────────────────────────

    public override void Refresh()
    {
        if (Hub.Catalogue is not { } cat) return;
        var inputs = Hub.Inputs();
        _refreshing = true;
        AgentsOn = Hub.HubState.AgentsOn;
        _refreshing = false;

        if (inputs is not null && !_selectionSet)
        {
            _selection = RememberedOrDefault(inputs);
            _selectionSet = true;
        }

        Rows.Clear();
        int ready = 0, paid = 0, coming = 0, labs = 0, notBuilt = 0;
        foreach (var cap in Track.Capabilities)
        {
            var product = cat.Product(cap.Product);
            var status = product is null || inputs is null ? ProductStatus.Coming : TrackPlanner.StatusOf(product, inputs);
            switch (status)
            {
                case ProductStatus.Coming when product is not null && inputs is not null && TrackPlanner.AvailabilityOf(product, inputs) == BuildAvailability.NotBuiltForEngine: notBuilt++; break;
                case ProductStatus.Coming: coming++; break;
                case ProductStatus.Labs: labs++; break;
                case ProductStatus.NeedsPurchase: paid++; break;
                default: ready++; break;
            }
            if (status == ProductStatus.Labs && !Hub.HubState.ShowLabs) continue;
            Rows.Add(new CapabilityRowModel(this, cap, product, status, inputs));
        }

        var parts = new List<string> { $"{ready} ready to install" };
        if (paid > 0) parts.Add($"{paid} paid");
        if (notBuilt > 0) parts.Add($"{notBuilt} not built for Unreal Engine {Hub.EngineVersion} yet");
        if (coming > 0) parts.Add($"{coming} coming soon");
        if (labs > 0) parts.Add($"{labs} in Labs");
        CountLine = string.Join(" · ", parts);
        HasHiddenLabs = labs > 0 && !Hub.HubState.ShowLabs;

        OnPropertyChanged(nameof(NoEngine));
        OnPropertyChanged(nameof(HasManyEngines));
        OnPropertyChanged(nameof(TargetLabel));
        OnPropertyChanged(nameof(PaymentsLive));
        Recompute();
        _ = RefreshChecklistAsync(openIfDue: true);
    }

    private TrackSelection RememberedOrDefault(PlanInputs inputs)
    {
        var fallback = TrackPlanner.DefaultSelection(Track, inputs);
        if (Hub.SelectedEngine is null || Hub.HubState.Track(Track.Slug, Hub.TargetRoot) is not { } record) return fallback;
        // What was chosen last time on this target, plus anything the person can now have that is new.
        var engines = fallback.Engines.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var (cap, list) in record.Engines) engines[cap] = list;
        return new TrackSelection(record.Capabilities.Union(fallback.Capabilities, StringComparer.OrdinalIgnoreCase).ToList(), engines);
    }

    public bool IsTicked(string capability) => _selection.HasCapability(capability);
    public bool IsEngineTicked(string capability, string engine) => _selection.HasEngine(capability, engine);

    internal void SetTicked(string capability, bool on)
    {
        if (_selection.HasCapability(capability) == on) return;
        _selection = _selection.With(capability, on);
        Recompute();
    }

    internal void SetEngineTicked(string capability, string engine, bool on)
    {
        if (_selection.HasEngine(capability, engine) == on) return;
        _selection = _selection.WithEngine(capability, engine, on);
        Recompute();
    }

    /// <summary>The summary changes the moment a tick changes (4.3).</summary>
    private void Recompute()
    {
        var inputs = Hub.Inputs();
        Notes.Clear();
        LaterLines.Clear();
        ComponentGroups.Clear();
        NotIncluded.Clear();
        foreach (var r in Rows) r.RefreshFromPage();

        if (inputs is null || Hub.Catalogue is not { } cat)
        {
            Summary = "Reading the list of tools…";
            SizeLine = "";
            ButtonText = $"Install {Track.Name}";
            ButtonEnabled = false;
            return;
        }

        _plan = TrackPlanner.Plan(Track, _selection, inputs);
        _planWithAgents = inputs.AgentAccess ? _plan : TrackPlanner.Plan(Track, _selection, inputs with { AgentAccess = true });
        var plan = _plan;

        Summary = plan.Summary;
        SizeLine = $"{Words.Size(plan.Size)} · {cat.PriceLabel("free")}";
        AlreadyHereLine = plan.AlreadyHere > 0 ? $"{plan.AlreadyHere} already here" : "";
        OnPropertyChanged(nameof(HasAlreadyHere));

        var more = _planWithAgents.ComponentCount - (inputs.AgentAccess ? _planWithAgents.ComponentCount : plan.ComponentCount);
        AgentsLine = AgentsOn
            ? "Agent access is on for everything you install. Change it in Settings."
            : $"Adds agent access, so Claude, Cursor or another agent can work these tools inside Unreal. {Words.Count(more, "more component")}.";

        // The button and its notes (the table of 4.3).
        var anyInstalledTicked = Rows.Any(r => r.IsInstalled && IsTicked(r.Capability.Id));
        ButtonShowsTick = false;
        if (!Hub.HasEngine)
        {
            ButtonText = $"Unreal Engine {Hub.EngineVersion} not found";
            ButtonEnabled = false;
        }
        else if (Track.Capabilities.All(c => cat.Product(c.Product) is not { } p || TrackPlanner.StatusOf(p, inputs) == ProductStatus.Coming)
                 && inputs.Manifest.BuiltFor(Hub.EngineVersion, Hub.Channel) == 0)
        {
            ButtonText = $"No builds for Unreal Engine {Hub.EngineVersion} yet";
            ButtonEnabled = false;
            var built = inputs.Manifest.EnginesWithBuilds(Hub.Channel);
            Notes.Add(built.Count == 0 ? "Nothing is built yet." : $"Kovati's tools are built for Unreal Engine {string.Join(", ", built)} today.");
        }
        else if (plan.CapabilityCount == 0)
        {
            ButtonText = "Nothing to install";
            ButtonEnabled = false;
        }
        else if (plan.ComponentCount == 0)
        {
            ButtonText = "Installed";
            ButtonEnabled = false;
            ButtonShowsTick = true;
        }
        else
        {
            ButtonEnabled = true;
            ButtonText = !Hub.IsSignedIn ? "Sign in and install"
                : anyInstalledTicked ? $"Install {Words.Count(plan.NewCapabilityCount, "more capability", "more capabilities")}"
                : $"Install {Track.Name}";
            if (!Hub.IsSignedIn) Notes.Add("Free. Your tools are added to your Kovati account, so they follow you to any computer.");
            if (Hub.TargetProject is { } project)
            {
                Notes.Add($"Installs into {project.Name} only, in its Plugins\\{Core.Paths.PluginFolder} folder. What it has from Unreal Engine {Hub.EngineVersion} stays there.");
                if (project.IsCodeProject) Notes.Add($"{project.Name} is a C++ project: rebuild it in your IDE before opening it.");
            }
            if (Hub.TargetNeedsAdmin) Notes.Add("Windows will ask for permission once: this engine is in Program Files.");
            if (Notes.Count < 2 && Hub.EditorRunningOnTarget()) Notes.Add("Unreal Editor is open. The new tools appear after you restart it.");
        }

        // What engines will download later, beside the size it would otherwise misstate.
        foreach (var cap in Track.Capabilities.Where(c => IsTicked(c.Id)))
            foreach (var e in cap.Engines.Where(e => IsEngineTicked(cap.Id, e.Product)))
                if (cat.Product(e.Product) is { Later: { Length: > 0 } later } ep && plan.Items.Any(i => i.Product == ep.Slug && i.Role == PlanRole.Engine && i.Action != PlanAction.Current))
                    LaterLines.Add($"{cat.ShortName(ep)}: {later}");

        BuildComponentGroups(cat, inputs);

        foreach (var r in Rows.Where(r => r.Status == ProductStatus.NeedsPurchase && r.Product is not null))
            NotIncluded.Add(new NotIncludedModel(this, r.Capability.Name, r.Product!, Hub.PriceOf(r.Product!), cat.PriceLabel(r.PriceKey)));
        OnPropertyChanged(nameof(HasNotIncluded));
        OnPropertyChanged(nameof(HasManyNotIncluded));
        OnPropertyChanged(nameof(AddAllLabel));
    }

    /// <summary>"Show components" (4.4): grouped by capability, the editor plugin under ALWAYS, agent access listed even when off.</summary>
    private void BuildComponentGroups(Catalogue cat, PlanInputs inputs)
    {
        if (_plan is null || _planWithAgents is null) return;
        foreach (var cap in Track.Capabilities)
        {
            var items = _plan.Items.Where(i => i.Capability == cap.Id && i.Role != PlanRole.Agent).ToList();
            if (items.Count == 0) continue;
            ComponentGroupModel group = new(cap.Name.ToUpperInvariant(), false);
            foreach (var i in items) group.Items.Add(ComponentLineModel.From(i, i.Role == PlanRole.Dependency ? $"needed by {i.NeededBy}" : null, muted: i.Action == PlanAction.Current));
            ComponentGroups.Add(group);
        }
        var always = _plan.Items.Where(i => i.Role == PlanRole.Always).ToList();
        if (always.Count > 0)
        {
            // The Kovati foundation: what every engine install carries, each with its one line from the catalogue.
            ComponentGroupModel group = new("ALWAYS · KOVATI FOUNDATION", false);
            foreach (var i in always)
                group.Items.Add(ComponentLineModel.From(i,
                    cat.FoundationOf(i.Id) is { } a ? (string.Equals(a.Plugin, i.Id, StringComparison.OrdinalIgnoreCase) ? a.Line : $"Agent access for {a.Name}")
                    : i.NeededBy is { } by ? $"needed by {by}" : null,
                    muted: i.Action == PlanAction.Current));
            ComponentGroups.Add(group);
        }
        var agents = _planWithAgents.Items.Where(i => i.Role == PlanRole.Agent).ToList();
        if (agents.Count > 0)
        {
            ComponentGroupModel group = new(AgentsOn ? "AGENT ACCESS" : "AGENT ACCESS (OFF)", !AgentsOn);
            foreach (var i in agents) group.Items.Add(ComponentLineModel.From(i, null, muted: !AgentsOn || i.Action == PlanAction.Current));
            ComponentGroups.Add(group);
        }
    }
}

/// <summary>One capability row (4.3): tick, picture, name, product, line, chips, and the right side by state.</summary>
public sealed partial class CapabilityRowModel : ObservableObject
{
    private readonly TrackPageViewModel _page;
    private bool _ignore;

    public CapabilityRowModel(TrackPageViewModel page, Capability capability, Product? product, ProductStatus status, PlanInputs? inputs)
    {
        _page = page;
        Capability = capability;
        Product = product;
        Status = status;
        var hub = page.Hub;
        var cat = hub.Catalogue!;
        PriceKey = product is null || inputs is null ? product?.Price ?? "free" : ProductFacts.PriceKey(product, inputs);
        var owned = product is not null && inputs is not null && ProductFacts.Owned(product, inputs);
        // A paid tool this account has as its free tier installs with the rest, marked LITE (beta 7).
        Tier = product is null || inputs is null || owned ? null : ProductFacts.FreeTierOf(product, inputs);
        var stage = product is null || inputs is null ? Stage.Coming : TrackPlanner.StageOf(product, inputs);

        // Chips: price (or Owned), stage. Released shows none on a row.
        Chips.Add(owned ? new ChipModel("OWNED", "owned")
            : Tier is not null ? new ChipModel(FreeTiers.Word(Tier).ToUpperInvariant(), "free")
            : ChipModel.Price(cat, PriceKey));
        if (product?.Trial == true) Chips.Add(new ChipModel(cat.Labels.Trial.ToUpperInvariant(), "free"));
        var notBuilt = status == ProductStatus.Coming && product is not null && inputs is not null
                       && TrackPlanner.AvailabilityOf(product, inputs) == BuildAvailability.NotBuiltForEngine;
        if (notBuilt) Chips.Add(new ChipModel($"NOT BUILT FOR {hub.EngineVersion}", "soon"));
        else if (status == ProductStatus.Coming) Chips.Add(new ChipModel(cat.StageLabel(Stage.Coming).ToUpperInvariant(), "soon"));
        else if (ChipModel.Stage(cat, stage) is { } chip) Chips.Add(chip);

        var price = product is null ? null : hub.PriceOf(product);
        ProductName = product is null ? "" : status == ProductStatus.NeedsPurchase && price is not null ? $"{product.Name} · {price}" : product.Name;

        IsInstalled = status is ProductStatus.Installed or ProductStatus.Update;
        HasBox = status is ProductStatus.Available || (status == ProductStatus.Labs && hub.HubState.ShowLabs);
        IsComing = status == ProductStatus.Coming;
        NeedsPurchase = status == ProductStatus.NeedsPurchase;
        if (IsInstalled && product?.MainComponent is { } main && inputs is not null && !inputs.Installed.ContainsKey(main) && inputs.ProvidedElsewhere.TryGetValue(main, out var there))
            InstalledText = $"Here · {there} · from the engine";
        else if (IsInstalled && product?.MainComponent is { } main2 && inputs is not null && inputs.Installed.TryGetValue(main2, out var have))
        {
            InstalledText = $"Installed · {have}";
            var newer = product.Plugins.Concat(product.AgentPlugins).Select(id => TrackPlanner.NewerVersion(id, inputs)).FirstOrDefault(v => v is not null);
            UpdateText = status == ProductStatus.Update ? $"Update to {TrackPlanner.NewerVersion(main2, inputs) ?? newer}" : "";
        }

        if (product is not null && (inputs is not null ? ProductFacts.FreeTierPromised(product, inputs) : ProductFacts.FreeTierPromised(product, new Core.Manifest.Manifest())))
            ExtraLine = "A free tier is planned. Today this is the full version.";
        else if (status == ProductStatus.Labs)
            ExtraLine = cat.LabsLine;
        else if (notBuilt)
            ExtraLine = TrackPlanner.NotBuiltLine(hub.EngineVersion);

        CartLabel = hub.PaymentsLive ? "Add to cart" : "Buying opens soon";
        CartNote = hub.PaymentsLive ? "Opens your Kovati account in the browser. It appears here as soon as it is yours." : "You can read about it on kovati.dev.";

        if (capability.Engines.Count > 0 && inputs is not null)
        {
            EngineLabel = (capability.EngineLabel ?? "Engines").ToUpperInvariant();
            foreach (var e in capability.Engines)
            {
                var ep = cat.Product(e.Product);
                if (ep is null) continue;
                var labs = TrackPlanner.IsLabsEngine(e, inputs);
                if (labs && !hub.HubState.ShowLabs) continue;
                var est = TrackPlanner.StatusOf(ep, inputs);
                var installed = est is ProductStatus.Installed or ProductStatus.Update;
                Engines.Add(new EngineLineModel(this, e.Product, ep.Runs ?? ep.Name, ep.Vendor ?? "", labs, installed,
                    est is ProductStatus.Available or ProductStatus.Installed or ProductStatus.Update || (labs && est == ProductStatus.Labs)));
            }
        }
        RefreshFromPage();
    }

    public TrackPageViewModel Page => _page;
    public Capability Capability { get; }
    public Product? Product { get; }
    public ProductStatus Status { get; }
    public string PriceKey { get; }
    /// <summary>"lite" or "trial" when the product goes in as its free tier.</summary>
    public string? Tier { get; }
    public string Name => Capability.Name;
    public string Line => Capability.Line;
    public string? Poster => Capability.Media.Poster;
    public string ProductName { get; }
    public ObservableCollection<ChipModel> Chips { get; } = [];
    public ObservableCollection<EngineLineModel> Engines { get; } = [];
    public string EngineLabel { get; } = "";
    public bool HasEngines => Engines.Count > 0;

    public bool HasBox { get; }
    public bool IsInstalled { get; }
    public bool IsComing { get; }
    public bool NeedsPurchase { get; }
    public string InstalledText { get; } = "";
    public string UpdateText { get; } = "";
    public bool HasUpdate => UpdateText.Length > 0;
    public bool ShowInstalledText => IsInstalled && !HasUpdate;
    public string ExtraLine { get; } = "";
    public bool HasExtraLine => ExtraLine.Length > 0;
    public string CartLabel { get; }
    public string CartNote { get; }
    public bool CanBuy => NeedsPurchase && Page.Hub.PaymentsLive;
    public string SiteLabel => "See it on kovati.dev ↗";

    [ObservableProperty] private bool _isTicked;
    [ObservableProperty] private bool _locked;
    [ObservableProperty] private bool _showEngineWarning;
    [ObservableProperty] private string _engineWarning = "";

    public bool CanToggle => HasBox && !Locked;
    partial void OnLockedChanged(bool value) { OnPropertyChanged(nameof(CanToggle)); foreach (var e in Engines) e.Refresh(); }

    partial void OnIsTickedChanged(bool value)
    {
        if (_ignore) return;
        _page.SetTicked(Capability.Id, value);
    }

    internal void RefreshFromPage()
    {
        _ignore = true;
        IsTicked = _page.IsTicked(Capability.Id) && (HasBox || IsInstalled);
        _ignore = false;
        foreach (var e in Engines) e.Refresh();
        var anyEngine = Engines.Any(e => e.IsTicked);
        ShowEngineWarning = Capability.EngineRequired && IsTicked && Engines.Count > 0 && !anyEngine;
        EngineWarning = Product is null ? "" : $"{Product.Name} has no engine ticked. It will have nothing to generate with.";
    }

    [RelayCommand] private void AddToCart() { if (Product is not null && Page.Hub.PaymentsLive) Page.Hub.AddToCart(Product); }
    [RelayCommand] private void OpenSite() => Page.Hub.OpenUrl(Page.Hub.SiteUrl(Product?.Site ?? Page.Track.Site));
    [RelayCommand] private Task Update() => Page.InstallAsync(Capability.Id);
    [RelayCommand] private void Details() { if (Product is not null) Page.Hub.OpenProduct(Product.Slug); }
    public bool HasDetails => Product is not null && !IsComing;
}

/// <summary>One engine inside a capability: where it runs and what it costs, then whose it is.</summary>
public sealed partial class EngineLineModel(CapabilityRowModel row, string slug, string runs, string vendor, bool labs, bool installed, bool available) : ObservableObject
{
    private bool _ignore;
    public string Slug { get; } = slug;
    public string Runs { get; } = runs;
    public string Vendor { get; } = vendor;
    public bool IsLabs { get; } = labs;
    public bool IsInstalledEngine { get; } = installed;
    public ChipModel LabsChip => new("LABS", "labs");

    [ObservableProperty] private bool _isTicked;
    [ObservableProperty] private bool _canToggle;

    partial void OnIsTickedChanged(bool value)
    {
        if (_ignore) return;
        row.Page.SetEngineTicked(row.Capability.Id, Slug, value);
    }

    internal void Refresh()
    {
        _ignore = true;
        IsTicked = row.Page.IsEngineTicked(row.Capability.Id, Slug) && available;
        CanToggle = available && row.IsTicked && !row.Locked && (row.HasBox || row.IsInstalled);
        _ignore = false;
    }
}

public sealed class ComponentGroupModel(string label, bool muted)
{
    public string Label { get; } = label;
    public bool Muted { get; } = muted;
    public ObservableCollection<ComponentLineModel> Items { get; } = [];
}

public sealed record ComponentLineModel(string Id, string Version, string? Note, bool Muted)
{
    public bool HasNote => !string.IsNullOrEmpty(Note);

    public static ComponentLineModel From(PlanItem i, string? note, bool muted) =>
        new(i.Id, i.Action == PlanAction.Current ? "here" : i.Version,
            i.IsFreeTier ? (note is { Length: > 0 } n ? $"{n} · {FreeTiers.Word(i.FreeTier)}" : FreeTiers.Word(i.FreeTier)) : note, muted);
}

/// <summary>"Capture · Paid · $79.99" with "Add to cart" (4.3, NOT INCLUDED).</summary>
public sealed partial class NotIncludedModel(TrackPageViewModel page, string capability, Product product, string? price, string priceWord)
{
    public string Line { get; } = price is null ? $"{capability} · {priceWord}" : $"{capability} · {priceWord} · {price}";
    public long Cents { get; } = page.Hub.AccountCatalogue?.Plugin(product.StoreKey)?.Price?.Amount ?? 0;
    public string CartLabel => page.Hub.PaymentsLive ? "Add to cart" : "Buying opens soon";
    public bool CanBuy => page.Hub.PaymentsLive;

    [RelayCommand] private void AddToCart() { if (page.Hub.PaymentsLive) page.Hub.AddToCart(product); }
}

/// <summary>One line of the install progress (4.5).</summary>
public sealed partial class InstallLineModel(string id, string name, string version, long size) : ObservableObject
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Version { get; } = version;
    public long Size { get; } = size;

    [ObservableProperty] private bool _running;
    [ObservableProperty] private bool _done;
    [ObservableProperty] private bool _failed;
    [ObservableProperty] private double _fraction;

    public string PercentText => Running && !Done && Fraction > 0 ? $"{(int)(Fraction * 100)} %" : "";
    public bool IsPending => !Done && !Failed;
    partial void OnFractionChanged(double value) => OnPropertyChanged(nameof(PercentText));
    partial void OnRunningChanged(bool value) => OnPropertyChanged(nameof(PercentText));
    partial void OnDoneChanged(bool value) => OnPropertyChanged(nameof(IsPending));
    partial void OnFailedChanged(bool value) => OnPropertyChanged(nameof(IsPending));
}

/// <summary>"MontageForge: Unreal Editor has it open. Close Unreal and try again." with "Try again".</summary>
public sealed partial class FailureModel(string name, string message, TrackPageViewModel page)
{
    public string Name { get; } = name;
    public string Message { get; } = message;

    [RelayCommand] private Task TryAgain() => page.InstallCommand.ExecuteAsync(null);
}
