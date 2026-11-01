using LiftUp.Core.Domain;
using LiftUp.Core.Services;
using Xunit;

namespace LiftUp.Core.Tests;

/// <summary>Projektbild für das Deckblatt: Prüfung, Kopie in den Projektordner und Eindeutigkeit.</summary>
public sealed class ProjectImageTests
{
    private static ProjectInput Input(string folder, string? imagePath) => new()
    {
        Name = "Messligfeld 1 und 3, Sennewald",
        FolderPath = folder,
        ImagePath = imagePath,
        Status = ProjectStatus.Active,
        CreateFolderIfMissing = true,
    };

    /// <summary>Legt eine Datei mit Bildendung an; der Inhalt spielt für die Prüfung keine Rolle.</summary>
    private static string WriteImage(string folder, string name)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, [0x89, 0x50, 0x4E, 0x47]);
        return path;
    }

    [Fact]
    public void Neues_projekt_kopiert_das_bild_in_den_projektordner()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var projects = new ProjectService(fixture.Database, session);

        var source = WriteImage(Path.Combine(fixture.Root, "quelle"), "deckblatt.png");
        var folder = Path.Combine(fixture.Root, "p1");

        var project = projects.Create(Input(folder, source));

        var expected = Path.Combine(folder, ProjectFolders.Images, "deckblatt.png");
        Assert.Equal(expected, project.ImagePath);
        Assert.True(File.Exists(expected));
        // Das Original bleibt am gewählten Ort liegen.
        Assert.True(File.Exists(source));

        var loaded = projects.GetProject(project.Id);
        Assert.Equal(expected, loaded.ImagePath);
    }

    [Fact]
    public void Bild_im_projektordner_bleibt_an_seinem_platz()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var projects = new ProjectService(fixture.Database, session);

        var folder = Path.Combine(fixture.Root, "p1");
        var inside = WriteImage(Path.Combine(folder, ProjectFolders.Images), "logo.png");

        var project = projects.Create(Input(folder, inside));

        Assert.Equal(inside, project.ImagePath);
        Assert.Single(Directory.GetFiles(Path.Combine(folder, ProjectFolders.Images)));
    }

    [Fact]
    public void Gleichnamiges_bild_bekommt_einen_eigenen_namen()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var projects = new ProjectService(fixture.Database, session);

        var first = WriteImage(Path.Combine(fixture.Root, "quelle1"), "bild.png");
        var second = WriteImage(Path.Combine(fixture.Root, "quelle2"), "bild.png");
        var folder = Path.Combine(fixture.Root, "p1");

        var project = projects.Create(Input(folder, first));
        var replaced = projects.Update(project.Id, Input(folder, second));

        var expected = Path.Combine(folder, ProjectFolders.Images, "bild (2).png");
        Assert.Equal(expected, replaced.ImagePath);
        Assert.True(File.Exists(Path.Combine(folder, ProjectFolders.Images, "bild.png")));
        Assert.True(File.Exists(expected));
    }

    [Fact]
    public void Dateityp_ohne_bildformat_wird_abgelehnt()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var projects = new ProjectService(fixture.Database, session);

        var text = WriteImage(Path.Combine(fixture.Root, "quelle"), "notiz.txt");
        var folder = Path.Combine(fixture.Root, "p1");

        Assert.Throws<ValidationException>(() => projects.Create(Input(folder, text)));
        Assert.Empty(projects.Search(new ProjectQuery()));
    }
}
