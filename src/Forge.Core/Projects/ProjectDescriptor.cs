using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Forge.Core.Projects;

/// <summary>One entry of a .uproject's Plugins array.</summary>
public sealed record PluginEntry(string Name, bool? Enabled);

/// <summary>
/// A .uproject: read through System.Text.Json, and changed by editing its text in place, the one token or the one
/// entry that changes and nothing else. Unreal writes the file with tabs and CRLF, and a project is usually under
/// source control: turning one tool on must be a one-line diff (an added entry is the lines of that entry), never a
/// reformatted file. The byte-order mark, the line endings, the indentation, the key order and the trailing newline
/// are kept as they were. The new text is written to a file beside it and moved over it.
/// </summary>
public sealed class ProjectDescriptor
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];
    private static readonly JsonDocumentOptions ReadOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private JsonObject _root = new();
    private byte[] _bytes = [];
    private bool _bom;

    public string Path { get; }
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
    public string Directory => System.IO.Path.GetDirectoryName(Path)!;

    public ProjectDescriptor(string uprojectOrDir)
    {
        Path = File.Exists(uprojectOrDir)
            ? System.IO.Path.GetFullPath(uprojectOrDir)
            : System.IO.Directory.GetFiles(uprojectOrDir, "*.uproject").FirstOrDefault()
              ?? throw new FileNotFoundException($"No .uproject in {uprojectOrDir}");
        Load(File.ReadAllBytes(Path));
    }

    private void Load(byte[] raw)
    {
        _bom = raw.AsSpan().StartsWith(Bom);
        _bytes = _bom ? raw[3..] : raw;
        _root = JsonNode.Parse(_bytes, documentOptions: ReadOptions)?.AsObject() ?? throw new InvalidDataException($"{Path} is not a JSON object.");
    }

    public string? EngineAssociation => _root["EngineAssociation"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>A C++ project: it lists modules of its own, so it is rebuilt after its plugins change.</summary>
    public bool IsCodeProject => _root["Modules"] is JsonArray { Count: > 0 };

    /// <summary>The .uproject is read-only (a Perforce checkout that is not open, a lock): never cleared by Kovati Hub.</summary>
    public bool IsReadOnly => new FileInfo(Path).IsReadOnly;

    public IReadOnlyList<PluginEntry> Plugins =>
        (_root["Plugins"] as JsonArray ?? []).OfType<JsonObject>()
        .Select(o => new PluginEntry(Str(o["Name"]) ?? "", Bool(o["Enabled"])))
        .Where(e => e.Name.Length > 0)
        .ToList();

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    private static bool? Bool(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    /// <summary>The explicit entry's Enabled, or null when the project does not name the plugin.</summary>
    public bool? IsEnabled(string plugin) =>
        Plugins.FirstOrDefault(e => string.Equals(e.Name, plugin, StringComparison.OrdinalIgnoreCase))?.Enabled;

    /// <summary>
    /// Turn a plugin on or off in the project, the edit the editor's Plugins window makes. Returns true when the
    /// file changed. Throws <see cref="UnauthorizedAccessException"/> for a read-only file (it is not cleared).
    /// </summary>
    public bool SetEnabled(string plugin, bool enabled)
    {
        var text = Edit(_bytes, plugin, enabled);
        if (text is null) return false;
        if (IsReadOnly)
            throw new UnauthorizedAccessException($"{System.IO.Path.GetFileName(Path)} is read-only. Check it out or make it writable, then try again.");
        var output = _bom ? [.. Bom, .. text] : text;
        WriteReplacing(Path, output);
        Load(output);
        return true;
    }

    /// <summary>Write beside the file, then move over it: a cut-short write never leaves half a .uproject.</summary>
    private static void WriteReplacing(string path, byte[] bytes)
    {
        var temp = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, $".{System.IO.Path.GetFileName(path)}.kovati-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    // ── the text edit ────────────────────────────────────────────────────

    /// <summary>Where things are in the file, as byte offsets into the UTF-8 text without its BOM.</summary>
    private sealed class Layout
    {
        public int RootStart = -1, RootEnd = -1, LastRootValueEnd = -1;
        public int PluginsPropStart = -1, ArrayStart = -1, ArrayEnd = -1;
        public List<EntryLayout> Entries { get; } = [];
    }

    private sealed class EntryLayout
    {
        public int Start, End;                        // '{' and '}'
        public string? Name;
        public int NamePropStart = -1, NameValueEnd = -1;
        public int EnabledStart = -1, EnabledLength;  // the literal true / false
        public int FirstPropStart = -1;
    }

    /// <summary>The new text, or null when the file already says so.</summary>
    internal static byte[]? Edit(byte[] json, string plugin, bool enabled)
    {
        var l = Scan(json);
        var literal = enabled ? "true" : "false";
        var nl = DetectNewline(json);
        var unit = DetectIndentUnit(json);
        var entry = l.Entries.FirstOrDefault(e => string.Equals(e.Name, plugin, StringComparison.OrdinalIgnoreCase));

        if (entry is not null && entry.EnabledStart >= 0)
        {
            var current = Encoding.UTF8.GetString(json, entry.EnabledStart, entry.EnabledLength);
            if (current == literal) return null;
            return Splice(json, entry.EnabledStart, entry.EnabledLength, literal);
        }

        if (entry is not null)
        {
            // The entry names the plugin and says nothing of Enabled: one property after its Name.
            var inline = !HasNewline(json, entry.Start, entry.NamePropStart);
            var insert = inline
                ? $", \"Enabled\": {literal}"
                : $",{nl}{IndentOf(json, entry.NamePropStart)}\"Enabled\": {literal}";
            return Splice(json, entry.NameValueEnd, 0, insert);
        }

        if (l.ArrayStart >= 0)
        {
            var arrayIndent = IndentOf(json, l.PluginsPropStart);
            if (l.Entries.Count > 0)
            {
                // After the last entry, in its shape: the same indents, one line per property, or one line in all.
                var last = l.Entries[^1];
                var multiline = last.FirstPropStart >= 0 && HasNewline(json, last.Start, last.FirstPropStart);
                var entryIndent = HasNewline(json, l.ArrayStart, last.Start) ? IndentOf(json, last.Start) : "";
                var propIndent = multiline ? IndentOf(json, last.FirstPropStart) : entryIndent + unit;
                var between = l.Entries.Count >= 2
                    ? Encoding.UTF8.GetString(json, CommaAfter(json, l.Entries[^2].End) + 1, last.Start - (CommaAfter(json, l.Entries[^2].End) + 1))
                    : entryIndent.Length > 0 ? nl + entryIndent : " ";
                var body = multiline
                    ? $"{{{nl}{propIndent}\"Name\": \"{Escape(plugin)}\",{nl}{propIndent}\"Enabled\": {literal}{nl}{entryIndent}}}"
                    : $"{{ \"Name\": \"{Escape(plugin)}\", \"Enabled\": {literal} }}";
                return Splice(json, last.End + 1, 0, "," + between + body);
            }
            {
                // An empty array: open it the way Unreal writes one.
                var entryIndent = arrayIndent + unit;
                var propIndent = entryIndent + unit;
                var body = $"[{nl}{entryIndent}{{{nl}{propIndent}\"Name\": \"{Escape(plugin)}\",{nl}{propIndent}\"Enabled\": {literal}{nl}{entryIndent}}}{nl}{arrayIndent}]";
                return Splice(json, l.ArrayStart, l.ArrayEnd - l.ArrayStart + 1, body);
            }
        }

        if (l.RootStart < 0) throw new InvalidDataException("The project file is not a JSON object.");
        {
            // No Plugins at all: a new last property of the root object.
            var p1 = unit;
            var p2 = unit + unit;
            var p3 = unit + unit + unit;
            var block = $"\"Plugins\": [{nl}{p2}{{{nl}{p3}\"Name\": \"{Escape(plugin)}\",{nl}{p3}\"Enabled\": {literal}{nl}{p2}}}{nl}{p1}]";
            return l.LastRootValueEnd >= 0
                ? Splice(json, l.LastRootValueEnd, 0, $",{nl}{p1}{block}")
                : Splice(json, l.RootStart + 1, 0, $"{nl}{p1}{block}{nl}");
        }
    }

    private static Layout Scan(byte[] json)
    {
        var l = new Layout();
        var r = new Utf8JsonReader(json, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (!r.Read() || r.TokenType != JsonTokenType.StartObject) return l;
        l.RootStart = (int)r.TokenStartIndex;
        while (r.Read())
        {
            if (r.TokenType == JsonTokenType.EndObject) { l.RootEnd = (int)r.TokenStartIndex; break; }
            if (r.TokenType != JsonTokenType.PropertyName) continue;
            var propStart = (int)r.TokenStartIndex;
            var name = r.GetString();
            r.Read();
            if (name == "Plugins" && r.TokenType == JsonTokenType.StartArray && l.ArrayStart < 0)
            {
                l.PluginsPropStart = propStart;
                l.ArrayStart = (int)r.TokenStartIndex;
                while (r.Read() && r.TokenType != JsonTokenType.EndArray)
                {
                    if (r.TokenType != JsonTokenType.StartObject) { r.Skip(); continue; }
                    var e = new EntryLayout { Start = (int)r.TokenStartIndex };
                    while (r.Read() && r.TokenType != JsonTokenType.EndObject)
                    {
                        if (r.TokenType != JsonTokenType.PropertyName) { r.Skip(); continue; }
                        var pStart = (int)r.TokenStartIndex;
                        if (e.FirstPropStart < 0) e.FirstPropStart = pStart;
                        var key = r.GetString();
                        r.Read();
                        if (key == "Name" && r.TokenType == JsonTokenType.String)
                        {
                            e.Name = r.GetString();
                            e.NamePropStart = pStart;
                            e.NameValueEnd = (int)r.BytesConsumed;
                        }
                        else if (key == "Enabled" && r.TokenType is JsonTokenType.True or JsonTokenType.False)
                        {
                            e.EnabledStart = (int)r.TokenStartIndex;
                            e.EnabledLength = r.TokenType == JsonTokenType.True ? 4 : 5;
                        }
                        else r.Skip();
                    }
                    e.End = (int)r.TokenStartIndex;
                    l.Entries.Add(e);
                }
                l.ArrayEnd = (int)r.TokenStartIndex;
            }
            else r.Skip();
            l.LastRootValueEnd = (int)r.BytesConsumed;
        }
        return l;
    }

    private static byte[] Splice(byte[] json, int at, int remove, string insert)
    {
        var add = Utf8NoBom.GetBytes(insert);
        var result = new byte[json.Length - remove + add.Length];
        json.AsSpan(0, at).CopyTo(result);
        add.CopyTo(result.AsSpan(at));
        json.AsSpan(at + remove).CopyTo(result.AsSpan(at + add.Length));
        return result;
    }

    private static int CommaAfter(byte[] json, int from)
    {
        for (var i = from + 1; i < json.Length; i++)
            if (json[i] == (byte)',') return i;
        return from;
    }

    private static bool HasNewline(byte[] json, int from, int to)
    {
        for (var i = Math.Max(0, from); i < Math.Min(to, json.Length); i++)
            if (json[i] == (byte)'\n') return true;
        return false;
    }

    /// <summary>The whitespace from the start of the line to <paramref name="at"/>.</summary>
    private static string IndentOf(byte[] json, int at)
    {
        if (at < 0) return "";
        var start = at;
        while (start > 0 && json[start - 1] is (byte)' ' or (byte)'\t') start--;
        if (start > 0 && json[start - 1] is not ((byte)'\n' or (byte)'\r')) return "";
        return Encoding.UTF8.GetString(json, start, at - start);
    }

    private static string DetectNewline(byte[] json)
    {
        for (var i = 0; i < json.Length; i++)
            if (json[i] == (byte)'\n') return i > 0 && json[i - 1] == (byte)'\r' ? "\r\n" : "\n";
        return Environment.NewLine == "\r\n" ? "\r\n" : "\n";
    }

    /// <summary>The first indented line's indent: a tab, or that many spaces. A tab when nothing is indented.</summary>
    private static string DetectIndentUnit(byte[] json)
    {
        for (var i = 0; i < json.Length - 1; i++)
        {
            if (json[i] != (byte)'\n') continue;
            if (json[i + 1] == (byte)'\t') return "\t";
            var n = 0;
            while (i + 1 + n < json.Length && json[i + 1 + n] == (byte)' ') n++;
            if (n > 0) return new string(' ', n);
        }
        return "\t";
    }

    private static string Escape(string s) => JsonEncodedText.Encode(s).ToString();
}
