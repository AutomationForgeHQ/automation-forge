using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Forge.Hub.ViewModels.Pages;

public sealed record OtherReceipt(string Id, string Version, string Where, string Path);

/// <summary>
/// "Everything on this machine" (4.9): today's plugin list, kept on purpose, for developers, support and
/// anything the catalogue does not know yet. Every component in the manifest for the chosen target, grouped by
/// family, with Install, Update, Remove and What's new; a filter by id; and receipts on other targets.
/// In slice 1 it is also what Library opens.
/// </summary>
public sealed partial class ComponentsPageViewModel : PageViewModel
{
    public ComponentsPageViewModel(MainViewModel hub) : base(hub) => Refresh();

    public override string Title => "Everything on this machine";

    public ObservableCollection<ComponentFamilyModel> Families { get; } = [];
    public ObservableCollection<OtherReceipt> Others { get; } = [];

    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private string _summaryLine = "";

    public bool HasOthers => Others.Count > 0;
    public bool IsEmpty => Families.Count == 0;
    public string EmptyLine => string.IsNullOrWhiteSpace(Filter)
        ? (Hub.HasEngine ? "Reading the list of tools…" : $"Unreal Engine {Hub.EngineVersion} was not found on this computer, so there is nothing to list for it.")
        : $"No component called \"{Filter.Trim()}\".";

    partial void OnFilterChanged(string value) => Refresh();

    public override void Refresh()
    {
        Families.Clear();
        var f = Filter.Trim();
        // The Kovati foundation first (catalogue `always`, its agent access and what they need): one quiet place to
        // see that the pieces every install carries are here and current.
        var foundation = FoundationIds();
        var foundationRows = Hub.Sets.SelectMany(s => s.Plugins)
            .Where(p => foundation.Contains(p.Id) && (f.Length == 0 || p.Id.Contains(f, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(p => foundation.IndexOf(p.Id)).ToList();
        if (foundationRows.Count > 0) Families.Add(new ComponentFamilyModel("KOVATI FOUNDATION · WITH EVERY INSTALL", foundationRows));
        foreach (var set in Hub.Sets)
        {
            var rows = set.Plugins.Where(p => !foundation.Contains(p.Id) && (f.Length == 0 || p.Id.Contains(f, StringComparison.OrdinalIgnoreCase))).ToList();
            if (rows.Count == 0) continue;
            Families.Add(new ComponentFamilyModel(set.Name.ToUpperInvariant(), rows));
        }
        Others.Clear();
        foreach (var o in Hub.OtherTargetReceipts()) Others.Add(o);

        var all = Hub.Sets.SelectMany(s => s.Plugins).ToList();
        SummaryLine = $"{all.Count} components for {Hub.TargetLabel} · {all.Count(p => p.Installed is not null)} installed"
                      + (Hub.UpdatesCount > 0 ? $" · {Hub.UpdatesCount} to update" : "");
        OnPropertyChanged(nameof(HasOthers));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyLine));
    }

    /// <summary>The foundation's component ids, dependencies first, in the catalogue's order; its agent tools only with agent access on.</summary>
    private List<string> FoundationIds()
    {
        var ids = new List<string>();
        if (Hub.Catalogue is not { } cat || Hub.Manifest is not { } m) return ids;
        foreach (var a in cat.Always)
            foreach (var id in new[] { a.Plugin }.Concat(Hub.HubState.AgentsOn ? a.AgentPlugins : []))
                if (m.Plugin(id) is { } p)
                    foreach (var x in m.Closure(p))
                        if (!ids.Contains(x.Id, StringComparer.OrdinalIgnoreCase)) ids.Add(x.Id);
        return ids;
    }
}

public sealed record ComponentFamilyModel(string Name, IReadOnlyList<PluginRow> Rows);
