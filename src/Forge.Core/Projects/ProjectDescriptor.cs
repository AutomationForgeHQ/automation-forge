using System.Text.Json;
using System.Text.Json.Nodes;

namespace Forge.Core.Projects;

/// <summary>
/// The .uproject's Plugins array, edited in place and nothing else touched —
/// the same edit the editor's Plugins window makes. Enabling here is what makes
/// an engine-installed plugin part of a project.
/// </summary>
public sealed class ProjectDescriptor
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly JsonObject _root;

    public string Path { get; }
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);

    public ProjectDescriptor(string uprojectOrDir)
    {
        Path = File.Exists(uprojectOrDir)
            ? System.IO.Path.GetFullPath(uprojectOrDir)
            : Directory.GetFiles(uprojectOrDir, "*.uproject").FirstOrDefault()
              ?? throw new FileNotFoundException($"No .uproject in {uprojectOrDir}");
        _root = JsonNode.Parse(File.ReadAllText(Path))?.AsObject() ?? throw new InvalidDataException($"{Path} is not a JSON object.");
    }

    public string? EngineAssociation => _root["EngineAssociation"]?.GetValue<string>();

    private JsonArray Plugins => _root["Plugins"] as JsonArray ?? (JsonArray)(_root["Plugins"] = new JsonArray());

    private JsonObject? Entry(string plugin) =>
        Plugins.OfType<JsonObject>().FirstOrDefault(o => string.Equals(o["Name"]?.GetValue<string>(), plugin, StringComparison.OrdinalIgnoreCase));

    public bool? IsEnabled(string plugin) => Entry(plugin)?["Enabled"]?.GetValue<bool>();

    /// <summary>Returns true when the file changed.</summary>
    public bool SetEnabled(string plugin, bool enabled)
    {
        var entry = Entry(plugin);
        if (entry is null)
        {
            Plugins.Add(new JsonObject { ["Name"] = plugin, ["Enabled"] = enabled });
        }
        else
        {
            if (entry["Enabled"]?.GetValue<bool>() == enabled) return false;
            entry["Enabled"] = enabled;
        }
        File.WriteAllText(Path, _root.ToJsonString(Json) + Environment.NewLine);
        return true;
    }
}
