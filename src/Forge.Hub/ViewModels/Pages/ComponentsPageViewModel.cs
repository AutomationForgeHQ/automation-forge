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
        foreach (var set in Hub.Sets)
        {
            var rows = set.Plugins.Where(p => f.Length == 0 || p.Id.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
            if (rows.Count == 0) continue;
            Families.Add(new ComponentFamilyModel(set.Name.ToUpperInvariant(), rows));
        }
        Others.Clear();
        foreach (var o in Hub.OtherTargetReceipts()) Others.Add(o);

        var all = Hub.Sets.SelectMany(s => s.Plugins).ToList();
        SummaryLine = $"{all.Count} components for {Hub.EngineLabel} · {all.Count(p => p.Installed is not null)} installed"
                      + (Hub.UpdatesCount > 0 ? $" · {Hub.UpdatesCount} to update" : "");
        OnPropertyChanged(nameof(HasOthers));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyLine));
    }
}

public sealed record ComponentFamilyModel(string Name, IReadOnlyList<PluginRow> Rows);
