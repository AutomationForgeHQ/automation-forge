using Avalonia.Input.Platform;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Forge.Core;
using Forge.Core.Catalogue;

namespace Forge.Hub.ViewModels.Pages;

public enum LibraryTab { Updates, Installed, Account }

/// <summary>
/// Library (4.8): Updates, Installed, On your account, and "Everything on this machine" at the right.
/// Updates are counted per product and opened down to components and versions (8.1); "Update all" leaves
/// Labs and pinned products alone and says so (8.2 to 8.4); notes are read in place (8.5).
/// </summary>
public sealed partial class LibraryPageViewModel : PageViewModel
{
    public LibraryPageViewModel(MainViewModel hub, LibraryTab tab = LibraryTab.Updates) : base(hub)
    {
        _tab = tab;
        Refresh();
    }

    public override string Title => "Library";

    [ObservableProperty] private LibraryTab _tab;
    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private string _summaryLine = "";
    [ObservableProperty] private string _updateAllTip = "";
    [ObservableProperty] private string _updatesTabLabel = "Updates";
    [ObservableProperty] private string _installedTabLabel = "Installed";
    [ObservableProperty] private string _accountTabLabel = "On your account";
    [ObservableProperty] private string _copied = "";

    public ObservableCollection<UpdateGroupModel> Groups { get; } = [];
    public ObservableCollection<UpdateRowModel> LabsRows { get; } = [];
    public ObservableCollection<UpdateRowModel> PinnedRows { get; } = [];
    public ObservableCollection<InstalledGroupModel> InstalledGroups { get; } = [];
    public ObservableCollection<OwnedRowModel> AccountRows { get; } = [];

    public bool IsUpdates => Tab == LibraryTab.Updates;
    public bool IsInstalled => Tab == LibraryTab.Installed;
    public bool IsAccount => Tab == LibraryTab.Account;
    public bool HasUpdates => Groups.Count > 0;
    public bool HasLabs => LabsRows.Count > 0;
    public bool HasPinned => PinnedRows.Count > 0;
    public bool NothingToUpdate => !HasUpdates && !HasLabs && !Hub.HasHubUpdate;
    public bool HasHubUpdate => Hub.HasHubUpdate;
    public string HubUpdateLine => $"Kovati Hub · {AppInfo.Version} → {Hub.HubUpdateVersion}";
    public string QuietLine => string.IsNullOrEmpty(Hub.CheckedAt) ? "Everything is up to date." : $"Everything is up to date. Checked at {Hub.CheckedAt}.";
    public bool SignedIn => Hub.IsSignedIn;
    public bool HasAccountRows => AccountRows.Count > 0;
    public string LabsLine => $"{Hub.Catalogue?.LabsLine} Not part of Update all.";

    partial void OnTabChanged(LibraryTab value)
    {
        OnPropertyChanged(nameof(IsUpdates));
        OnPropertyChanged(nameof(IsInstalled));
        OnPropertyChanged(nameof(IsAccount));
    }

    partial void OnFilterChanged(string value) => BuildInstalled();

    [RelayCommand] private void ShowUpdatesTab() => Tab = LibraryTab.Updates;
    [RelayCommand] private void ShowInstalledTab() => Tab = LibraryTab.Installed;
    [RelayCommand] private void ShowAccountTab() => Tab = LibraryTab.Account;
    [RelayCommand] private void EverythingOnThisMachine() => Hub.ShowComponents();
    [RelayCommand] private Task CheckNow() => Hub.CheckNowCommand.ExecuteAsync(null);
    [RelayCommand] private Task SignIn() => Hub.SignInCommand.ExecuteAsync(null);
    [RelayCommand] private void ManageAccount() => Hub.OpenUrl(Hub.Catalogue?.Links.Account ?? "https://app.kovati.dev");
    [RelayCommand] private Task UpdateHub() => Hub.ApplyHubUpdateCommand.ExecuteAsync(null);

    private UpdateList? _list;

    public override void Refresh()
    {
        Groups.Clear();
        LabsRows.Clear();
        PinnedRows.Clear();
        AccountRows.Clear();
        var cat = Hub.Catalogue;
        var inputs = Hub.Inputs();
        if (cat is null || inputs is null) { BuildInstalled(); Announce(); return; }

        _list = UpdatePlanner.Build(inputs);
        foreach (var t in cat.TracksInOrder)
        {
            var rows = _list.Regular.Where(r => r.Track == t.Slug).ToList();
            if (rows.Count == 0) continue;
            Groups.Add(new UpdateGroupModel(this, t.Name.ToUpperInvariant(), $"Update {t.Name}", rows.Select(r => new UpdateRowModel(this, r)).ToList()));
        }
        var other = _list.Regular.Where(r => r.Track is null).ToList();
        if (other.Count > 0) Groups.Add(new UpdateGroupModel(this, "OTHER TOOLS", "Update other tools", other.Select(r => new UpdateRowModel(this, r)).ToList()));
        foreach (var r in _list.Labs) LabsRows.Add(new UpdateRowModel(this, r));
        foreach (var r in _list.Pinned) PinnedRows.Add(new UpdateRowModel(this, r));

        var count = _list.Count + (Hub.HasHubUpdate ? 1 : 0);
        SummaryLine = $"{Words.Count(count, "update")} for {Hub.TargetLabel}";
        UpdateAllTip = _list.UpdateAllTip;
        UpdatesTabLabel = count > 0 ? $"Updates {count}" : "Updates";

        foreach (var p in Hub.OwnedNotInstalled()) AccountRows.Add(new OwnedRowModel(Hub, p, cat));
        AccountTabLabel = AccountRows.Count > 0 ? $"On your account {AccountRows.Count}" : "On your account";
        BuildInstalled();
        Announce();
    }

    private void Announce()
    {
        OnPropertyChanged(nameof(HasUpdates));
        OnPropertyChanged(nameof(HasLabs));
        OnPropertyChanged(nameof(HasPinned));
        OnPropertyChanged(nameof(NothingToUpdate));
        OnPropertyChanged(nameof(HasHubUpdate));
        OnPropertyChanged(nameof(HubUpdateLine));
        OnPropertyChanged(nameof(QuietLine));
        OnPropertyChanged(nameof(SignedIn));
        OnPropertyChanged(nameof(HasAccountRows));
    }

    /// <summary>Installed (4.8): grouped by track; engines and agent access indented under their tool; the editor plugin last.</summary>
    private void BuildInstalled()
    {
        InstalledGroups.Clear();
        var cat = Hub.Catalogue;
        var inputs = Hub.Inputs();
        if (cat is null || inputs is null) return;
        var f = Filter.Trim();
        bool Match(string s) => f.Length == 0 || s.Contains(f, StringComparison.OrdinalIgnoreCase);
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var count = 0;

        foreach (var t in cat.TracksInOrder.Cast<Track?>().Append(null))
        {
            var rows = new List<InstalledRowModel>();
            var tools = cat.Products.Where(p => !p.IsEngine && (t is null ? cat.Track(p.Track) is null : p.Track == t.Slug)
                                                && p.MainComponent is { } main && inputs.Installed.ContainsKey(main) && !claimed.Contains(main));
            foreach (var p in tools)
            {
                var block = new List<InstalledRowModel> { InstalledRowModel.For(this, p, p.MainComponent!, inputs, p.Name, TypeLine(cat, p), 0) };
                foreach (var id in p.Plugins) claimed.Add(id);
                foreach (var e in cat.Products.Where(e => e.IsEngine && e.Extends == p.Slug && e.MainComponent is { } em && inputs.Installed.ContainsKey(em)))
                {
                    block.Add(InstalledRowModel.For(this, e, e.MainComponent!, inputs, $"{cat.ShortName(e)} engine", e.Runs ?? "", 1));
                    foreach (var id in e.Plugins) claimed.Add(id);
                }
                foreach (var a in p.AgentPlugins.Where(inputs.Installed.ContainsKey))
                {
                    block.Add(InstalledRowModel.For(this, p, a, inputs, "Agent access", "", 1));
                    claimed.Add(a);
                }
                if (block.Any(r => Match(r.Name) || Match(p.Name))) rows.AddRange(block);
            }
            if (rows.Count == 0) continue;
            count += rows.Count;
            InstalledGroups.Add(new InstalledGroupModel(t?.Name.ToUpperInvariant() ?? "OTHER TOOLS", rows));
        }

        // Engines whose tool is not installed, and components the catalogue does not know.
        var rest = inputs.Installed.Keys.Where(id => !claimed.Contains(id) && cat.FoundationOf(id) is null).OrderBy(x => x).ToList();
        var loose = rest.Select(id => cat.ProductOfComponent(id) is { } p
                ? InstalledRowModel.For(this, p, id, inputs, p.Plugins.Contains(id) ? p.Name : "Agent access", p.Name, 0)
                : InstalledRowModel.For(this, null, id, inputs, id, "Not in the catalogue yet", 0))
            .Where(r => Match(r.Name)).ToList();
        if (loose.Count > 0) InstalledGroups.Add(new InstalledGroupModel("MORE", loose));

        // The Kovati foundation (catalogue `always`): every piece and its agent access that is installed here.
        var always = cat.Always
            .SelectMany(a => new[] { (id: a.Plugin, name: a.Name, line: a.Line) }
                .Concat(a.AgentPlugins.Select(x => (id: x, name: $"Agent access for {a.Name}", line: ""))))
            .Where(x => inputs.Installed.ContainsKey(x.id) && !claimed.Contains(x.id))
            .Select(x => InstalledRowModel.For(this, null, x.id, inputs, x.name, x.line, 0)).Where(r => Match(r.Name)).ToList();
        if (always.Count > 0) InstalledGroups.Add(new InstalledGroupModel("KOVATI FOUNDATION", always));

        InstalledTabLabel = inputs.Installed.Count > 0 ? $"Installed {inputs.Installed.Count}" : "Installed";
    }

    private static string TypeLine(Catalogue cat, Product p) =>
        p.Type == "Add-on" && cat.Product(p.Extends) is { } parent ? $"Add-on for {parent.Name}" : p.Type;

    internal Task UpdateIds(IEnumerable<string> ids)
    {
        var list = ids.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return list.Length == 0 ? Task.CompletedTask : Hub.UpdateComponentsAsync(list);
    }

    /// <summary>"Update all": every row except the Labs group and the pinned ones (8.2).</summary>
    [RelayCommand]
    private Task UpdateAll() => _list is null ? Task.CompletedTask : UpdateIds(_list.UpdateAllIds);

    /// <summary>"Copy the list": plain text for a team's notes.</summary>
    [RelayCommand]
    private async Task CopyList()
    {
        if (_list is null) return;
        var lines = new List<string> { $"Kovati Hub, {Hub.TargetLabel}, {DateTime.Now:d MMMM yyyy}" };
        foreach (var r in _list.Rows.Where(r => !r.IsPinned))
            foreach (var p in r.Parts) lines.Add($"{p.Id} {p.From} -> {p.To}");
        var text = string.Join(Environment.NewLine, lines);
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime { MainWindow: { Clipboard: { } cb } })
            await cb.SetTextAsync(text);
        Copied = "Copied.";
        Hub.Say($"Copied the list of {Words.Count(lines.Count - 1, "update")}.");
    }
}

public sealed partial class UpdateGroupModel(LibraryPageViewModel page, string label, string updateLabel, IReadOnlyList<UpdateRowModel> rows)
{
    public string Label { get; } = label;
    public string UpdateLabel { get; } = updateLabel;
    public IReadOnlyList<UpdateRowModel> Rows { get; } = rows;

    [RelayCommand] private Task Update() => page.UpdateIds(Rows.SelectMany(r => r.Update.Parts.Select(p => p.Id)));
}

/// <summary>One product's update: its parts, its notes in place, and its "…" menu.</summary>
public sealed partial class UpdateRowModel : ObservableObject
{
    private readonly LibraryPageViewModel _page;

    public UpdateRowModel(LibraryPageViewModel page, ProductUpdate update)
    {
        _page = page;
        Update = update;
        var cat = page.Hub.Catalogue!;
        var manifest = page.Hub.Manifest!;
        Picture = update.Product is { } p
            ? cat.Track(p.Track)?.Capabilities.FirstOrDefault(c => c.Product == p.Slug)?.Media.Poster ?? p.Media.Card ?? p.Media.Hero
            : null;
        foreach (var part in update.Parts)
        {
            var plugin = manifest.Plugin(part.Id);
            var entries = plugin?.ChangesBetween(part.From, part.To) ?? [];
            Notes.Add(new NotesBlockModel(update.Parts.Count > 1 ? part.Name.ToUpperInvariant() : "", entries,
                plugin?.Latest(page.Hub.EngineVersion, page.Hub.Channel)?.Notes));
        }
    }

    public ProductUpdate Update { get; }
    public string Title => Update.Title;
    public string Subtitle => Update.Subtitle;
    public string? Picture { get; }
    public string VersionLine => $"{Update.Main.From} → {Update.Main.To}";
    public string PartsLine => Update.PartsLine;
    public bool HasParts => Update.HasManyParts;
    public string PinnedLine => $"Pinned at {Update.PinnedAt} · {Update.Main.To} is out";
    public bool LeavesLabs => Update.LeavesLabs;
    public ObservableCollection<NotesBlockModel> Notes { get; } = [];

    [ObservableProperty] private bool _notesOpen;
    public string NotesLabel => NotesOpen ? "What's new ⌃" : "What's new ⌄";
    partial void OnNotesOpenChanged(bool value) => OnPropertyChanged(nameof(NotesLabel));

    [RelayCommand] private void ToggleNotes() => NotesOpen = !NotesOpen;
    [RelayCommand] private Task DoUpdate() => _page.UpdateIds(Update.Parts.Select(p => p.Id));
    [RelayCommand] private void Pin() { if (Update.Product is { } p) _page.Hub.Pin(p); else _page.Hub.PinComponent(Update.Main.Id); }
    [RelayCommand] private void Unpin() { if (Update.Product is { } p) _page.Hub.Unpin(p); else _page.Hub.UnpinComponent(Update.Main.Id); }
    [RelayCommand] private void ShowComponents() => _page.Hub.ShowComponentsFor(Update.Main.Id);
}

/// <summary>The notes of one component, every version between the installed one and the offered one, newest first.</summary>
public sealed class NotesBlockModel(string heading, IReadOnlyList<Core.Manifest.ChangelogEntry> entries, string? releasePage)
{
    public string Heading { get; } = heading;
    public bool HasHeading => Heading.Length > 0;
    public IReadOnlyList<Core.Manifest.ChangelogEntry> Entries { get; } = entries;
    public bool HasEntries => Entries.Count > 0;
    public string? ReleasePage { get; } = releasePage?.StartsWith("https://", StringComparison.Ordinal) == true ? releasePage : null;
    public bool HasReleasePage => !HasEntries && ReleasePage is not null;
}

public sealed record InstalledGroupModel(string Label, IReadOnlyList<InstalledRowModel> Rows);

/// <summary>A row of the Installed tab: name, type, stage, version, its state and its menu.</summary>
public sealed partial class InstalledRowModel : ObservableObject
{
    private readonly LibraryPageViewModel _page;

    private InstalledRowModel(LibraryPageViewModel page, Product? product, string id, string name, string type, int indent,
        string version, string? newer, bool pinned, ChipModel? stage, IReadOnlyList<Core.Manifest.ChangelogEntry> notes)
    {
        _page = page;
        Product = product;
        Id = id;
        Name = name;
        Type = type;
        Indent = indent;
        Version = version;
        Newer = newer;
        Pinned = pinned;
        Stage = stage;
        Notes = notes;
    }

    public static InstalledRowModel For(LibraryPageViewModel page, Product? product, string id, PlanInputs inputs, string name, string type, int indent)
    {
        var version = inputs.Installed[id];
        var newer = TrackPlanner.NewerVersion(id, inputs with { Pins = new Dictionary<string, string>() });
        var stage = product is null || indent > 0 ? null : ChipModel.Stage(page.Hub.Catalogue!, TrackPlanner.StageOf(product, inputs));
        var notes = newer is null ? [] : inputs.Manifest.Plugin(id)?.ChangesBetween(version, newer) ?? [];
        return new InstalledRowModel(page, product, id, name, type, indent, version, newer, inputs.Pins.ContainsKey(id), stage, notes);
    }

    public Product? Product { get; }
    public string Id { get; }
    public string Name { get; }
    public string Type { get; }
    public int Indent { get; }
    public Avalonia.Thickness IndentMargin => new(Indent * 28, 0, 0, 0);
    public bool IsChild => Indent > 0;
    public string Version { get; }
    public string? Newer { get; }
    public bool Pinned { get; }
    public ChipModel? Stage { get; }
    public bool HasStage => Stage is not null;
    public IReadOnlyList<Core.Manifest.ChangelogEntry> Notes { get; }
    public bool HasNotes => Notes.Count > 0;
    public bool HasUpdate => Newer is not null && !Pinned;
    public string StateLine => Pinned ? $"Pinned at {Version}" : Newer is null ? "Up to date" : $"Update to {Newer}";
    public bool CanPin => Product is not null && !Pinned && Indent == 0;
    public bool CanUnpin => Product is not null && Pinned && Indent == 0;

    [ObservableProperty] private bool _notesOpen;

    [RelayCommand] private void ToggleNotes() => NotesOpen = !NotesOpen;
    [RelayCommand] private Task DoUpdate() => Newer is null ? Task.CompletedTask : _page.UpdateIds([Id]);
    [RelayCommand] private void Pin() { if (Product is not null) _page.Hub.Pin(Product); }
    [RelayCommand] private void Unpin() { if (Product is not null) _page.Hub.Unpin(Product); }
    [RelayCommand] private void ShowComponents() => _page.Hub.ShowComponentsFor(Id);
    [RelayCommand] private Task Remove() => _page.Hub.RemoveComponentAsync(Id);
}
