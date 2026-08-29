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

    public KeyRow(DeclaredKey key, Action changed,
        IReadOnlyList<string>? consumers = null, IReadOnlyList<DeclaredAccess>? access = null)
    {
        _key = key;
        _changed = changed;
        Consumers = consumers ?? [];
        Access = (access ?? []).Select(a => new AccessRow(a)).ToList();
        Reread();
    }

    /// <summary>Which installed plugins use this key. Only meaningful for a general one.</summary>
    public IReadOnlyList<string> Consumers { get; }

    public string ConsumersLine => Consumers.Count == 0 ? "" : $"Used by {string.Join(", ", Consumers)}";
    public bool HasConsumers => Consumers.Count > 0;

    /// <summary>What this key must additionally be granted, per plugin that needs a grant.</summary>
    public IReadOnlyList<AccessRow> Access { get; }

    public bool HasAccess => Access.Count > 0;

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

/// <summary>
/// One general key as several plugins declared it.
///
/// The first declaration supplies the row - id, vault entry, where to get one. What every
/// declaration adds is who wants it and what must additionally be granted, because a shared token can
/// front two entirely unrelated approvals: Meta review Llama 3 by hand and take days, NVIDIA's terms
/// are one click. Showing only the first plugin's would leave the other invisible.
///
/// Required beats optional. A key one plugin can live without and another cannot is a key this
/// machine needs, and drawing it as optional tells half the truth to whichever half of the user is
/// about to be stuck.
/// </summary>
public sealed class MergedKey(DeclaredKey first)
{
    public DeclaredKey Key { get; private set; } = first;
    public List<string> Consumers { get; } = [];
    public List<DeclaredAccess> Access { get; } = [];

    public void Absorb(DeclaredKey key, string plugin)
    {
        var who = string.IsNullOrWhiteSpace(key.ConsumedBy) ? plugin : key.ConsumedBy;
        if (!Consumers.Contains(who)) Consumers.Add(who);

        if (key.RequiresAccess is { } grant && !Access.Any(a => a.Url == grant.Url))
        {
            Access.Add(grant);
        }

        if (!key.Optional && Key.Optional)
        {
            Key = Key with { Optional = false };
        }
    }
}

/// <summary>One gated resource a key must be granted access to, as the card draws it.</summary>
public sealed partial class AccessRow(DeclaredAccess access) : ViewModelBase
{
    public string What => string.IsNullOrWhiteSpace(access.Model) ? access.Url : access.Model;
    public string Note => access.Note;
    public bool HasNote => !string.IsNullOrWhiteSpace(access.Note);

    /// <summary>Reviewed by a human, so it is worth starting before anything else.</summary>
    public bool ByHand => access.IsReviewedByHand;

    public string ActionLabel => ByHand ? "Request access" : "Accept the terms";
    public string Timing => ByHand ? "reviewed by a person - start it early" : "granted immediately";

    [RelayCommand]
    private void Open()
    {
        if (!string.IsNullOrWhiteSpace(access.Url))
        {
            Process.Start(new ProcessStartInfo(access.Url) { UseShellExecute = true });
        }
    }
}
