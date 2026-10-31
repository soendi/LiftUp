using LiftUp.Core.Domain;
using LiftUp.Core.Services;
using Xunit;

namespace LiftUp.Core.Tests;

public sealed class ImageLibraryTests
{
    private static string CreateImage(TestDatabase fixture, string name)
    {
        var path = Path.Combine(fixture.Root, name);
        File.WriteAllBytes(path, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        return path;
    }

    private static ImageService Images(TestDatabase fixture, Session session) =>
        new(fixture.Database, session, Path.Combine(fixture.Root, "bilder"));

    private static Project CreateProject(TestDatabase fixture, Session session, string name) =>
        new ProjectService(fixture.Database, session).Create(new ProjectInput
        {
            Name = name,
            FolderPath = Path.Combine(fixture.Root, name.Replace(' ', '-')),
            CreateFolderIfMissing = true,
        });

    [Fact]
    public void Schlagworte_werden_gespeichert_und_durchsucht()
    {
        using var fixture = new TestDatabase();
        var images = Images(fixture, fixture.LoginAsAdmin());

        images.Import(CreateImage(fixture, "a.png"), new ImageInput
        {
            Name = "Grundriss",
            Category = "Plan",
            Tags = "logo, rot",
        });

        var listed = Assert.Single(images.Search(null, "rot"));
        Assert.Equal("logo, rot", listed.Tags);
        Assert.Equal(["logo", "rot"], listed.TagList);
        Assert.Single(images.Search(null, "Grundriss"));
        Assert.Single(images.Search(null, "plan"));
        Assert.Single(images.Search(null, "logo rot"));
        Assert.Empty(images.Search(null, "blau"));
    }

    [Fact]
    public void Schlagworte_werden_vereinheitlicht()
    {
        using var fixture = new TestDatabase();
        var images = Images(fixture, fixture.LoginAsAdmin());

        var image = images.Import(CreateImage(fixture, "a.png"), new ImageInput
        {
            Name = "Grundriss",
            Tags = " Rot ; rot, Blau ",
        });

        Assert.Equal("Rot, Blau", image.Tags);
    }

    [Fact]
    public void Aktives_projekt_und_alle_projekte_unterscheiden_sich()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var images = Images(fixture, session);
        var first = CreateProject(fixture, session, "Projekt Eins");
        var second = CreateProject(fixture, session, "Projekt Zwei");

        images.Import(CreateImage(fixture, "global.png"), new ImageInput { Name = "Global" });
        images.Import(CreateImage(fixture, "p1.png"), new ImageInput { Name = "Plan", ProjectId = first.Id });
        images.Import(CreateImage(fixture, "p2.png"), new ImageInput { Name = "Detail", ProjectId = second.Id });

        var activeProject = images.Search(first.Id, null);
        Assert.Equal(2, activeProject.Count);
        Assert.DoesNotContain(activeProject, item => item.Name == "Detail");

        var allProjects = images.Search(first.Id, null, allProjects: true);
        Assert.Equal(3, allProjects.Count);
    }

    [Fact]
    public void Dokumentensuche_bleibt_im_aktiven_projekt()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var images = Images(fixture, session);
        var first = CreateProject(fixture, session, "Projekt Eins");
        var second = CreateProject(fixture, session, "Projekt Zwei");

        images.Import(CreateImage(fixture, "global.png"), new ImageInput { Name = "Global" });
        images.Import(CreateImage(fixture, "p1.png"), new ImageInput { Name = "Plan", ProjectId = first.Id });
        images.Import(CreateImage(fixture, "p2.png"), new ImageInput { Name = "Detail", ProjectId = second.Id });

        // „&“ sucht nur im gewählten Projekt, nicht in der globalen Bibliothek.
        var onlyProject = images.SearchForDocument(first.Id, null, includeGlobal: false);
        Assert.Equal("Plan", Assert.Single(onlyProject).Name);

        // „&&“ nimmt die globale Bibliothek dazu, aber kein fremdes Projekt.
        var withGlobal = images.SearchForDocument(first.Id, null, includeGlobal: true);
        Assert.Equal(2, withGlobal.Count);
        Assert.DoesNotContain(withGlobal, item => item.Name == "Detail");
    }

    [Fact]
    public void Bearbeiten_aendert_name_kategorie_und_schlagworte()
    {
        using var fixture = new TestDatabase();
        var images = Images(fixture, fixture.LoginAsAdmin());
        var image = images.Import(CreateImage(fixture, "a.png"), new ImageInput
        {
            Name = "Alt",
            Category = "Plan",
            Tags = "altes",
        });

        var updated = images.Update(image.Id, new ImageInput
        {
            Name = "Neu",
            Category = "Foto",
            Tags = "frisch, rot",
        });

        Assert.Equal("Neu", updated.Name);
        Assert.Equal("Foto", updated.Category);
        Assert.Equal("frisch, rot", updated.Tags);

        var listed = Assert.Single(images.Search(null, "frisch"));
        Assert.Equal("Neu", listed.Name);
        Assert.Empty(images.Search(null, "altes"));
        Assert.True(File.Exists(updated.FilePath));
    }

    [Fact]
    public void Bearbeiten_lehnt_einen_doppelten_namen_ab()
    {
        using var fixture = new TestDatabase();
        var images = Images(fixture, fixture.LoginAsAdmin());
        images.Import(CreateImage(fixture, "a.png"), new ImageInput { Name = "Erstes" });
        var second = images.Import(CreateImage(fixture, "b.png"), new ImageInput { Name = "Zweites" });

        Assert.Throws<ValidationException>(() =>
            images.Update(second.Id, new ImageInput { Name = "Erstes" }));
    }

    [Fact]
    public void Fehlende_datei_wird_als_solche_gemeldet()
    {
        using var fixture = new TestDatabase();
        var images = Images(fixture, fixture.LoginAsAdmin());
        var image = images.Import(CreateImage(fixture, "a.png"), new ImageInput { Name = "Logo" });

        File.Delete(image.FilePath);

        var listed = Assert.Single(images.Search(null, null));
        Assert.False(listed.FileExists);
        Assert.Equal("fehlt", listed.FileStatusText);
    }

    [Fact]
    public void Fehlende_eintraege_lassen_sich_entfernen()
    {
        using var fixture = new TestDatabase();
        var images = Images(fixture, fixture.LoginAsAdmin());
        var missing = images.Import(CreateImage(fixture, "a.png"), new ImageInput { Name = "Weg" });
        images.Import(CreateImage(fixture, "b.png"), new ImageInput { Name = "Da" });

        File.Delete(missing.FilePath);

        Assert.Equal(1, images.RemoveMissing(null));
        var remaining = Assert.Single(images.Search(null, null));
        Assert.Equal("Da", remaining.Name);
        Assert.True(remaining.FileExists);
    }

    [Fact]
    public void Fehlende_eintraege_bleiben_im_gewaehlten_bereich()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var images = Images(fixture, session);
        var project = CreateProject(fixture, session, "Projekt Eins");

        var global = images.Import(CreateImage(fixture, "global.png"), new ImageInput { Name = "Global" });
        var projectImage = images.Import(
            CreateImage(fixture, "projekt.png"),
            new ImageInput { Name = "Plan", ProjectId = project.Id });

        File.Delete(global.FilePath);
        File.Delete(projectImage.FilePath);

        // Die Ansicht „Projekt“ zeigt globale und Projektbilder – aufgeräumt wird genau dieser Bereich.
        Assert.Equal(2, images.RemoveMissing(project.Id));
        Assert.Empty(images.Search(project.Id, null));
        Assert.Empty(images.Search(null, null));
    }

    [Fact]
    public void Aufraeumen_erfordert_das_recht_bilder_zu_verwalten()
    {
        using var fixture = new TestDatabase();
        var leser = fixture.CreateAndLogin("leser", "Leser12345", "Leser");

        Assert.Throws<PermissionDeniedException>(() => Images(fixture, leser).RemoveMissing(null));
    }

    [Fact]
    public void Bearbeiten_erfordert_das_recht_bilder_zu_verwalten()
    {
        using var fixture = new TestDatabase();
        var image = Images(fixture, fixture.LoginAsAdmin())
            .Import(CreateImage(fixture, "a.png"), new ImageInput { Name = "Logo" });

        var leser = fixture.CreateAndLogin("leser", "Leser12345", "Leser");

        Assert.Throws<PermissionDeniedException>(() =>
            Images(fixture, leser).Update(image.Id, new ImageInput { Name = "Anders" }));
    }
}
