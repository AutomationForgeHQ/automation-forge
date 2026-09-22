using CommunityToolkit.Mvvm.Input;
using Forge.Core.Manifest;

namespace Forge.Hub.ViewModels;

/// <summary>
/// What an update brings, before it is taken: every version between the one installed and the one
/// offered, newest first, from the plugin's own CHANGELOG.md as the manifest carries it.
///
/// Every version, not only the newest. Somebody on 0.2.1 offered 0.3.0 is getting 0.2.2 as well,
/// and the fix they were waiting for may be in the one they skipped.
/// </summary>
public sealed partial class ReleaseNotesViewModel : ViewModelBase
{
    private readonly PluginRow _row;
    private readonly Action _close;

    public ReleaseNotesViewModel(PluginRow row, Action close)
    {
        _row = row;
        _close = close;
        Entries = row.Changes.Select(e => new ReleaseNotesEntry(e)).ToList();
    }

    public IReadOnlyList<ReleaseNotesEntry> Entries { get; }

    public string Plugin => _row.Id;
    public string Title => $"What's new · {_row.Id}";
    public bool HasEntries => Entries.Count > 0;

    public string SummaryLine
    {
        get
        {
            var from = _row.Installed?.Version ?? "";
            var to = _row.Latest?.Version ?? "";
            var count = Entries.Count;
            return count switch
            {
                0 => $"{from} installed → {to}",
                1 => $"{from} installed → {to} · one release",
                _ => $"{from} installed → {to} · {count} releases since yours",
            };
        }
    }

    /// <summary>
    /// Why the list is empty, said plainly. The manifest carries notes from the day it learned to;
    /// a catalogue fetched before then, or a version released with none, has nothing to show, and
    /// an empty window must not read as "nothing changed".
    /// </summary>
    public string EmptyLine => $"The catalogue has no release notes for {_row.Id} after {_row.Installed?.Version}. "
                               + (HasReleasePage ? "The release page has what was published with it." : "Nothing was published with it.");

    /// <summary>The newest release's page on GitHub. A paid plugin's releases are private, so it has none.</summary>
    public bool HasReleasePage => _row.Latest?.Notes?.StartsWith("https://", StringComparison.Ordinal) == true;

    public string ActionLabel => _row.ActionLabel == "Update" ? $"Update to {_row.Latest?.Version}" : _row.ActionLabel;
    public bool CanAct => _row.CanAct;

    [RelayCommand]
    private void OpenReleasePage()
    {
        if (HasReleasePage) HubUpdater.OpenInBrowser(_row.Latest!.Notes!);
    }

    /// <summary>The row's own action - Update, or Buy or Sign in when that is what stands in the way.</summary>
    [RelayCommand]
    private void Act()
    {
        _close();
        if (_row.ActCommand.CanExecute(null)) _row.ActCommand.Execute(null);
    }

    [RelayCommand]
    private void Close() => _close();
}

public sealed class ReleaseNotesEntry(ChangelogEntry entry)
{
    public string Version { get; } = entry.Version;
    public string Date { get; } = entry.Date ?? "";
    public string Body { get; } = entry.Body;
    public bool HasDate => !string.IsNullOrEmpty(Date);
}
