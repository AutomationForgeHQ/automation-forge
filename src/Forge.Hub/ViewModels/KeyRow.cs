using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Forge.Core.Machine;

namespace Forge.Hub.ViewModels;

/// <summary>
/// One API key, as the hub can act on it.
///
/// The hub sets, replaces and clears; it does not test. A test is one authenticated call in the
/// provider's own shape, and the alternative to saying so plainly is a second implementation of
/// every provider's authentication living in this application. The card says where testing happens
/// rather than offering a button that cannot do it.
/// </summary>
public partial class KeyRow : ViewModelBase
{
    private readonly DeclaredKey _key;
    private readonly Action _changed;

    public KeyRow(DeclaredKey key, Action changed)
    {
        _key = key;
        _changed = changed;
        Reread();
    }

    public string Title => string.IsNullOrWhiteSpace(_key.DisplayName) ? _key.Id : _key.DisplayName;
    public string Purpose => _key.Purpose;
    public string VaultEntry => _key.VaultEntry;
    public bool HasHelp => !string.IsNullOrWhiteSpace(_key.HelpUrl);

    /// <summary>An unset optional key is a fact, not a problem, and is not drawn as one.</summary>
    public bool IsOptional => _key.Optional;

    [ObservableProperty] private string _statusLine = "";
    [ObservableProperty] private bool _isSet;

    /// <summary>Amber only for a *required* key that is missing. Optional ones stay quiet.</summary>
    public bool IsMissing => !IsSet && !IsOptional;

    /// <summary>Typed here, never read back. Cleared the moment it is stored.</summary>
    [ObservableProperty] private string _secret = "";

    [ObservableProperty] private string _message = "";

    public bool CanSave => !string.IsNullOrWhiteSpace(Secret);

    partial void OnSecretChanged(string value) => OnPropertyChanged(nameof(CanSave));

    partial void OnIsSetChanged(bool value) => OnPropertyChanged(nameof(IsMissing));

    [RelayCommand]
    private void Save()
    {
        if (!CanSave) return;

        Message = CredentialVault.Store(_key, Secret.Trim(), out var error)
            ? "Stored. The editor reads the same entry."
            : error;

        // Out of memory and off the screen as soon as it is written. The interface never shows a
        // stored secret back, so leaving it in the box would be the one place it was visible.
        Secret = "";
        Reread();
        _changed();
    }

    [RelayCommand]
    private void Clear()
    {
        Message = CredentialVault.Clear(_key, out var error) ? "Removed." : error;
        Reread();
        _changed();
    }

    [RelayCommand]
    private void Help()
    {
        if (!HasHelp) return;
        Process.Start(new ProcessStartInfo(_key.HelpUrl!) { UseShellExecute = true });
    }

    private void Reread()
    {
        var source = CredentialVault.Find(_key);
        IsSet = source != KeySource.None;
        StatusLine = CredentialVault.Describe(_key);

        // Clearing the vault entry does not clear an environment variable, and a row that said
        // "Removed" while the plugin still had a key would be a small lie with real consequences.
        CanClear = source == KeySource.Vault;
        OnPropertyChanged(nameof(CanClear));
    }

    public bool CanClear { get; private set; }
}

/// <summary>The keys one installed plugin asks for. Grouping by plugin is how somebody finds theirs.</summary>
public sealed class KeyGroup(string plugin, IEnumerable<KeyRow> rows) : ViewModelBase
{
    public string Plugin { get; } = plugin;
    public ObservableCollection<KeyRow> Keys { get; } = new(rows);
}
