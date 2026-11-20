using Forge.Core;
using Forge.Core.Catalogue;

namespace Forge.Hub.ViewModels;

/// <summary>
/// One page of the window: Home, a track, Library, Runners, Components (spec 3). The window shows
/// <see cref="MainViewModel.CurrentPage"/> through the ViewLocator; there is no navigation library.
/// </summary>
public abstract class PageViewModel(MainViewModel hub) : ViewModelBase
{
    public MainViewModel Hub { get; } = hub;

    /// <summary>The page's name in a back link: "Home", "Animation", "Library".</summary>
    public abstract string Title { get; }

    /// <summary>The page re-reads what it shows: receipts, ownership, the catalogue, the chosen engine.</summary>
    public virtual void Refresh() { }

    /// <summary>"← Home", "← Animation": where the back link goes, named. Empty when the page has none.</summary>
    public virtual string BackLabel => Hub.BackLabel;
    public bool HasBack => Hub.CanGoBack;

    /// <summary>
    /// Esc, Alt+Left and the mouse's back button, before the window goes back a page: a page with a state of its
    /// own to step out of (a project opened on the Projects page) steps out and says so. Most pages have none.
    /// </summary>
    public virtual bool BackInside() => false;
}

/// <summary>A chip (9.1): its words and its tone. Order on a card or row: price, trial, stage; three at most.</summary>
public sealed record ChipModel(string Text, string Kind, bool OnPicture = false)
{
    public bool IsFree => Kind is "free" or "owned";
    public bool IsPaid => Kind == "paid";
    public bool IsStage => Kind == "stage";
    public bool IsSoon => Kind == "soon";
    public bool IsLabs => Kind == "labs";

    public static ChipModel Price(Catalogue cat, string priceKey, bool onPicture = false) =>
        new(cat.PriceLabel(priceKey).ToUpperInvariant(), priceKey == "paid" ? "paid" : "free", onPicture);

    /// <summary>The stage chip, or null for Released (no chip on cards and rows, as on the site).</summary>
    public static ChipModel? Stage(Catalogue cat, Core.Stage stage, bool onPicture = false, bool showReleased = false) => stage switch
    {
        Core.Stage.Released when !showReleased => null,
        Core.Stage.Labs => new(cat.StageLabel(stage).ToUpperInvariant(), "labs", onPicture),
        Core.Stage.Coming => new(cat.StageLabel(stage).ToUpperInvariant(), "soon", onPicture),
        _ => new(cat.StageLabel(stage).ToUpperInvariant(), "stage", onPicture),
    };
}
