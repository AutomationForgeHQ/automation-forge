using System.Diagnostics;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.Input;
using Forge.Core;
using Forge.Core.Catalogue;
using Forge.Core.Machine;
using Forge.Core.Projects;

namespace Forge.Hub.ViewModels;

/// <summary>The checklist's view of this machine: the Windows vault, Docker, and the projects' .uproject files.</summary>
public sealed class LiveMachineFacts : IMachineFacts
{
    private DockerState? _docker;

    public bool KeySet(DeclaredKey key) => CredentialVault.Find(key) != KeySource.None;

    /// <summary>Asked once per check: one `docker` call, however many engines ask.</summary>
    public DockerState Docker() => _docker ??= DockerCli.Probe();

    public ContainerState Runner(DeclaredRunner runner) => DockerCli.Container(runner.ComposeProject, runner.Service, out _);

    public bool? ProjectEnabled(string uproject, string plugin)
    {
        try { return new ProjectDescriptor(uproject).IsEnabled(plugin) == true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException or InvalidOperationException) { return null; }
    }
}

/// <summary>
/// Slice 2: the set-up checklist's machinery (projects, keys, the guide), Home's per-track counts, what the
/// account holds that is not installed, and the two agent actions in Settings (6.2).
/// </summary>
public partial class MainViewModel
{
    /// <summary>For the screenshot harness: a machine with fixed keys and no Docker, instead of this one.</summary>
    internal IMachineFacts? FactsOverride { get; set; }

    /// <summary>The checklist of a track on the chosen target. Runs Docker and reads files: call it off the UI thread.</summary>
    public Checklist? BuildChecklist(Track track, IMachineFacts? facts = null)
    {
        if (Catalogue is null || Target is null) return null;
        var installed = InstalledOnTarget();
        var surfaces = MachineSurface.Discover([Target.Root, Target.LegacyRoot ?? ""])
            .ToDictionary(s => s.Plugin, StringComparer.OrdinalIgnoreCase);
        var projects = HubState.Projects.Select(p => p.Path).Where(File.Exists).ToList();
        var record = HubState.Track(track.Slug, Target.Root);
        return ChecklistBuilder.Build(track, Catalogue, installed, surfaces, projects, record?.FirstResultOpened == true,
            EditorMenuName(), facts ?? FactsOverride ?? new LiveMachineFacts());
    }

    /// <summary>
    /// "Choose a project…": turn the track's installed components on in a .uproject (the edit `forge enable`
    /// makes) and remember the project. Returns the error text of 5.2, or null.
    /// </summary>
    public string? EnableTrackInProject(Track track, string uproject)
    {
        if (Catalogue is null) return null;
        var ids = ChecklistBuilder.TrackComponents(track, Catalogue, InstalledOnTarget());
        try
        {
            var project = new ProjectDescriptor(uproject);
            var changed = ids.Count(id => project.SetEnabled(id, true));
            Say(changed == 0 ? $"Already on in {project.Name}." : $"Turned on {changed} tool{(changed == 1 ? "" : "s")} in {project.Name}. Restart Unreal if it is open.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            Say($"failed: {uproject}: {ex.Message}");
            return $"Could not change {Path.GetFileName(uproject)}. It may be read-only or checked in. Nothing was turned on.";
        }
        if (!HubState.Projects.Any(p => SamePath(p.Path, uproject)))
            HubState.Projects.Add(new ProjectRecord { Path = uproject, AddedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ") });
        TrySaveState();
        return null;
    }

    /// <summary>"Browse for a .uproject…".</summary>
    public async Task<string?> PickProjectAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime { MainWindow: { } w }) return null;
        var files = await w.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a project",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Unreal project") { Patterns = ["*.uproject"] }],
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    public IReadOnlyList<string> RememberedProjects() => HubState.Projects.Select(p => p.Path).Where(File.Exists).ToList();

    /// <summary>"Open Colony_NP24": starts the project, which starts Unreal. Only ever on a click.</summary>
    public void OpenProject(string uproject)
    {
        try { Process.Start(new ProcessStartInfo { FileName = uproject, UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { Say($"Could not open {uproject}: {ex.Message}"); }
    }

    public void MarkFirstResultOpened(Track track)
    {
        if (Target is null) return;
        var record = HubState.Track(track.Slug, Target.Root);
        if (record is null) return;
        record.FirstResultOpened = true;
        TrySaveState();
    }

    public void SetChecklistHidden(Track track, bool hidden)
    {
        if (Target is null) return;
        var record = HubState.Track(track.Slug, Target.Root);
        if (record is null)
        {
            // A track inferred from an older Hub's installs: opening its checklist starts remembering it.
            if (hidden) return;
            HubState.RecordTrack(track.Slug, Target.Root, InstalledCapabilities(track).Select(c => c.Id), new Dictionary<string, IReadOnlyCollection<string>>());
            record = HubState.Track(track.Slug, Target.Root)!;
        }
        record.ChecklistHidden = hidden;
        TrySaveState();
    }

    public IReadOnlyList<Capability> InstalledCapabilities(Track track)
    {
        var installed = InstalledOnTarget();
        return Catalogue is null ? [] : track.Capabilities
            .Where(c => Catalogue.Product(c.Product)?.MainComponent is { } main && installed.ContainsKey(main)).ToList();
    }

    // ── updates, counted per product (8.1) ───────────────────────────────

    /// <summary>Installed products with a newer component on the target, without pinned and without Labs.</summary>
    public IReadOnlyList<Product> ProductUpdates()
    {
        if (Inputs() is not { } inputs || Catalogue is null) return [];
        return Catalogue.Products
            .Where(p => p.MainComponent is { } main && inputs.Installed.ContainsKey(main))
            .Where(p => TrackPlanner.HasUpdate(p, inputs) && TrackPlanner.StageOf(p, inputs) != Stage.Labs)
            .ToList();
    }

    /// <summary>Products the account holds that are not installed on this target: "NEW ON YOUR ACCOUNT".</summary>
    public IReadOnlyList<Product> OwnedNotInstalled()
    {
        if (!IsSignedIn || Catalogue is null || _manifest is null || Inputs() is not { } inputs) return [];
        var installed = InstalledOnTarget();
        // Held in full only: a paid tool held by a preview claim, with a free tier, runs as Lite and is not "Owned".
        return Catalogue.Products
            .Where(p => p.MainComponent is { } main && _owned.Contains(main) && !installed.ContainsKey(main)
                        && _manifest.Plugin(main)?.IsPaid == true && ProductFacts.Owned(p, inputs))
            .ToList();
    }

    /// <summary>Install one product with what it depends on (Home's "Install" on a row). Uses the 0.4.4 path, elevation included.</summary>
    public Task InstallProductAsync(Product product)
    {
        if (_manifest is null) return Task.CompletedTask;
        var ids = product.Plugins
            .Concat(HubState.AgentsOn ? product.AgentPlugins : [])
            .Select(id => _manifest.Plugin(id)).OfType<Core.Manifest.PluginInfo>()
            .SelectMany(_manifest.Closure).Select(p => p.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return ids.Length == 0 ? Task.CompletedTask : RunAsync(ids);
    }

    // ── agent access in Settings (6.2) ───────────────────────────────────

    /// <summary>Agent components of installed tools that are not installed, with a build for this engine.</summary>
    public IReadOnlyList<(Product Product, string Id)> MissingAgentAccess()
    {
        if (Inputs() is not { } inputs || Catalogue is null) return [];
        return Catalogue.Products
            .Where(p => p.MainComponent is { } main && inputs.Installed.ContainsKey(main))
            .SelectMany(p => p.AgentPlugins.Select(id => (p, id)))
            .Where(x => !inputs.Installed.ContainsKey(x.id) && inputs.Manifest.Plugin(x.id)?.Latest(inputs.Engine, inputs.Channel) is not null)
            .Where(x => inputs.Manifest.Plugin(x.id)!.Dependencies.All(d => inputs.Installed.ContainsKey(d)))
            .ToList();
    }

    public IReadOnlyList<string> InstalledAgentAccess()
    {
        if (_manifest is null) return [];
        return InstalledOnTarget().Keys.Where(id => _manifest.Plugin(id)?.Role == "toolset").ToList();
    }

    public bool ShowAddAgentAccess => HubState.AgentsOn && MissingAgentAccess().Count > 0;
    public bool ShowRemoveAgentAccess => !HubState.AgentsOn && InstalledAgentAccess().Count > 0;

    public string AddAgentAccessLine
    {
        get
        {
            var n = MissingAgentAccess().Select(x => x.Product.Slug).Distinct().Count();
            return n == 1 ? "1 installed tool has agent access you have not installed." : $"{n} installed tools have agent access you have not installed.";
        }
    }

    public string RemoveAgentAccessLabel
    {
        get
        {
            var n = InstalledAgentAccess().Count;
            return $"Remove agent access from {Words.Count(n, "tool")}";
        }
    }

    [RelayCommand]
    private Task AddAgentAccess()
    {
        var ids = MissingAgentAccess().Select(x => x.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return ids.Length == 0 ? Task.CompletedTask : RunAsync(ids);
    }

    [RelayCommand]
    private Task RemoveAgentAccess()
    {
        var ids = InstalledAgentAccess().ToArray();
        return ids.Length == 0 ? Task.CompletedTask : RunAsync(ids, uninstall: true);
    }

    private void AnnounceAgentActions()
    {
        OnPropertyChanged(nameof(ShowAddAgentAccess));
        OnPropertyChanged(nameof(ShowRemoveAgentAccess));
        OnPropertyChanged(nameof(AddAgentAccessLine));
        OnPropertyChanged(nameof(RemoveAgentAccessLabel));
    }
}
