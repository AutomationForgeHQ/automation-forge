using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Forge.Core;
using Forge.Core.Catalogue;
using Forge.Core.Manifest;

namespace Forge.Hub.ViewModels.Pages;

/// <summary>
/// A product page (4.7): one template with optional blocks, as on the site. Every block is shown only when the
/// catalogue or the manifest has something for it, so no page carries an empty block.
/// </summary>
public sealed partial class ProductPageViewModel : PageViewModel
{
    public ProductPageViewModel(MainViewModel hub, Product product) : base(hub)
    {
        Product = product;
        Refresh();
    }

    public Product Product { get; }
    public override string Title => Product.Name;
    private Catalogue Cat => Hub.Catalogue!;

    [ObservableProperty] private ProductState? _state;
    [ObservableProperty] private string _buttonText = "";
    [ObservableProperty] private bool _buttonEnabled;
    [ObservableProperty] private string _buttonLine = "";
    [ObservableProperty] private string _onThisComputer = "";
    [ObservableProperty] private bool _showRemoveDialog;
    [ObservableProperty] private string _removeTitle = "";
    [ObservableProperty] private string _removeLine = "";
    [ObservableProperty] private bool _showViewer;
    [ObservableProperty] private int _viewerIndex;
    [ObservableProperty] private bool _earlierOpen;

    public ObservableCollection<ChipModel> Chips { get; } = [];
    public ObservableCollection<EnginePanelModel> Engines { get; } = [];
    public ObservableCollection<PosterModel> Extensions { get; } = [];
    public ObservableCollection<ShotModel> Shots { get; } = [];
    public ObservableCollection<ChangelogEntry> Earlier { get; } = [];

    // ── identity ─────────────────────────────────────────────────────────

    public string TypeLine => Product.Type == "Add-on" && Cat.Product(Product.Extends) is { } parent
        ? $"ADD-ON · FOR {parent.Name.ToUpperInvariant()}"
        : Product.Type == "Add-on" && Product.Extends is { Length: > 0 } ext ? $"ADD-ON · FOR {ext.ToUpperInvariant()}"
        : Product.Type.ToUpperInvariant();
    public string TrackName => (Cat.Track(Product.Track)?.Name ?? "Studio").ToUpperInvariant();
    public bool HasTrack => Cat.Track(Product.Track) is not null;
    public string Name => Product.Name;
    public string VersionLine => State?.LatestVersion is { } v ? $"v{v}" : "";
    public string? Picture => Product.Media.Hero ?? Product.Media.Card;
    public bool HasPicture => Controls.MediaImage.Available(Picture);
    public string Mark => Product.Mark ?? Product.Name;
    public string Slug => Product.Slug;
    public bool HasFilm => Product.Page?.Film is not null || Cat.Track(Product.Track)?.Film is not null;
    public bool IsLabs => State?.IsLabs == true;
    public string LabsLine => Cat.LabsLine;

    public string Promise => Product.Page?.Promise ?? "";
    public bool HasPromise => Promise.Length > 0;
    public string Subline => Product.Page?.Subline ?? Product.Line;
    public bool HasSite => !string.IsNullOrEmpty(Product.Site);
    public bool HasDocs => !string.IsNullOrEmpty(Product.Docs);

    // ── blocks ───────────────────────────────────────────────────────────

    public IReadOnlyList<TitleLine> Adds => Product.Page?.Adds.Take(6).ToList() ?? [];
    public bool HasAdds => Adds.Count > 0;
    public bool HasMoreAdds => Product.Page?.Adds.Count > 6;
    public PageInside? Inside => Product.Page?.Inside;
    public bool HasInside => Inside is { Items.Count: > 0 };
    public IReadOnlyList<NameLine> Takes => Product.Page?.Takes ?? [];
    public bool HasTakes => Takes.Count > 0;
    public string PriceLine => Product.Page?.PriceLine ?? "";
    public bool HasPriceLine => PriceLine.Length > 0;
    public IReadOnlyList<EditionModel> Editions => Product.Editions.Select(e => new EditionModel(e, ChipModel.Price(Cat, e.Price))).ToList();
    public bool HasEditions => Product.Editions.Count > 0;
    public bool HasEngines => Engines.Count > 0;
    public IReadOnlyList<NameLine> Characters => Product.Characters;
    public bool HasCharacters => Product.Characters.Count > 0;
    public bool HasExtensions => Extensions.Count > 0;
    public string Contents => string.Join(" · ", Product.Contents);
    public bool HasContents => Product.Contents.Count > 0;
    public string Software => string.Join(", ", Product.Software);
    public bool HasSoftware => Product.Software.Count > 0;
    public bool HasAgents => Product.AgentPlugins.Count > 0;
    public string AgentsLine => Hub.HubState.AgentsOn
        ? $"Agent access is on. An agent can work {Product.Name} inside Unreal."
        : $"Agent access is off. Turn it on in Settings to let an agent work {Product.Name} inside Unreal.";
    public ChangelogEntry? Newest { get; private set; }
    public bool HasNotes => Newest is not null;
    public bool HasEarlier => Earlier.Count > 0;
    public bool HasChangelog => !string.IsNullOrEmpty(Product.Changelog);
    public bool HasShots => Shots.Count > 0;
    public string ShotsTitle => Product.Page?.Shots?.Title ?? "";
    public bool IsInstalled => State?.IsInstalled == true;
    public bool IsPinned => Product.MainComponent is { } m && Hub.HubState.Pins.ContainsKey(m);
    public string PinLabel => IsPinned ? "Unpin" : "Pin this version";
    public ShotModel? ViewerShot => ViewerIndex >= 0 && ViewerIndex < Shots.Count ? Shots[ViewerIndex] : null;
    partial void OnViewerIndexChanged(int value) => OnPropertyChanged(nameof(ViewerShot));

    public override void Refresh()
    {
        State = ProductState.Of(Hub, Product);
        Chips.Clear();
        foreach (var c in State.Chips(Cat, onPicture: false, showReleased: true)) Chips.Add(c);
        (ButtonText, ButtonEnabled, _) = State.Button(productPage: true);
        var size = Product.Plugins.Concat(DefaultEngineComponents()).Select(id => Hub.Manifest?.Plugin(id)?.Latest(Hub.EngineVersion, Hub.Channel)?.Size ?? 0).Sum();
        ButtonLine = State.NeedsPurchase
            ? (State.PaymentsLive ? "Opens your Kovati account in the browser. It appears here as soon as it is yours." : "You can read about it on kovati.dev.")
            : State.IsComing ? ""
            : $"{Cat.PriceLabel(State.PriceKey)} · {Words.Size(size)}{(DefaultEngineComponents().Any() ? " with its engines" : "")}";
        if (ProductFacts.FreeTierPromised(Product, Hub.Manifest ?? new Manifest())) ButtonLine = "A free tier is planned. Today this is the full version.";
        OnThisComputer = State.IsInstalled ? $"{State.InstalledVersion} · {Hub.EngineLabel}" + (IsPinned ? $" · Pinned at {State.InstalledVersion}" : "") : "Not installed.";

        Engines.Clear();
        foreach (var e in Cat.Products.Where(e => e.IsEngine && e.Extends == Product.Slug))
        {
            var labs = ProductState.Of(Hub, e).IsLabs;
            if (labs && !Hub.HubState.ShowLabs) continue;
            Engines.Add(new EnginePanelModel(this, e, ProductState.Of(Hub, e), labs));
        }
        Extensions.Clear();
        foreach (var x in Cat.Products.Where(x => x.IsListed && x.BelongsTo == Product.Slug && !x.IsEngine))
        {
            if (ProductState.Of(Hub, x).IsLabs && !Hub.HubState.ShowLabs) continue;
            Extensions.Add(new PosterModel(Hub, x));
        }

        Shots.Clear();
        foreach (var s in Product.Page?.Shots?.Items ?? [])
            if (Controls.MediaImage.Available(s.Src)) Shots.Add(new ShotModel(this, s, Shots.Count));

        var plugin = Product.MainComponent is { } main ? Hub.Manifest?.Plugin(main) : null;
        var entries = plugin?.Changelog ?? [];
        Newest = entries.FirstOrDefault();
        Earlier.Clear();
        foreach (var e in entries.Skip(1)) Earlier.Add(e);

        foreach (var name in new[] { nameof(VersionLine), nameof(IsLabs), nameof(HasEngines), nameof(HasExtensions), nameof(HasShots), nameof(HasNotes),
                     nameof(Newest), nameof(HasEarlier), nameof(IsInstalled), nameof(IsPinned), nameof(PinLabel), nameof(AgentsLine), nameof(HasPicture) })
            OnPropertyChanged(name);
    }

    private IEnumerable<string> DefaultEngineComponents()
    {
        var cap = Cat.Track(Product.Track)?.Capabilities.FirstOrDefault(c => c.Product == Product.Slug)
                  ?? Cat.Tracks.SelectMany(t => t.Capabilities).FirstOrDefault(c => c.Product == Product.Slug);
        if (cap is null) return [];
        return cap.Engines.Where(e => e.Default && !e.Labs).SelectMany(e => Cat.Product(e.Product)?.Plugins ?? []);
    }

    [RelayCommand]
    private Task Act()
    {
        if (State is null) return Task.CompletedTask;
        if (State.IsComing) { Hub.OpenUrl(Hub.SiteUrl(Product.Site)); return Task.CompletedTask; }
        if (State.NeedsPurchase) { if (State.PaymentsLive) Hub.AddToCart(Product); return Task.CompletedTask; }
        if (!Hub.IsSignedIn) return Hub.SignInCommand.ExecuteAsync(null);
        if (State.IsInstalled && State.HasUpdate) return Hub.UpdateProductAsync(Product);
        return Hub.InstallProductWithEnginesAsync(Product);
    }

    [RelayCommand] private void OpenSite() => Hub.OpenUrl(Hub.SiteUrl(Product.Site));
    [RelayCommand] private void OpenDocs() => Hub.OpenUrl(Product.Docs);
    [RelayCommand] private void OpenChangelog() => Hub.OpenUrl(Product.Changelog);
    [RelayCommand] private void OpenFilm() => Hub.OpenUrl(Hub.SiteUrl(Product.Site ?? Cat.Track(Product.Track)?.Site));
    [RelayCommand] private void OpenTrack() => Hub.OpenTrack(Product.Track);
    [RelayCommand] private void ToggleEarlier() => EarlierOpen = !EarlierOpen;
    [RelayCommand] private void ShowComponents() => Hub.ShowComponentsFor(Product.MainComponent ?? Product.Name);
    [RelayCommand] private void TogglePin() { if (IsPinned) Hub.Unpin(Product); else Hub.Pin(Product); Refresh(); }

    // ── the picture viewer (4.12) ────────────────────────────────────────

    internal void OpenShot(int index) { ViewerIndex = index; ShowViewer = true; }
    [RelayCommand] private void CloseViewer() => ShowViewer = false;
    [RelayCommand] private void NextShot() { if (Shots.Count > 0) ViewerIndex = (ViewerIndex + 1) % Shots.Count; }
    [RelayCommand] private void PreviousShot() { if (Shots.Count > 0) ViewerIndex = (ViewerIndex - 1 + Shots.Count) % Shots.Count; }

    // ── Remove, asked once (4.7) ─────────────────────────────────────────

    [RelayCommand]
    private void AskRemove()
    {
        var ids = Hub.RemovalSet(Product);
        RemoveTitle = $"Remove {Product.Name} from {Hub.EngineLabel}?";
        RemoveLine = ids.Count > 1
            ? $"Its engines and its agent access go with it: {Words.Count(ids.Count, "component")}. Your projects keep what you made."
            : "Your projects keep what you made.";
        ShowRemoveDialog = true;
    }

    [RelayCommand] private void Keep() => ShowRemoveDialog = false;

    [RelayCommand]
    private async Task ConfirmRemove()
    {
        ShowRemoveDialog = false;
        await Hub.RemoveProductAsync(Product);
        Refresh();
    }
}

public sealed record EditionModel(Edition Edition, ChipModel Chip)
{
    public string Name => Edition.Name;
    public string Line => Edition.Line;
}

/// <summary>"CHOOSE YOUR ENGINE": its runs, its vendor, its line, its price, and Add, Remove or Installed.</summary>
public sealed partial class EnginePanelModel(ProductPageViewModel page, Product engine, ProductState state, bool labs)
{
    public Product Engine { get; } = engine;
    public string Runs => Engine.Runs ?? Engine.Name;
    public string Vendor => Engine.Vendor ?? "";
    public string Line => Engine.Line;
    public bool IsLabs { get; } = labs;
    public ChipModel PriceChip { get; } = ChipModel.Price(page.Hub.Catalogue!, state.PriceKey);
    public ChipModel LabsChip { get; } = new("LABS", "labs");
    public string NeedsLine => Engine.Needs.Where(n => !n.Optional).Select((n, i) => i > 0 && (n.Name.StartsWith("A ") || n.Name.StartsWith("An ")) ? char.ToLowerInvariant(n.Name[0]) + n.Name[1..] : n.Name).ToList() is { Count: > 0 } needs
        ? $"Needs {string.Join(" and ", needs)}." : "";
    public bool HasNeeds => NeedsLine.Length > 0;
    public bool IsInstalled { get; } = state.IsInstalled;
    public string ActionText => IsInstalled ? "Remove" : state.IsComing ? "Coming soon" : "Add";
    public bool CanAct => !state.IsComing && !state.NeedsPurchase;

    [RelayCommand]
    private Task Act() => IsInstalled ? page.Hub.RemoveProductAsync(Engine) : page.Hub.InstallProductAsync(Engine);
}

/// <summary>A thumbnail under the product picture: the tool's own window, or what it makes.</summary>
public sealed partial class ShotModel(ProductPageViewModel page, Shot shot, int index)
{
    public string Src => shot.Src;
    public string Label => shot.Label ?? "";
    public string Caption => shot.Caption ?? "";

    [RelayCommand] private void Open() => page.OpenShot(index);
}
