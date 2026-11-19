using CommunityToolkit.Mvvm.Input;
using Forge.Core.Catalogue;
using Forge.Hub.ViewModels.Pages;

namespace Forge.Hub.ViewModels;

/// <summary>Slice 4: product pages, Browse and search, and installing or removing one product.</summary>
public partial class MainViewModel
{
    public void OpenProduct(string slug)
    {
        if (Catalogue?.Product(slug) is { } p) Navigate(new ProductPageViewModel(this, p));
    }

    [RelayCommand]
    public void ShowBrowse()
    {
        _back.Clear();
        CurrentPage = new BrowsePageViewModel(this);
    }

    /// <summary>
    /// Install one product from its page (4.7): the same engine defaults and the same agent switch as its track,
    /// for this one capability, through the planner.
    /// </summary>
    public Task InstallProductWithEnginesAsync(Product product)
    {
        if (Inputs() is not { } inputs || Catalogue is null) return Task.CompletedTask;
        var track = Catalogue.Tracks.FirstOrDefault(t => t.Slug == product.Track && t.Capabilities.Any(c => c.Product == product.Slug))
                    ?? Catalogue.Tracks.FirstOrDefault(t => t.Capabilities.Any(c => c.Product == product.Slug));
        if (track is null) return InstallProductAsync(product);
        var cap = track.Capabilities.First(c => c.Product == product.Slug);
        var defaults = TrackPlanner.DefaultSelection(track, inputs with { ShowLabs = true });
        var selection = new TrackSelection([cap.Id], defaults.Engines.Where(kv => kv.Key == cap.Id).ToDictionary(kv => kv.Key, kv => kv.Value));
        var plan = TrackPlanner.Plan(track, selection, inputs with { ShowLabs = true });
        var ids = plan.Ids.ToArray();
        return ids.Length == 0 ? Task.CompletedTask : RunAsync(ids);
    }

    public Task UpdateProductAsync(Product product)
    {
        if (Inputs() is not { } inputs) return Task.CompletedTask;
        var row = UpdatePlanner.Build(inputs with { Pins = new Dictionary<string, string>() }).Rows.FirstOrDefault(r => r.Product?.Slug == product.Slug);
        var ids = row?.Parts.Select(p => p.Id).ToArray() ?? [];
        return ids.Length == 0 ? Task.CompletedTask : RunAsync(ids);
    }

    /// <summary>
    /// What "Remove" takes (4.7): the product's components, its engines and its agent access, and nothing another
    /// installed product still needs.
    /// </summary>
    public IReadOnlyList<string> RemovalSet(Product product)
    {
        if (Catalogue is null || _manifest is null) return [];
        var installed = InstalledOnTarget();
        var set = new HashSet<string>(product.Plugins.Concat(product.AgentPlugins).Where(installed.ContainsKey), StringComparer.OrdinalIgnoreCase);
        if (!product.IsEngine)
            foreach (var e in Catalogue.Products.Where(e => e.IsEngine && e.Extends == product.Slug))
                foreach (var id in e.Plugins.Concat(e.AgentPlugins).Where(installed.ContainsKey)) set.Add(id);
        // Anything left installed that depends on a member keeps it.
        bool changed;
        do
        {
            changed = false;
            foreach (var other in installed.Keys.Where(id => !set.Contains(id)))
                foreach (var dep in _manifest.Plugin(other)?.Dependencies ?? [])
                    if (set.Remove(dep)) changed = true;
        } while (changed);
        return set.ToList();
    }

    public Task RemoveProductAsync(Product product)
    {
        var ids = RemovalSet(product).ToArray();
        return ids.Length == 0 ? Task.CompletedTask : RunAsync(ids, uninstall: true);
    }
}
