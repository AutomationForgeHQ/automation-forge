using Xunit;
using Forge.Core.Engines;
using Forge.Core.Projects;

namespace Forge.Core.Tests;

/// <summary>The Projects page's grid: which picture a project has, its initials, the search and the orders.</summary>
public sealed class ProjectGalleryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kovati-gallery-tests", Guid.NewGuid().ToString("N"));

    public ProjectGalleryTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Project(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name + ".uproject");
        File.WriteAllText(path, "{ \"FileVersion\": 3, \"EngineAssociation\": \"5.8\" }");
        return path;
    }

    private static void Png(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
    }

    [Fact]
    public void The_projects_own_picture_comes_first()
    {
        var p = Project("MyGame");
        Png(Path.Combine(_root, "MyGame", "MyGame.png"));
        Png(Path.Combine(_root, "MyGame", "Saved", "AutoScreenshot.png"));
        Assert.Equal(Path.Combine(_root, "MyGame", "MyGame.png"), ProjectGallery.PictureOf(p));
    }

    [Fact]
    public void Without_its_own_picture_the_auto_screenshot_is_used()
    {
        var p = Project("Studio");
        Png(Path.Combine(_root, "Studio", "Saved", "AutoScreenshot.png"));
        Assert.Equal(Path.Combine(_root, "Studio", "Saved", "AutoScreenshot.png"), ProjectGallery.PictureOf(p));
    }

    [Fact]
    public void No_picture_an_empty_file_or_another_name_is_none()
    {
        var p = Project("Archive");
        Assert.Null(ProjectGallery.PictureOf(p));
        File.WriteAllBytes(Path.Combine(_root, "Archive", "Archive.png"), []);
        Assert.Null(ProjectGallery.PictureOf(p));
        Png(Path.Combine(_root, "Archive", "Other.png"));
        Assert.Null(ProjectGallery.PictureOf(p));
        Assert.Null(ProjectGallery.PictureOf(""));
        Assert.Null(ProjectGallery.PictureOf(Path.Combine(_root, "Gone", "Gone.uproject")));
    }

    [Theory]
    [InlineData("Colony_NP24", "CN")]
    [InlineData("MyGame", "MG")]
    [InlineData("Studio", "ST")]
    [InlineData("client-demo", "CD")]
    [InlineData("X", "X")]
    [InlineData("__", "?")]
    [InlineData("UE5Sandbox", "UE")]
    public void Initials_are_two_letters_from_the_name(string name, string initials) =>
        Assert.Equal(initials, ProjectGallery.Initials(name));

    private static ProjectReport Report(string path, DateTime? opened = null, string? engine = "5.8", bool installed = true) => new()
    {
        Found = new FoundProject(path, ProjectSource.Recent, opened),
        Engine = new ResolvedEngine(AssociationKind.Version, engine ?? "", engine,
            installed && engine is not null ? new EngineInstall(engine, engine + ".1", @"C:\UE_" + engine, true) : null),
    };

    [Fact]
    public void Search_matches_name_or_folder_in_any_case()
    {
        var r = Report(@"D:\Work\Clients\Acme\ClientDemo\ClientDemo.uproject");
        Assert.True(ProjectGallery.Matches(r, "clientdemo"));
        Assert.True(ProjectGallery.Matches(r, "ACME"));
        Assert.True(ProjectGallery.Matches(r, "  demo "));
        Assert.True(ProjectGallery.Matches(r, ""));
        Assert.True(ProjectGallery.Matches(r, null));
        Assert.False(ProjectGallery.Matches(r, "colony"));
        // The .uproject's own file name is not searched beyond the name: ".uproject" is in every path.
        Assert.False(ProjectGallery.Matches(r, ".uproject"));
    }

    [Fact]
    public void Recently_opened_puts_the_newest_first_and_never_opened_last_by_name()
    {
        var a = Report(@"C:\P\Alpha\Alpha.uproject", new DateTime(2026, 3, 1));
        var b = Report(@"C:\P\Beta\Beta.uproject", new DateTime(2026, 11, 19));
        var c = Report(@"C:\P\Charlie\Charlie.uproject");
        var d = Report(@"C:\P\Able\Able.uproject");
        var order = ProjectGallery.Sort([a, b, c, d], r => r, ProjectSort.RecentlyOpened).Select(r => r.Name);
        Assert.Equal(["Beta", "Alpha", "Able", "Charlie"], order);
    }

    [Fact]
    public void Name_order_ignores_case()
    {
        var order = ProjectGallery.Sort([Report(@"C:\b\beta.uproject"), Report(@"C:\a\Alpha.uproject"), Report(@"C:\c\Gamma.uproject")], r => r, ProjectSort.Name).Select(r => r.Name);
        Assert.Equal(["Alpha", "beta", "Gamma"], order);
    }

    [Fact]
    public void Engine_order_is_newest_first_by_version_not_by_text_and_unknown_last()
    {
        var order = ProjectGallery.Sort(
            [Report(@"C:\a\A.uproject", engine: "5.6"), Report(@"C:\b\B.uproject", engine: null), Report(@"C:\c\C.uproject", engine: "5.10"),
             Report(@"C:\d\D.uproject", engine: "5.8"), Report(@"C:\e\E.uproject", engine: "5.8", installed: false)],
            r => r, ProjectSort.EngineVersion).Select(r => r.Name);
        Assert.Equal(["C", "D", "E", "A", "B"], order);
    }

    [Fact]
    public void The_badge_says_the_version_and_whether_it_is_here()
    {
        Assert.Equal("5.8", ProjectGallery.EngineBadge(Report(@"C:\a\A.uproject").Engine));
        Assert.Equal("5.7 · not installed", ProjectGallery.EngineBadge(Report(@"C:\a\A.uproject", engine: "5.7", installed: false).Engine));
        Assert.Equal("ENGINE ?", ProjectGallery.EngineBadge(new ResolvedEngine(AssociationKind.Version, "", null, null)));
    }
}
