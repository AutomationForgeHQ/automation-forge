using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;

namespace Forge.Hub.Views;

/// <summary>
/// The Markdown a CHANGELOG.md section is written in, drawn in the hub's own type.
///
/// Only what the changelogs use: headings, paragraphs, bullets (nested, and wrapped across lines
/// the way the files are hard-wrapped at a hundred columns), fenced code, and inside a line bold,
/// italic, code and links. Anything else reads as the text it is, which for Markdown is legible
/// anyway - a renderer with a dependency and a theme of its own would be a second design language
/// in one window.
/// </summary>
public sealed class MarkdownBlock : StackPanel
{
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownBlock, string?>(nameof(Markdown));

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    private static readonly Regex Heading = new(@"^(#{1,6})\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex Bullet = new(@"^(\s*)([-*+]|\d+[.)])\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex Link = new(@"\G\[([^\]]+)\]\(([^)\s]+)\)", RegexOptions.Compiled);

    private enum Kind { Heading, Paragraph, Bullet, Code }

    private sealed class Block(Kind kind, string text, int level = 0, string marker = "")
    {
        public Kind Kind { get; } = kind;
        public StringBuilder Text { get; } = new(text);
        public int Level { get; } = level;
        public string Marker { get; } = marker;
    }

    public MarkdownBlock()
    {
        Spacing = 8;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MarkdownProperty) Rebuild();
    }

    private void Rebuild()
    {
        Children.Clear();
        foreach (var block in Parse(Markdown ?? ""))
        {
            Children.Add(Render(block));
        }
    }

    private static List<Block> Parse(string markdown)
    {
        var blocks = new List<Block>();
        Block? open = null;
        var fenced = false;

        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();

            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                fenced = !fenced;
                open = fenced ? new Block(Kind.Code, "") : null;
                if (fenced) blocks.Add(open!);
                continue;
            }

            if (fenced)
            {
                if (open!.Text.Length > 0) open.Text.Append('\n');
                open.Text.Append(raw);
                continue;
            }

            if (line.Length == 0)
            {
                open = null;
                continue;
            }

            if (Heading.Match(line) is { Success: true } h)
            {
                blocks.Add(new Block(Kind.Heading, h.Groups[2].Value.Trim(), h.Groups[1].Length));
                open = null;
                continue;
            }

            if (Bullet.Match(line) is { Success: true } b)
            {
                var marker = char.IsDigit(b.Groups[2].Value[0]) ? b.Groups[2].Value : "–";
                open = new Block(Kind.Bullet, b.Groups[3].Value.Trim(), Math.Min(b.Groups[1].Value.Length / 2, 3), marker);
                blocks.Add(open);
                continue;
            }

            // A line under a bullet or a paragraph continues it: the files wrap by hand.
            if (open is { Kind: Kind.Bullet or Kind.Paragraph })
            {
                open.Text.Append(' ').Append(line.Trim());
                continue;
            }

            open = new Block(Kind.Paragraph, line.Trim());
            blocks.Add(open);
        }

        return blocks;
    }

    private static Control Render(Block block)
    {
        switch (block.Kind)
        {
            case Kind.Heading when block.Level <= 2:
                return new TextBlock
                {
                    Classes = { "display" },
                    FontSize = 16,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 6, 0, 0),
                    Inlines = Inlines(block.Text.ToString()),
                };

            // "### Added", "### Fixed": the section labels the rest of the hub already uses.
            case Kind.Heading:
                return new TextBlock
                {
                    Classes = { "label" },
                    Text = block.Text.ToString().ToUpperInvariant(),
                    Margin = new Thickness(0, 8, 0, 0),
                };

            case Kind.Code:
                return new Border
                {
                    Background = Brush("InkBrush"),
                    BorderBrush = Brush("LineBrush"),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(12, 10),
                    Child = new TextBlock
                    {
                        Classes = { "mono" },
                        FontSize = 12,
                        Text = block.Text.ToString(),
                        TextWrapping = TextWrapping.Wrap,
                    },
                };

            case Kind.Bullet:
                var marker = new TextBlock
                {
                    Text = block.Marker,
                    Foreground = Brush("AmberBrush"),
                    FontFamily = Resource("MonoFont") as FontFamily ?? FontFamily.Default,
                    FontSize = 12.5,
                    LineHeight = 20,
                    Width = 18,
                    VerticalAlignment = VerticalAlignment.Top,
                };
                var text = Body(block.Text.ToString());
                Grid.SetColumn(text, 1);
                return new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                    Margin = new Thickness(block.Level * 18, 0, 0, 0),
                    Children = { marker, text },
                };

            default:
                return Body(block.Text.ToString());
        }
    }

    /// <summary>
    /// Prose in the mono face, as every other sentence in the hub is. Not Archivo: the embedded
    /// Archivo is a variable font whose default instance is weight 600, and every weight asked of it
    /// draws that instance - a paragraph of it reads as shouting, and bold inside it cannot be seen.
    /// </summary>
    private static TextBlock Body(string text) => new()
    {
        FontFamily = Resource("MonoFont") as FontFamily ?? FontFamily.Default,
        FontSize = 12.5,
        LineHeight = 20,
        TextWrapping = TextWrapping.Wrap,
        Inlines = Inlines(text),
    };

    private static InlineCollection Inlines(string text)
    {
        var inlines = new InlineCollection();
        AddInlines(inlines, text, bold: false, italic: false);
        return inlines;
    }

    /// <summary>Bold, italic, code and links, nested the way the changelogs nest them (**`x`**).</summary>
    private static void AddInlines(InlineCollection target, string text, bool bold, bool italic)
    {
        var plain = new StringBuilder();

        void Flush()
        {
            if (plain.Length == 0) return;
            target.Add(Run(plain.ToString(), bold, italic, code: false));
            plain.Clear();
        }

        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];

            if (c == '`' && text.IndexOf('`', i + 1) is var codeEnd and > 0)
            {
                Flush();
                target.Add(Run(text[(i + 1)..codeEnd], bold, italic, code: true));
                i = codeEnd + 1;
                continue;
            }

            if (c == '*' && i + 1 < text.Length && text[i + 1] == '*' && text.IndexOf("**", i + 2, StringComparison.Ordinal) is var boldEnd and > 0)
            {
                Flush();
                AddInlines(target, text[(i + 2)..boldEnd], true, italic);
                i = boldEnd + 2;
                continue;
            }

            if (c is '*' or '_' && OpensEmphasis(text, i) && ClosingEmphasis(text, i) is var italicEnd and > 0)
            {
                Flush();
                AddInlines(target, text[(i + 1)..italicEnd], bold, true);
                i = italicEnd + 1;
                continue;
            }

            if (c == '[' && Link.Match(text, i) is { Success: true } link)
            {
                // The text, not the address: this is somewhere to read, and a line of URL in the
                // middle of a sentence is the part nobody wanted to read.
                Flush();
                AddInlines(target, link.Groups[1].Value, bold, italic);
                i += link.Length;
                continue;
            }

            plain.Append(c);
            i++;
        }

        Flush();
    }

    /// <summary>An underscore inside a word is part of the word - UE_PLUGIN_NAME is not emphasis.</summary>
    private static bool OpensEmphasis(string text, int i) =>
        i + 1 < text.Length && !char.IsWhiteSpace(text[i + 1])
        && (text[i] == '*' || i == 0 || !char.IsLetterOrDigit(text[i - 1]));

    private static int ClosingEmphasis(string text, int open)
    {
        var mark = text[open];
        for (var j = open + 2; j < text.Length; j++)
        {
            if (text[j] != mark || char.IsWhiteSpace(text[j - 1])) continue;
            if (mark == '*' && j + 1 < text.Length && text[j + 1] == '*') { j++; continue; }
            if (mark == '_' && j + 1 < text.Length && char.IsLetterOrDigit(text[j + 1])) continue;
            return j;
        }

        return -1;
    }

    private static Run Run(string text, bool bold, bool italic, bool code)
    {
        var run = new Run(text);
        // Medium is the heaviest mono face embedded, and against Regular it is plainly bold.
        if (bold) run.FontWeight = FontWeight.Medium;
        if (italic) run.FontStyle = FontStyle.Italic;
        // The prose is mono already, so code is told apart by colour.
        if (code) run.Foreground = Brush("AmberBrush");
        return run;
    }

    private static IBrush? Brush(string key) => Resource(key) as IBrush;

    private static object? Resource(string key) =>
        Application.Current?.TryGetResource(key, null, out var value) == true ? value : null;
}
