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
    /// <summary>"lite" or "trial" when this account has the product as its free tier (beta 7); null otherwise.</summary>
    public string? Tier { get; init; }
    /// <summary>The name of the paid tool it comes with, when the account service says it is included with another.</summary>
    public string? IncludedWithName { get; init; }
    /// <summary>The product's free tier is live (the catalogue, or the account service's tier).</summary>
    public bool FreeTierLive { get; init; }

    /// <summary>Installs, or is installed, as Lite (or a trial): never called Owned, Bought or Licensed.</summary>
    public bool IsFreeTier => Tier is not null;
    /// <summary>"Lite" or "trial".</summary>
    public string TierWord => FreeTiers.Word(Tier);
    public bool IsIncluded => NeedsPurchase && IncludedWithName is not null;

    /// <summary>Whether the first component has a build for the target's Unreal Engine version.</summary>
    public BuildAvailability Availability { get; init; } = BuildAvailability.Built;
    /// <summary>The target's Unreal Engine version, "5.8".</summary>
    public string Engine { get; init; } = "5.8";
    /// <summary>A project target has it from its engine or a copy of its own, not from Kovati Hub's folder in it.</summary>
    public bool FromElsewhere { get; init; }

    public bool IsInstalled => InstalledVersion is not null;
    public bool IsComing => Status == ProductStatus.Coming;
    /// <summary>Released, but not for this Unreal Engine version: "Not built for Unreal Engine 5.7 yet."</summary>
    public bool IsNotBuilt => IsComing && Availability == BuildAvailability.NotBuiltForEngine;
    public string NotBuiltLine => TrackPlanner.NotBuiltLine(Engine);
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
        var elsewhere = false;
        if (installed is null && main is not null && inputs.ProvidedElsewhere.TryGetValue(main, out var ev))
        {
            elsewhere = true;
            installed = ev.Length > 0 ? ev : "here";
        }
        // The build this account may have: the newest, or a free tier's newest free-tier build.
        var latest = main is null || inputs.Manifest.Plugin(main) is not { } mp ? null
            : (FreeTiers.BuildFor(mp, inputs) ?? mp.Latest(inputs.Engine, inputs.Channel))?.Version;
        var owned = ProductFacts.Owned(p, inputs);
        var parent = ProductFacts.IncludedWith(p, inputs);
        return new ProductState
        {
            Product = p,
            Status = status,
            Stage = TrackPlanner.StageOf(p, inputs),
            PriceKey = ProductFacts.PriceKey(p, inputs),
            Owned = owned,
            Tier = owned ? null : ProductFacts.FreeTierOf(p, inputs),
            FreeTierLive = ProductFacts.FreeTierLive(p, inputs),
            IncludedWithName = parent is null ? null
                : hub.Catalogue?.ProductOfComponent(parent)?.Name ?? inputs.Offers?.Plugin(parent)?.Name ?? parent,
            InstalledVersion = installed,
            LatestVersion = latest,
            HasUpdate = TrackPlanner.HasUpdate(p, inputs),
            Price = hub.PriceOf(p),
            PaymentsLive = hub.PaymentsLive,
            SignedIn = hub.IsSignedIn,
            Availability = TrackPlanner.AvailabilityOf(p, inputs),
            Engine = inputs.Engine,
            FromElsewhere = elsewhere,
        };
    }

    /// <summary>Price, trial, stage: three chips at most (9.1). Released shows none on cards and rows.</summary>
    public List<ChipModel> Chips(Catalogue cat, bool onPicture, bool showReleased = false)
    {
        // Owned only when held in full; a free-tier install says LITE (or TRIAL), never Owned.
        var chips = new List<ChipModel>
        {
            Owned ? new ChipModel("OWNED", "owned", onPicture)
            : IsFreeTier && IsInstalled ? new ChipModel(TierWord.ToUpperInvariant(), "free", onPicture)
            : IsIncluded ? new ChipModel("INCLUDED", "owned", onPicture)
            : ChipModel.Price(cat, PriceKey, onPicture),
        };
        if (Product.Trial) chips.Add(new ChipModel(cat.Labels.Trial.ToUpperInvariant(), "free", onPicture));
        if (IsNotBuilt) chips.Add(new ChipModel($"NOT BUILT FOR {Engine}", "soon", onPicture));
        else if (IsComing) chips.Add(new ChipModel(cat.StageLabel(Stage.Coming).ToUpperInvariant(), "soon", onPicture));
        else if (ChipModel.Stage(cat, Stage, onPicture, showReleased) is { } s) chips.Add(s);
        return chips;
    }

    /// <summary>The main button's words (4.7, 4.10) and whether it can be pressed.</summary>
    public (string Text, bool Enabled, bool Visible) Button(bool productPage)
    {
        if (IsNotBuilt) return productPage ? (NotBuiltLine.TrimEnd('.'), false, true) : ("", false, false);
        if (IsComing) return productPage ? ("See it on kovati.dev ↗", true, true) : ("", false, false);
        // Included with another paid tool: never offered on its own.
        if (IsIncluded) return productPage ? ($"Comes with {IncludedWithName}", false, true) : ("", false, false);
        if (NeedsPurchase) return PaymentsLive ? ("Add to cart", true, true) : ("Buying opens soon", false, true);
        if (IsInstalled) return HasUpdate ? ($"Update to {LatestVersion}", true, true) : ("Installed", false, true);
        if (!SignedIn && productPage) return ("Sign in to install", true, true);
        if (IsLabs) return ("Install the Labs build", true, true);
        if (IsFreeTier) return ($"Install {TierWord}", true, true);
        return ("Install", true, true);
    }

    /// <summary>"Buy Full · $99.99" beside a free tier's button (beta 7): the store page, as Add to cart opens it.</summary>
    public (string Text, bool Enabled, bool Visible) BuyFullButton()
    {
        if (!IsFreeTier) return ("", false, false);
        if (!PaymentsLive) return ("Full: buying opens soon", false, true);
        return (Price is { } price ? $"Buy Full · {price}" : "Buy Full", true, true);
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

    private static bool MediaFound(string? path) => Controls.MediaImage.Available(path);

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
