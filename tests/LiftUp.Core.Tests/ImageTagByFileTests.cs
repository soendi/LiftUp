using LiftUp.Core.Domain;
using LiftUp.Core.Services;
using Xunit;

namespace LiftUp.Core.Tests;

/// <summary>
/// Schlagwort über den Dateipfad setzen: Projektbild und Fotos aus dem Projektordner werden
/// dabei bei Bedarf zuerst in die Bibliothek übernommen.
/// </summary>
public sealed class ImageTagByFileTests
{
    private const string Library = "bilder";

    private static ImageService Images(TestDatabase fixture, Session session) =>
        new(fixture.Database, session, Path.Combine(fixture.Root, Library));

    private static Project CreateProject(TestDatabase fixture, Session session) =>
        new ProjectService(fixture.Database, session).Create(new ProjectInput
        {
            Name = "Neubau",
            FolderPath = Path.Combine(fixture.Root, "projekt"),
            CreateFolderIfMissing = true,
        });

    private static string WriteImage(Project project, string name)
    {
        var folder = Path.Combine(project.FolderPath, ProjectFolders.Images);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        return path;
    }

    [Fact]
    public void Bild_bekommt_das_schlagwort_ueber_den_pfad()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var images = Images(fixture, session);
        var project = CreateProject(fixture, session);
        var path = WriteImage(project, "Projektbild.png");

        Assert.True(images.TryAddTagToFile(project.Id, path, "Projektbild"));

        var item = Assert.Single(images.Search(project.Id, null));
        Assert.Contains("Projektbild", item.Tags, StringComparison.Ordinal);

        // Ein zweiter Aufruf ändert nichts, meldet aber weiterhin Erfolg.
        Assert.True(images.TryAddTagToFile(project.Id, path, "Projektbild"));
        Assert.Single(images.Search(project.Id, null));
    }

    [Fact]
    public void Aufzugsnummer_wird_zum_zweiten_schlagwort()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var images = Images(fixture, session);
        var project = CreateProject(fixture, session);
        var path = WriteImage(project, "foto.png");

        images.TryAddTagToFile(project.Id, path, "8609861");

        var item = Assert.Single(images.Search(project.Id, null));
        Assert.Contains("8609861", item.Tags, StringComparison.Ordinal);
    }

    [Fact]
    public void Unbekannter_pfad_bekommt_kein_schlagwort()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var images = Images(fixture, session);
        var project = CreateProject(fixture, session);

        Assert.False(images.TryAddTagToFile(project.Id,
            Path.Combine(project.FolderPath, ProjectFolders.Images, "fehlt.png"), "Projektbild"));
        Assert.Empty(images.Search(project.Id, null));
    }
}
