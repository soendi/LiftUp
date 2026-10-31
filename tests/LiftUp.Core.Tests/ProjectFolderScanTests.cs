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

        var sync = images.SyncProjectFolder(project.Id);

        Assert.Equal(2, sync.Added);
        Assert.Equal(0, sync.Moved);

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

        Assert.Equal(1, images.SyncProjectFolder(project.Id).Added);
        Assert.Equal(0, images.SyncProjectFolder(project.Id).Added);
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

        Assert.Equal(2, images.SyncProjectFolder(project.Id).Added);

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

        Assert.Equal(1, images.SyncProjectFolder(project.Id).Added);
        Assert.Single(images.Search(project.Id, null));
    }

    [Fact]
    public void Verschobene_datei_wird_wiedererkannt()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var images = Images(fixture, session);
        var project = CreateProject(fixture, session);

        var source = WriteImage(Path.Combine(project.FolderPath, "02_Bilder"), "logo.png");
        Assert.Equal(1, images.SyncProjectFolder(project.Id).Added);

        // Datei in einen anderen Unterordner verschieben.
        var targetFolder = Path.Combine(project.FolderPath, "02_Bilder", "Neu");
        Directory.CreateDirectory(targetFolder);
        var target = Path.Combine(targetFolder, "logo.png");
        File.Move(source, target);

        var sync = images.SyncProjectFolder(project.Id);

        Assert.Equal(0, sync.Added);
        Assert.Equal(1, sync.Moved);

        var listed = Assert.Single(images.Search(project.Id, null));
        Assert.Equal(Path.GetFullPath(target), Path.GetFullPath(listed.FilePath));
        Assert.Equal("02_Bilder/Neu", listed.Category);
        Assert.True(listed.FileExists);
    }

    [Fact]
    public void Umbenannte_datei_wird_wiedererkannt()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var images = Images(fixture, session);
        var project = CreateProject(fixture, session);

        var source = WriteImage(Path.Combine(project.FolderPath, "02_Bilder"), "logo.png");
        images.SyncProjectFolder(project.Id);

        var target = Path.Combine(project.FolderPath, "02_Bilder", "zeichen.png");
        File.Move(source, target);

        var sync = images.SyncProjectFolder(project.Id);

        Assert.Equal(0, sync.Added);
        Assert.Equal(1, sync.Moved);

        var listed = Assert.Single(images.Search(project.Id, null));
        var image = images.Get(listed.Id);

        Assert.Equal(Path.GetFullPath(target), Path.GetFullPath(image.FilePath));
        Assert.Equal("zeichen.png", image.FileName);
        Assert.Equal("logo", image.Name);
        Assert.True(File.Exists(image.FilePath));
    }

    [Fact]
    public void Anderer_inhalt_bei_gleicher_groesse_gilt_als_neues_bild()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var images = Images(fixture, session);
        var project = CreateProject(fixture, session);
        var folder = Path.Combine(project.FolderPath, "02_Bilder");

        var source = WriteImage(folder, "logo.png");
        images.SyncProjectFolder(project.Id);

        // Alte Datei umbenennen (Inhalt A), danach eine neue Datei mit anderem Inhalt
        // gleicher Länge anlegen – sie darf nicht als „verschoben“ gelten.
        File.Move(source, Path.Combine(folder, "alt.png"));
        File.WriteAllBytes(Path.Combine(folder, "neu.png"), [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08]);

        var sync = images.SyncProjectFolder(project.Id);

        Assert.Equal(1, sync.Moved);
        Assert.Equal(1, sync.Added);

        var listed = images.Search(project.Id, null);
        Assert.Equal(2, listed.Count);
        Assert.Single(listed, item => item.Name == "logo" && item.FileExists);
        Assert.Single(listed, item => item.Name == "neu" && item.FileExists);
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
