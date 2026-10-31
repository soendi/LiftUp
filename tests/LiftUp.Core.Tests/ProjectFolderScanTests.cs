using LiftUp.Core.Domain;
using LiftUp.Core.Services;
using Xunit;

namespace LiftUp.Core.Tests;

public sealed class ProjectFolderScanTests
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

    private static string WriteImage(string folder, string name)
    {
        var path = Path.Combine(folder, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        return path;
    }

    [Fact]
    public void Bilder_aus_dem_projektordner_werden_uebernommen()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var images = Images(fixture, session);
        var project = CreateProject(fixture, session);

        var rootFile = WriteImage(Path.Combine(project.FolderPath, "02_Bilder"), "logo.png");
        var subFile = WriteImage(Path.Combine(project.FolderPath, "02_Bilder", "Grundriss"), "plan.png");

        var added = images.SyncProjectFolder(project.Id);

        Assert.Equal(2, added);

        var listed = images.Search(project.Id, null);
        Assert.Equal(2, listed.Count);

        var logo = Assert.Single(listed, item => item.Name == "logo");
        Assert.Equal("02_Bilder", logo.Category);
        Assert.Equal(Path.GetFullPath(rootFile), Path.GetFullPath(logo.FilePath));
        Assert.True(File.Exists(rootFile));

        var plan = Assert.Single(listed, item => item.Name == "plan");
        Assert.Equal("02_Bilder/Grundriss", plan.Category);
        Assert.Equal(Path.GetFullPath(subFile), Path.GetFullPath(plan.FilePath));

        // Es wird nichts in die Bibliothek kopiert.
        var libraryFolder = Path.Combine(fixture.Root, Library);
        var copies = Directory.Exists(libraryFolder)
            ? Directory.GetFiles(libraryFolder, "*", SearchOption.AllDirectories)
            : [];
        Assert.Empty(copies);
    }

    [Fact]
    public void Erneuter_abgleich_legt_nichts_doppelt_an()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var images = Images(fixture, session);
        var project = CreateProject(fixture, session);
        WriteImage(Path.Combine(project.FolderPath, "02_Bilder"), "logo.png");

        Assert.Equal(1, images.SyncProjectFolder(project.Id));
        Assert.Equal(0, images.SyncProjectFolder(project.Id));
        Assert.Single(images.Search(project.Id, null));
    }

    [Fact]
    public void Gleicher_dateiname_in_unterordnern_bekommt_einen_zusatz()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var images = Images(fixture, session);
        var project = CreateProject(fixture, session);

        WriteImage(Path.Combine(project.FolderPath, "02_Bilder", "a"), "logo.png");
        WriteImage(Path.Combine(project.FolderPath, "02_Bilder", "b"), "logo.png");

        Assert.Equal(2, images.SyncProjectFolder(project.Id));

        var names = images.Search(project.Id, null).Select(item => item.Name).OrderBy(name => name).ToArray();
        Assert.Equal(["logo", "logo (2)"], names);
    }

    [Fact]
    public void Nicht_bilddateien_werden_ignoriert()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var images = Images(fixture, session);
        var project = CreateProject(fixture, session);

        WriteImage(Path.Combine(project.FolderPath, "02_Bilder"), "logo.png");
        File.WriteAllText(Path.Combine(project.FolderPath, "01_Dokumente", "notiz.txt"), "kein Bild");
        File.WriteAllBytes(Path.Combine(project.FolderPath, "03_PDF", "angebot.pdf"), [0x25, 0x50, 0x44, 0x46]);

        Assert.Equal(1, images.SyncProjectFolder(project.Id));
        Assert.Single(images.Search(project.Id, null));
    }

    [Fact]
    public void Ordnerabgleich_erfordert_das_recht_bilder_zu_verwalten()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var project = CreateProject(fixture, session);

        var leser = fixture.CreateAndLogin("leser", "Leser12345", "Leser");

        Assert.Throws<PermissionDeniedException>(() => Images(fixture, leser).SyncProjectFolder(project.Id));
    }
}
