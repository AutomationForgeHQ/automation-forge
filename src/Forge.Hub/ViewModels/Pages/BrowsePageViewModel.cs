using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Forge.Core.Catalogue;

namespace Forge.Hub.ViewModels.Pages;

/// <summary>
/// Browse (4.10): the eight Forges as a rail, then one shelf at a time by category, filtered by track. Labs
/// products join their shelves only with "Show Labs" on; products with listed: false are on no shelf.
/// </summary>
public sealed partial class BrowsePageViewModel : PageViewModel
{
    /// <summary>
    /// The site's one line per category (v2/lib/featured.ts). The catalogue carries them as `categoryInfo`; these
    /// are only the words for a catalogue that predates it (an installer's copy from before slice 5).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> CategoryLines = new Dictionary<string, string>
    {
        ["Tool"] = "Has its own panel in Unreal and does one job well. The Forges.",
        ["Utility"] = "A focused fix with little or no interface: run it and the problem is gone.",
        ["Kit"] = "Content and ready setups you drop into a project. They work with or without our tools.",
        ["Add-on"] = "Plug a model or service into one of our tools, connect our tools to someone else’s, or add a feature.",
        ["Playbook"] = "A proven pipeline through an outside app: a tutorial for you, skills for your agent.",
    };

    private static readonly Dictionary<string, string> Plural = new()
    {
        ["Tool"] = "Tools", ["Utility"] = "Utilities", ["Kit"] = "Kits", ["Add-on"] = "Add-ons", ["Playbook"] = "Playbooks",
    };

    public BrowsePageViewModel(MainViewModel hub) : base(hub)
    {
        _showLabs = hub.HubState.ShowLabs;
        Tracks = ["All tracks", .. hub.Catalogue?.TracksInOrder.Select(t => t.Name) ?? [], "Studio"];
        _trackFilter = "All tracks";
        Refresh();
    }

    public override string Title => "Browse";

    public ObservableCollection<ForgeCardModel> Forges { get; } = [];
    public ObservableCollection<CategoryModel> Categories { get; } = [];
    public ObservableCollection<PosterModel> Shelf { get; } = [];
    public ObservableCollection<ShelfGroupModel> Groups { get; } = [];
    public IReadOnlyList<string> Tracks { get; }

    [ObservableProperty] private bool _showLabs;
    [ObservableProperty] private string _category = "Tool";
    [ObservableProperty] private string _trackFilter;

    public string CategoryLine => Hub.Catalogue?.CategoryInfo.GetValueOrDefault(Category)?.Line is { Length: > 0 } line ? line : CategoryLines.GetValueOrDefault(Category, "");

    private string TitleOf(string type) =>
        Hub.Catalogue?.CategoryInfo.GetValueOrDefault(type)?.Title is { Length: > 0 } title ? title : Plural.GetValueOrDefault(type, type);
    public bool IsGrouped => Category == "Add-on";
    public bool IsEmpty => Shelf.Count == 0 && Groups.Count == 0;
    public string LabsOnLine => $"Labs is on. {Hub.Catalogue?.LabsLine}";

    partial void OnShowLabsChanged(bool value) { Hub.SetShowLabs(value); Refresh(); }
    partial void OnCategoryChanged(string value) => BuildShelf();
    partial void OnTrackFilterChanged(string value) => BuildShelf();

    public override void Refresh()
    {
        var cat = Hub.Catalogue;
        Forges.Clear();
        Categories.Clear();
        if (cat is null) return;
        var heroes = cat.HeroOrder.Select(cat.Product).OfType<Product>().ToList();
        for (var i = 0; i < heroes.Count; i++) Forges.Add(new ForgeCardModel(Hub, heroes[i], i + 1, heroes.Count));
        foreach (var type in cat.Categories)
            Categories.Add(new CategoryModel(this, type, TitleOf(type), Visible(type).Count()));
        foreach (var c in Categories) c.Selected = c.Type == Category;
        BuildShelf();
        OnPropertyChanged(nameof(LabsOnLine));
    }

    /// <summary>What a shelf may show: listed, not a hero when the shelf is Tools, Labs only with the switch on.</summary>
    private IEnumerable<Product> Visible(string type)
    {
        var cat = Hub.Catalogue!;
        var heroes = new HashSet<string>(cat.HeroOrder, StringComparer.OrdinalIgnoreCase);
        return cat.Products
            .Where(p => p.IsListed && p.Type == type && !(type == "Tool" && heroes.Contains(p.Slug)))
            .Where(p => ShowLabs || !ProductState.Of(Hub, p).IsLabs);
    }

    private void BuildShelf()
    {
        Shelf.Clear();
        Groups.Clear();
        var cat = Hub.Catalogue;
        if (cat is null) return;
        foreach (var c in Categories) c.Selected = c.Type == Category;
        var trackOrder = cat.TracksInOrder.Select(t => t.Slug).ToList();
        var products = Visible(Category)
            .Where(p => TrackFilter == "All tracks"
                        || (TrackFilter == "Studio" ? cat.Track(p.Track) is null : cat.Track(p.Track)?.Name == TrackFilter))
            .OrderBy(p => trackOrder.IndexOf(p.Track) is var i and >= 0 ? i : 99)
            .ToList();
        if (Category == "Add-on")
        {
            // Our tools first, in the order of their tracks; someone else's product (Narrative Pro) last.
            var groups = products.GroupBy(p => cat.Product(p.Extends)?.Name ?? p.Extends ?? "Other")
                .OrderBy(g => cat.Product(g.First().Extends) is { } parent ? trackOrder.IndexOf(parent.Track) is var i and >= 0 ? i : 50 : 100);
            foreach (var g in groups)
                Groups.Add(new ShelfGroupModel($"FOR {g.Key.ToUpperInvariant()}", g.Select(p => new PosterModel(Hub, p)).ToList()));
        }
        else
        {
            foreach (var p in products) Shelf.Add(new PosterModel(Hub, p));
        }
        OnPropertyChanged(nameof(CategoryLine));
        OnPropertyChanged(nameof(IsGrouped));
        OnPropertyChanged(nameof(IsEmpty));
    }
}

public sealed partial class CategoryModel(BrowsePageViewModel page, string type, string title, int count) : ObservableObject
{
    public string Type { get; } = type;
    public string Label { get; } = $"{title} {count}";
    [ObservableProperty] private bool _selected;

    [RelayCommand] private void Choose() => page.Category = Type;
}

public sealed record ShelfGroupModel(string Label, IReadOnlyList<PosterModel> Posters);
