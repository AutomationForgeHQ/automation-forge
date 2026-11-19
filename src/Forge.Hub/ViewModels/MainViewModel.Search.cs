using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Forge.Core.Catalogue;
using Forge.Hub.ViewModels.Pages;

namespace Forge.Hub.ViewModels;

/// <summary>
/// Search (4.11): one field in the bar, Ctrl+K to reach it, a panel of results under it. Tracks, then tools and
/// add-ons (names before lines), then component ids; three, six and four at most. Labs products are found with
/// their chip whatever "Show Labs" says, and so is a product that is on no shelf.
/// </summary>
public partial class MainViewModel
{
    [ObservableProperty] private string _searchText = "";

    public ObservableCollection<SearchSectionModel> SearchSections { get; } = [];
    public bool ShowSearch => SearchText.Trim().Length > 0;
    public bool SearchEmpty => ShowSearch && SearchSections.Count == 0;
    public string SearchEmptyLine => $"Nothing called “{SearchText.Trim()}”.";

    partial void OnSearchTextChanged(string value) => RunSearch();

    private void RunSearch()
    {
        SearchSections.Clear();
        var q = SearchText.Trim();
        if (q.Length > 0 && Catalogue is { } cat)
        {
            bool Has(string? s) => s?.Contains(q, StringComparison.OrdinalIgnoreCase) == true;

            var tracks = cat.TracksInOrder.Where(t => Has(t.Name) || Has(t.Promise)).Take(3)
                .Select(t => new SearchResultModel(this, t.Name, t.Promise, t.Media.Door, [], () => OpenTrack(t.Slug))).ToList();
            if (tracks.Count > 0) SearchSections.Add(new SearchSectionModel("TRACKS", tracks));

            var capProducts = cat.Tracks.SelectMany(t => t.Capabilities).Where(c => Has(c.Name)).Select(c => c.Product).ToHashSet();
            var byName = cat.Products.Where(p => Has(p.Name) || capProducts.Contains(p.Slug));
            var byLine = cat.Products.Where(p => Has(p.Tagline) || Has(p.Line) || Has(p.Vendor) || Has(p.Runs) || p.Software.Any(Has));
            var products = byName.Concat(byLine).Distinct().Take(6)
                .Select(p =>
                {
                    var state = ProductState.Of(this, p);
                    var chips = state.Chips(cat, onPicture: false);
                    var sub = p.IsEngine && cat.Product(p.Extends) is { } parent ? $"for {parent.Name} · {p.Runs}" : p.Line;
                    return new SearchResultModel(this, p.Name, sub, p.Media.Card ?? p.Media.Hero, chips, () => OpenProduct(p.Slug));
                }).ToList();
            if (products.Count > 0) SearchSections.Add(new SearchSectionModel("TOOLS AND ADD-ONS", products));

            var ids = (_manifest?.Plugins.Select(p => p.Id) ?? []).Concat(InstalledOnTarget().Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase).Where(Has).Take(4)
                .Select(id =>
                {
                    var owner = cat.ProductOfComponent(id);
                    var line = owner is null ? "Not in the catalogue yet"
                        : owner.AgentPlugins.Contains(id, StringComparer.OrdinalIgnoreCase) ? $"Agent access for {owner.Name}" : owner.Name;
                    return new SearchResultModel(this, id, line, null, [], () => { if (owner is not null) OpenProduct(owner.Slug); else ShowComponentsFor(id); }, mono: true);
                }).ToList();
            if (ids.Count > 0) SearchSections.Add(new SearchSectionModel("COMPONENTS", ids));
        }
        OnPropertyChanged(nameof(ShowSearch));
        OnPropertyChanged(nameof(SearchEmpty));
        OnPropertyChanged(nameof(SearchEmptyLine));
    }

    /// <summary>Enter opens the first result.</summary>
    [RelayCommand]
    public void OpenFirstResult() => SearchSections.FirstOrDefault()?.Items.FirstOrDefault()?.OpenCommand.Execute(null);

    [RelayCommand]
    public void ClearSearch() => SearchText = "";

    [RelayCommand]
    private void BrowseFromSearch() { ClearSearch(); ShowBrowse(); }
}

public sealed record SearchSectionModel(string Label, IReadOnlyList<SearchResultModel> Items);

public sealed partial class SearchResultModel(MainViewModel hub, string title, string line, string? picture, IReadOnlyList<ChipModel> chips, Action open, bool mono = false)
{
    public string Title { get; } = title;
    public string Line { get; } = line;
    public string? Picture { get; } = picture;
    public bool HasPicture => !mono;
    public bool IsMono { get; } = mono;
    public IReadOnlyList<ChipModel> Chips { get; } = chips;

    [RelayCommand]
    private void Open()
    {
        hub.ClearSearch();
        open();
    }
}
