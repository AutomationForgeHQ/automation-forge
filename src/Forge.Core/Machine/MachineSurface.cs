using System.Text.Json;
using System.Text.Json.Serialization;

namespace Forge.Core.Machine;

/// <summary>
/// One API key a plugin asks for, as the plugin declares it.
///
/// Exactly the static half of the editor's own FForgeKeyProvider. The other half is closures over
/// the plugin's credential store, which cannot cross a process boundary — so the hub reads the
/// metadata from here and performs the operations itself against the same vault entry.
/// </summary>
public sealed record DeclaredKey
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("displayName")] public string DisplayName { get; init; } = "";
    [JsonPropertyName("owner")] public string Owner { get; init; } = "";
    [JsonPropertyName("purpose")] public string Purpose { get; init; } = "";

    /// <summary>True when the plugin works without it. An unset optional key is a fact, not a fault.</summary>
    [JsonPropertyName("optional")] public bool Optional { get; init; }

    [JsonPropertyName("helpUrl")] public string? HelpUrl { get; init; }

    /// <summary>The Windows Credential Manager target. This is what makes the two surfaces agree.</summary>
    [JsonPropertyName("vaultEntry")] public string VaultEntry { get; init; } = "";

    /// <summary>Consulted when the vault has nothing, exactly as the plugin does.</summary>
    [JsonPropertyName("environmentVariable")] public string? EnvironmentVariable { get; init; }

    /// <summary>
    /// "general" for a key that belongs to a person rather than to a plugin.
    ///
    /// A Runpod key and a Hugging Face token are accounts somebody has. Two plugins asking for the
    /// same one are asking the same question twice, and answering it twice leaves two copies to keep
    /// in step. General keys are shown once, under General, with the plugins that use them named.
    /// </summary>
    [JsonPropertyName("scope")] public string Scope { get; init; } = "";

    /// <summary>Which plugin declared this. Accumulated across declarations for a general key.</summary>
    [JsonPropertyName("consumedBy")] public string ConsumedBy { get; init; } = "";

    /// <summary>
    /// A gated resource this key must additionally be granted access to, or null.
    ///
    /// A token is not access. A valid token whose account has not accepted a model's terms installs
    /// perfectly and fails at the point of use, nowhere near the key that looked correct.
    /// </summary>
    [JsonPropertyName("requiresAccess")] public DeclaredAccess? RequiresAccess { get; init; }

    public bool IsGeneral => string.Equals(Scope, "general", StringComparison.OrdinalIgnoreCase);
}

/// <summary>What a key must be granted access to, beyond existing.</summary>
public sealed record DeclaredAccess
{
    /// <summary>The gated resource, e.g. "meta-llama/Meta-Llama-3-8B-Instruct".</summary>
    [JsonPropertyName("model")] public string Model { get; init; } = "";

    [JsonPropertyName("url")] public string Url { get; init; } = "";

    /// <summary>"manual" when a human reviews it - worth starting early - or "automatic".</summary>
    [JsonPropertyName("review")] public string Review { get; init; } = "";

    [JsonPropertyName("note")] public string Note { get; init; } = "";

    public bool IsReviewedByHand => string.Equals(Review, "manual", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Something a plugin can start and stop on this machine. Read by the Runners tab.</summary>
public sealed record DeclaredRunner
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("displayName")] public string DisplayName { get; init; } = "";

    /// <summary>Relative to the plugin folder. The editor uses it; the hub only reports it.</summary>
    [JsonPropertyName("compose")] public string Compose { get; init; } = "";

    /// <summary>
    /// The compose project name, which is what the hub finds the container by.
    ///
    /// Needed because the hub cannot run compose - the file interpolates variables only the plugin
    /// knows - so it looks for the labels compose stamps on the container instead.
    /// </summary>
    [JsonPropertyName("composeProject")] public string ComposeProject { get; init; } = "";

    [JsonPropertyName("service")] public string Service { get; init; } = "";
    [JsonPropertyName("image")] public string Image { get; init; } = "";
    [JsonPropertyName("signatureLabel")] public string? SignatureLabel { get; init; }
    [JsonPropertyName("health")] public string? Health { get; init; }

    /// <summary>Set when this runner can also be rented rather than run locally.</summary>
    [JsonPropertyName("cloud")] public DeclaredCloud? Cloud { get; init; }
}

/// <summary>
/// Where a runner can be rented, and which key pays for it.
///
/// Enough for the hub to show what is running and stop it. **Choosing** a machine is not here on
/// purpose: which card, in which region, under what price ceiling, is a decision made against what
/// the model needs, and it is made in the editor where those facts live.
/// </summary>
public sealed record DeclaredCloud
{
    [JsonPropertyName("provider")] public string Provider { get; init; } = "";

    /// <summary>The id of a key declared above - the account this rents on.</summary>
    [JsonPropertyName("keyId")] public string KeyId { get; init; } = "";

    /// <summary>What the editor names the pods it creates, so ours can be told from anything else.</summary>
    [JsonPropertyName("podName")] public string PodName { get; init; } = "";
}

/// <summary>The file itself: Config/ForgeMachine.json inside a plugin.</summary>
internal sealed record MachineFile
{
    [JsonPropertyName("keys")] public List<DeclaredKey> Keys { get; init; } = [];
    [JsonPropertyName("runners")] public List<DeclaredRunner> Runners { get; init; } = [];
}

/// <summary>What one installed plugin declares it needs from this machine.</summary>
public sealed record PluginSurface(
    string Plugin,
    string PluginDir,
    IReadOnlyList<DeclaredKey> Keys,
    IReadOnlyList<DeclaredRunner> Runners)
{
    public bool HasAnything => Keys.Count > 0 || Runners.Count > 0;
}

/// <summary>
/// What the installed plugins say they need: API keys, and things that run.
///
/// **The hub cannot ask a plugin anything.** It is a separate process that may run with no editor
/// open at all, which is the point of it — both of these are things somebody does before opening
/// Unreal, or after it has failed to start. So plugins declare, and the hub reads.
///
/// A plugin with no Config/ForgeMachine.json declares nothing and appears nowhere. Nothing here is
/// hard-coded, so a plugin written next year needs no change to this file.
/// </summary>
public static class MachineSurface
{
    public const string FileName = "ForgeMachine.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Every declaration under the given plugin roots, in plugin-name order.
    ///
    /// A root is an engine's Engine/Plugins/AutomationForge — which is the whole of what the hub can
    /// see. A plugin dropped by hand into one project's Plugins folder is invisible here, and the
    /// surfaces built on this have to say so where somebody would otherwise wait for it to appear.
    /// </summary>
    public static IReadOnlyList<PluginSurface> Discover(IEnumerable<string> pluginRoots)
    {
        var found = new Dictionary<string, PluginSurface>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in pluginRoots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;

            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var file = Path.Combine(dir, "Config", FileName);
                if (!File.Exists(file)) continue;

                var plugin = Path.GetFileName(dir);

                // First engine wins. The same plugin installed into two engines declares the same
                // keys, and the vault is machine-wide, so listing it twice would be one key drawn
                // as two - and setting one would silently set the other.
                if (found.ContainsKey(plugin)) continue;

                if (Read(plugin, dir, file) is { } surface && surface.HasAnything)
                {
                    found[plugin] = surface;
                }
            }
        }

        return found.Values.OrderBy(s => s.Plugin, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static PluginSurface? Read(string plugin, string dir, string file)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<MachineFile>(File.ReadAllText(file), Options);
            if (parsed is null) return null;

            // A key with no vault entry cannot be set or read, and drawing it would offer a control
            // that does nothing. Same for a runner with no compose file.
            var keys = parsed.Keys
                .Where(k => !string.IsNullOrWhiteSpace(k.Id) && !string.IsNullOrWhiteSpace(k.VaultEntry))
                .ToList();

            // Without a compose project there is no way to find the container, and a runner row
            // that cannot report a state is a row that can only mislead.
            var runners = parsed.Runners
                .Where(r => !string.IsNullOrWhiteSpace(r.Id) && !string.IsNullOrWhiteSpace(r.ComposeProject))
                .ToList();

            return new PluginSurface(plugin, dir, keys, runners);
        }
        catch (Exception)
        {
            // A malformed declaration is the plugin's problem and must not take the hub down with
            // it. It simply declares nothing.
            return null;
        }
    }
}
