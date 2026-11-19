using CommunityToolkit.Mvvm.Input;
using Forge.Core;
using Forge.Core.Catalogue;

namespace Forge.Hub.ViewModels.Pages;

/// <summary>Where one product stands for the person and this target, decided once for posters, cards and the product page.</summary>
public sealed class ProductState
{
    public required Product Product { get; init; }
    public required ProductStatus Status { get; init; }
    public required Stage Stage { get; init; }
    public required string PriceKey { get; init; }
    public bool Owned { get; init; }
    public string? InstalledVersion { get; init; }
    public string? LatestVersion { get; init; }
    public bool HasUpdate { get; init; }
    public string? Price { get; init; }
    public bool PaymentsLive { get; init; }
    public bool SignedIn { get; init; }

    public bool IsInstalled => InstalledVersion is not null;
    public bool IsComing => Status == ProductStatus.Coming;
    public bool IsLabs => Stage == Stage.Labs;
    public bool NeedsPurchase => Status == ProductStatus.NeedsPurchase;

    public static ProductState Of(MainViewModel hub, Product p)
    {
        var inputs = hub.Inputs();
        if (inputs is null)
            return new ProductState { Product = p, Status = ProductStatus.Coming, Stage = Stage.Coming, PriceKey = p.Price };
        var status = TrackPlanner.StatusOf(p, inputs);
        var main = p.MainComponent;
        var installed = main is not null && inputs.Installed.TryGetValue(main, out var v) ? v : null;
        var latest = main is null ? null : inputs.Manifest.Plugin(main)?.Latest(inputs.Engine, inputs.Channel)?.Version;
        var owned = p.Plugins.Count > 0 && p.Plugins.Any(id => inputs.Manifest.Plugin(id)?.IsPaid == true)
                    && p.Plugins.All(id => inputs.Owned.Contains(id) || inputs.Manifest.Plugin(id)?.IsPaid != true);
        return new ProductState
        {
            Product = p,
            Status = status,
            Stage = TrackPlanner.StageOf(p, inputs),
            PriceKey = ProductFacts.PriceKey(p, inputs.Manifest),
            Owned = owned,
            InstalledVersion = installed,
            LatestVersion = latest,
            HasUpdate = TrackPlanner.HasUpdate(p, inputs),
            Price = hub.PriceOf(p),
            PaymentsLive = hub.PaymentsLive,
            SignedIn = hub.IsSignedIn,
        };
    }

    /// <summary>Price, trial, stage: three chips at most (9.1). Released shows none on cards and rows.</summary>
    public List<ChipModel> Chips(Catalogue cat, bool onPicture, bool showReleased = false)
    {
        var chips = new List<ChipModel> { Owned ? new ChipModel("OWNED", "owned", onPicture) : ChipModel.Price(cat, PriceKey, onPicture) };
        if (Product.Trial) chips.Add(new ChipModel(cat.Labels.Trial.ToUpperInvariant(), "free", onPicture));
        if (IsComing) chips.Add(new ChipModel(cat.StageLabel(Stage.Coming).ToUpperInvariant(), "soon", onPicture));
        else if (ChipModel.Stage(cat, Stage, onPicture, showReleased) is { } s) chips.Add(s);
        return chips;
    }

    /// <summary>The main button's words (4.7, 4.10) and whether it can be pressed.</summary>
    public (string Text, bool Enabled, bool Visible) Button(bool productPage)
    {
        if (IsComing) return productPage ? ("See it on kovati.dev ↗", true, true) : ("", false, false);
        if (NeedsPurchase) return PaymentsLive ? ("Add to cart", true, true) : ("Buying opens soon", false, true);
        if (IsInstalled) return HasUpdate ? ($"Update to {LatestVersion}", true, true) : ("Installed", false, true);
        if (!SignedIn && productPage) return ("Sign in to install", true, true);
        if (IsLabs) return ("Install the Labs build", true, true);
        return ("Install", true, true);
    }
}

/// <summary>A product poster (11.4): 264 x 352, the picture or the typographic poster, chips, track and stage, name, line, a button.</summary>
public sealed partial class PosterModel
{
    private readonly MainViewModel _hub;

    public PosterModel(MainViewModel hub, Product product)
    {
        _hub = hub;
        Product = product;
        var cat = hub.Catalogue!;
        State = ProductState.Of(hub, product);
        var chips = State.Chips(cat, onPicture: true);
        PriceChip = chips.FirstOrDefault();
        StageChip = chips.Skip(1).LastOrDefault();
        TrackLine = (cat.Track(product.Track)?.Name ?? "Studio").ToUpperInvariant();
        (ButtonText, ButtonEnabled, HasButton) = State.Button(productPage: false);
        TypeLabel = product.Type.ToUpperInvariant();
        SpineText = product.Type == "Playbook" && product.Software.Count > 0 ? $"PLAYBOOK — WITH {string.Join(", ", product.Software).ToUpperInvariant()}" : "PLAYBOOK";
    }

    public Product Product { get; }
    public ProductState State { get; }
    public string Name => Product.Name;
    public string Line => Product.Line;
    public string Slug => Product.Slug;
    public string Mark => Product.Mark ?? Product.Name;
    public string? Picture => Product.Media.Card ?? Product.Media.Hero;
    public bool HasPicture => MediaFound(Picture);
    public string TrackLine { get; }
    public string TypeLabel { get; }
    public ChipModel? PriceChip { get; }
    public ChipModel? StageChip { get; }
    public bool HasStageChip => StageChip is not null;
    public string ButtonText { get; }
    public bool ButtonEnabled { get; }
    public bool HasButton { get; }
    public bool ShowDetailsOnly => !HasButton;
    public bool IsInstalled => State.IsInstalled;
    public bool IsKit => Product.Type == "Kit";
    public bool IsPlaybook => Product.Type == "Playbook";
    public bool IsUtility => Product.Type == "Utility";
    public string SpineText { get; }

    private static bool MediaFound(string? path) => Controls.MediaImage.Resolve(path) is not null;

    [RelayCommand] private void Open() => _hub.OpenProduct(Product.Slug);

    [RelayCommand]
    private Task Act()
    {
        if (State.NeedsPurchase) { if (State.PaymentsLive) _hub.AddToCart(Product); return Task.CompletedTask; }
        if (State.IsInstalled && State.HasUpdate) return _hub.UpdateProductAsync(Product);
        return _hub.InstallProductAsync(Product);
    }
}

/// <summary>A Forge card in the Browse rail (4.10): 210 x 262, chips, "03/08", the tagline in amber, the name, the line on hover.</summary>
public sealed partial class ForgeCardModel(MainViewModel hub, Product product, int index, int count)
{
    private readonly ProductState _state = ProductState.Of(hub, product);
    public Product Product { get; } = product;
    public string Name => Product.Name;
    public string Tagline => (Product.Tagline ?? "").ToUpperInvariant();
    public string Line => Product.Line;
    public string Number { get; } = $"{index:00}/{count:00}";
    public string? Picture => Product.Media.Card ?? Product.Media.Hero;
    public bool IsInstalled => _state.IsInstalled;
    public IReadOnlyList<ChipModel> Chips => _state.Chips(hub.Catalogue!, onPicture: true);
    public string SeeLine => $"See {Product.Name} →";

    [RelayCommand] private void Open() => hub.OpenProduct(Product.Slug);
}
