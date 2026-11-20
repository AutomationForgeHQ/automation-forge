using Xunit;
using System.Text;
using Forge.Core.Projects;

namespace Forge.Core.Tests;

/// <summary>
/// The .uproject edit must be the edit a person would make by hand: one line for a switch, the lines of one entry
/// for a new one, and every other byte as it was (tabs, CRLF, BOM or none, key order, trailing newline).
/// </summary>
public sealed class ProjectDescriptorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kovati-uproject-tests", Guid.NewGuid().ToString("N"));

    public ProjectDescriptorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var f in Directory.GetFiles(_dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(_dir, recursive: true);
    }

    /// <summary>What Unreal 5.8 writes for a C++ project, CRLF and tabs, as Colony_NP24.uproject is laid out.</summary>
    private const string Unreal = "{\r\n" +
        "\t\"FileVersion\": 3,\r\n" +
        "\t\"EngineAssociation\": \"5.8\",\r\n" +
        "\t\"Category\": \"\",\r\n" +
        "\t\"Description\": \"\",\r\n" +
        "\t\"Modules\": [\r\n" +
        "\t\t{\r\n" +
        "\t\t\t\"Name\": \"MyGame\",\r\n" +
        "\t\t\t\"Type\": \"Runtime\",\r\n" +
        "\t\t\t\"LoadingPhase\": \"Default\"\r\n" +
        "\t\t}\r\n" +
        "\t],\r\n" +
        "\t\"Plugins\": [\r\n" +
        "\t\t{\r\n" +
        "\t\t\t\"Name\": \"ModelingToolsEditorMode\",\r\n" +
        "\t\t\t\"Enabled\": true,\r\n" +
        "\t\t\t\"TargetAllowList\": [\r\n" +
        "\t\t\t\t\"Editor\"\r\n" +
        "\t\t\t]\r\n" +
        "\t\t},\r\n" +
        "\t\t{\r\n" +
        "\t\t\t\"Name\": \"MotionForge\",\r\n" +
        "\t\t\t\"Enabled\": true\r\n" +
        "\t\t}\r\n" +
        "\t]\r\n" +
        "}";

    private string Write(string text, bool bom = false, string name = "MyGame.uproject")
    {
        var path = Path.Combine(_dir, name);
        var bytes = Encoding.UTF8.GetBytes(text);
        File.WriteAllBytes(path, bom ? [0xEF, 0xBB, 0xBF, .. bytes] : bytes);
        return path;
    }

    private static string[] Lines(string text) => text.Split("\r\n");

    /// <summary>The lines of <paramref name="after"/> that are not in <paramref name="before"/>, and the reverse, by position-free comparison.</summary>
    private static (List<string> added, List<string> removed) Diff(string before, string after)
    {
        var a = Lines(before).ToList();
        var b = Lines(after).ToList();
        // A longest common subsequence: what a line diff would show.
        var n = a.Count; var m = b.Count;
        var lcs = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
            for (var j = m - 1; j >= 0; j--)
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
        var added = new List<string>(); var removed = new List<string>();
        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (a[x] == b[y]) { x++; y++; }
            else if (lcs[x + 1, y] >= lcs[x, y + 1]) removed.Add(a[x++]);
            else added.Add(b[y++]);
        }
        while (x < n) removed.Add(a[x++]);
        while (y < m) added.Add(b[y++]);
        return (added, removed);
    }

    [Fact]
    public void Turning_a_tool_off_changes_one_line_and_nothing_else()
    {
        var path = Write(Unreal);
        var d = new ProjectDescriptor(path);
        Assert.True(d.SetEnabled("MotionForge", false));
        var after = File.ReadAllText(path);
        var (added, removed) = Diff(Unreal, after);
        Assert.Equal(["\t\t\t\"Enabled\": true"], removed);
        Assert.Equal(["\t\t\t\"Enabled\": false"], added);
        Assert.False(new ProjectDescriptor(path).IsEnabled("MotionForge"));
        Assert.Equal(Unreal.Replace("\"MotionForge\",\r\n\t\t\t\"Enabled\": true", "\"MotionForge\",\r\n\t\t\t\"Enabled\": false"), after);
    }

    [Fact]
    public void Turning_a_new_tool_on_adds_only_its_entry_in_the_same_shape()
    {
        var path = Write(Unreal);
        Assert.True(new ProjectDescriptor(path).SetEnabled("MontageForge", true));
        var after = File.ReadAllText(path);
        Assert.Equal(Unreal.Replace("\"Enabled\": true\r\n\t\t}\r\n\t]",
            "\"Enabled\": true\r\n\t\t},\r\n\t\t{\r\n\t\t\t\"Name\": \"MontageForge\",\r\n\t\t\t\"Enabled\": true\r\n\t\t}\r\n\t]"), after);
        var (added, removed) = Diff(Unreal, after);
        // Nothing but the new entry and the comma it needs: four lines added, nothing taken away.
        Assert.Empty(removed);
        Assert.Equal(["\t\t},", "\t\t{", "\t\t\t\"Name\": \"MontageForge\",", "\t\t\t\"Enabled\": true"], added);
        Assert.DoesNotContain("\n", after.Replace("\r\n", ""));      // every line ending is still CRLF
        Assert.True(new ProjectDescriptor(path).IsEnabled("MontageForge"));
        Assert.True(new ProjectDescriptor(path).IsEnabled("ModelingToolsEditorMode"));
    }

    [Fact]
    public void A_file_that_already_says_so_is_not_written()
    {
        var path = Write(Unreal);
        var before = File.GetLastWriteTimeUtc(path);
        File.SetLastWriteTimeUtc(path, before.AddMinutes(-5));
        Assert.False(new ProjectDescriptor(path).SetEnabled("MotionForge", true));
        Assert.Equal(before.AddMinutes(-5), File.GetLastWriteTimeUtc(path));
        Assert.Equal(Unreal, File.ReadAllText(path));
    }

    [Fact]
    public void The_byte_order_mark_and_the_trailing_newline_are_kept()
    {
        var withBom = Write(Unreal + "\r\n", bom: true, name: "Bom.uproject");
        new ProjectDescriptor(withBom).SetEnabled("MotionForge", false);
        var bytes = File.ReadAllBytes(withBom);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        Assert.EndsWith("}\r\n", Encoding.UTF8.GetString(bytes[3..]));

        var noBom = Write(Unreal, name: "NoBom.uproject");
        new ProjectDescriptor(noBom).SetEnabled("MotionForge", false);
        var raw = File.ReadAllBytes(noBom);
        Assert.Equal((byte)'{', raw[0]);
        Assert.EndsWith("}", Encoding.UTF8.GetString(raw));
    }

    [Fact]
    public void An_entry_without_Enabled_gets_it_right_after_its_name()
    {
        var text = Unreal.Replace("\"Name\": \"MotionForge\",\r\n\t\t\t\"Enabled\": true", "\"Name\": \"MotionForge\"");
        var path = Write(text);
        new ProjectDescriptor(path).SetEnabled("MotionForge", true);
        var (added, removed) = Diff(text, File.ReadAllText(path));
        Assert.Equal(["\t\t\t\"Name\": \"MotionForge\""], removed);
        Assert.Equal(["\t\t\t\"Name\": \"MotionForge\",", "\t\t\t\"Enabled\": true"], added);
    }

    [Fact]
    public void An_empty_Plugins_array_and_a_missing_one_open_the_way_Unreal_writes_them()
    {
        var empty = Write("{\r\n\t\"FileVersion\": 3,\r\n\t\"EngineAssociation\": \"5.8\",\r\n\t\"Plugins\": []\r\n}\r\n", name: "Empty.uproject");
        new ProjectDescriptor(empty).SetEnabled("MotionForge", true);
        Assert.Equal("{\r\n\t\"FileVersion\": 3,\r\n\t\"EngineAssociation\": \"5.8\",\r\n\t\"Plugins\": [\r\n\t\t{\r\n\t\t\t\"Name\": \"MotionForge\",\r\n\t\t\t\"Enabled\": true\r\n\t\t}\r\n\t]\r\n}\r\n",
            File.ReadAllText(empty));

        var none = Write("{\r\n\t\"FileVersion\": 3,\r\n\t\"EngineAssociation\": \"5.8\"\r\n}", name: "None.uproject");
        new ProjectDescriptor(none).SetEnabled("MotionForge", true);
        Assert.Equal("{\r\n\t\"FileVersion\": 3,\r\n\t\"EngineAssociation\": \"5.8\",\r\n\t\"Plugins\": [\r\n\t\t{\r\n\t\t\t\"Name\": \"MotionForge\",\r\n\t\t\t\"Enabled\": true\r\n\t\t}\r\n\t]\r\n}",
            File.ReadAllText(none));
    }

    [Fact]
    public void A_file_with_spaces_and_LF_stays_spaces_and_LF()
    {
        var text = "{\n  \"FileVersion\": 3,\n  \"Plugins\": [\n    {\n      \"Name\": \"A\",\n      \"Enabled\": true\n    }\n  ]\n}\n";
        var path = Write(text, name: "Spaces.uproject");
        new ProjectDescriptor(path).SetEnabled("B", true);
        Assert.Equal("{\n  \"FileVersion\": 3,\n  \"Plugins\": [\n    {\n      \"Name\": \"A\",\n      \"Enabled\": true\n    },\n    {\n      \"Name\": \"B\",\n      \"Enabled\": true\n    }\n  ]\n}\n",
            File.ReadAllText(path));
    }

    [Fact]
    public void A_one_line_file_gets_a_one_line_entry()
    {
        var text = """{ "FileVersion": 3, "EngineAssociation": "5.8", "Plugins": [ { "Name": "ModelingToolsEditorMode", "Enabled": true } ] }""";
        var path = Write(text, name: "Compact.uproject");
        new ProjectDescriptor(path).SetEnabled("MotionForge", true);
        Assert.Equal("""{ "FileVersion": 3, "EngineAssociation": "5.8", "Plugins": [ { "Name": "ModelingToolsEditorMode", "Enabled": true }, { "Name": "MotionForge", "Enabled": true } ] }""",
            File.ReadAllText(path));
    }

    [Fact]
    public void A_read_only_project_is_refused_and_left_as_it_is()
    {
        var path = Write(Unreal, name: "Locked.uproject");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        var d = new ProjectDescriptor(path);
        Assert.True(d.IsReadOnly);
        Assert.Throws<UnauthorizedAccessException>(() => d.SetEnabled("MotionForge", false));
        Assert.Equal(Unreal, File.ReadAllText(path));
        Assert.True(new FileInfo(path).IsReadOnly);
    }

    [Fact]
    public void It_reads_the_association_modules_and_entries()
    {
        var d = new ProjectDescriptor(Write(Unreal));
        Assert.Equal("5.8", d.EngineAssociation);
        Assert.True(d.IsCodeProject);
        Assert.Equal(["ModelingToolsEditorMode", "MotionForge"], d.Plugins.Select(p => p.Name));
        Assert.Null(d.IsEnabled("MontageForge"));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }
}
