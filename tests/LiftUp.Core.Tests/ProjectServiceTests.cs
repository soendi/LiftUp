using LiftUp.Core.Data;
using LiftUp.Core.Domain;
using LiftUp.Core.Services;
using Xunit;

namespace LiftUp.Core.Tests;

public sealed class ProjectServiceTests
{
    private static ProjectInput Input(string folder, string name = "Neubau Musterweg", string? number = null) => new()
    {
        Name = name,
        Number = number,
        Customer = "Muster Bau GmbH",
        Description = "Testprojekt",
        FolderPath = folder,
        Status = ProjectStatus.Active,
        CreateFolderIfMissing = true,
    };

    [Fact]
    public void Projekt_anlegen_erstellt_ordner()
    {
        using var fixture = new TestDatabase();
        var service = new ProjectService(fixture.Database, fixture.LoginAsAdmin());
        var folder = Path.Combine(fixture.Root, "Projekte", "P-001");

        var project = service.Create(Input(folder, number: "P-001"));

        Assert.True(Directory.Exists(folder));
        Assert.Equal(folder, project.FolderPath);
        Assert.Single(service.Search(null));
    }

    [Fact]
    public void Vorhandener_ordner_wird_nicht_angelegt()
    {
        using var fixture = new TestDatabase();
        var service = new ProjectService(fixture.Database, fixture.LoginAsAdmin());
        var folder = Path.Combine(fixture.Root, "Bestand");
        Directory.CreateDirectory(folder);

        service.Create(Input(folder));

        Assert.True(Directory.Exists(folder));
        Assert.Single(service.Search(null));
    }

    [Fact]
    public void Fehlender_ordner_ohne_anlegen_wird_abgelehnt()
    {
        using var fixture = new TestDatabase();
        var service = new ProjectService(fixture.Database, fixture.LoginAsAdmin());
        var input = Input(Path.Combine(fixture.Root, "gibt-es-nicht"));
        input.CreateFolderIfMissing = false;

        var exception = Assert.Throws<ValidationException>(() => service.Create(input));
        Assert.Contains("existiert nicht", exception.Message);
    }

    [Fact]
    public void Leerer_projektname_wird_abgelehnt()
    {
        using var fixture = new TestDatabase();
        var service = new ProjectService(fixture.Database, fixture.LoginAsAdmin());

        Assert.Throws<ValidationException>(() => service.Create(Input(Path.Combine(fixture.Root, "x"), name: "   ")));
    }

    [Fact]
    public void Doppelter_ordner_wird_abgelehnt()
    {
        using var fixture = new TestDatabase();
        var service = new ProjectService(fixture.Database, fixture.LoginAsAdmin());
        var folder = Path.Combine(fixture.Root, "Projekte", "gleich");

        service.Create(Input(folder, name: "Erstes"));

        var exception = Assert.Throws<ValidationException>(() => service.Create(Input(folder, name: "Zweites")));
        Assert.Contains("bereits einem anderen Projekt", exception.Message);
    }

    [Fact]
    public void Doppelte_projektnummer_wird_abgelehnt()
    {
        using var fixture = new TestDatabase();
        var service = new ProjectService(fixture.Database, fixture.LoginAsAdmin());

        service.Create(Input(Path.Combine(fixture.Root, "a"), name: "A", number: "2026-1"));

        var exception = Assert.Throws<ValidationException>(() =>
            service.Create(Input(Path.Combine(fixture.Root, "b"), name: "B", number: "2026-1")));
        Assert.Contains("bereits vergeben", exception.Message);
    }

    [Fact]
    public void Projekt_bearbeiten_aendert_felder()
    {
        using var fixture = new TestDatabase();
        var service = new ProjectService(fixture.Database, fixture.LoginAsAdmin());
        var project = service.Create(Input(Path.Combine(fixture.Root, "p1"), name: "Alt", number: "1"));

        var update = Input(Path.Combine(fixture.Root, "p1"), name: "Neu", number: "2");
        update.Status = ProjectStatus.Completed;
        update.Customer = "Anderer Kunde";
        service.Update(project.Id, update);

        var reloaded = service.GetProject(project.Id);
        Assert.Equal("Neu", reloaded.Name);
        Assert.Equal("2", reloaded.Number);
        Assert.Equal(ProjectStatus.Completed, reloaded.Status);
        Assert.Equal("Anderer Kunde", reloaded.Customer);
        Assert.Equal(2, reloaded.RowVersion);
    }

    [Fact]
    public void Projekt_loeschen_entfernt_aus_liste_und_behaelt_ordner()
    {
        using var fixture = new TestDatabase();
        var service = new ProjectService(fixture.Database, fixture.LoginAsAdmin());
        var folder = Path.Combine(fixture.Root, "p1");
        var project = service.Create(Input(folder));

        service.Delete(project.Id, moveFolderToRecycleBin: false);

        Assert.Empty(service.Search(null));
        Assert.True(Directory.Exists(folder));
        Assert.Throws<ValidationException>(() => service.GetProject(project.Id));
    }

    [Fact]
    public void Projektnummer_nach_loeschen_wieder_verwendbar()
    {
        using var fixture = new TestDatabase();
        var service = new ProjectService(fixture.Database, fixture.LoginAsAdmin());
        var first = service.Create(Input(Path.Combine(fixture.Root, "p1"), name: "Erstes", number: "4711"));

        service.Delete(first.Id, moveFolderToRecycleBin: false);
        service.Create(Input(Path.Combine(fixture.Root, "p2"), name: "Zweites", number: "4711"));

        Assert.Single(service.Search(null));
    }

    [Fact]
    public void Aenderungen_landen_im_aenderungsprotokoll()
    {
        using var fixture = new TestDatabase();
        var admin = fixture.LoginAsAdmin();
        var service = new ProjectService(fixture.Database, admin);
        var writer = new ChangeLogWriter(fixture.Database);

        var project = service.Create(Input(Path.Combine(fixture.Root, "p1")));
        service.Update(project.Id, Input(Path.Combine(fixture.Root, "p1"), name: "Umbenannt"));
        service.Delete(project.Id, moveFolderToRecycleBin: false);

        var pending = writer.GetPending();
        var projectEntries = pending.Where(entry => entry.EntityType == "Project").ToArray();

        Assert.Equal(3, projectEntries.Length);
        Assert.Equal(["Create", "Update", "Delete"], projectEntries.Select(entry => entry.Operation));
        Assert.All(projectEntries, entry => Assert.Equal(project.Id.ToString("D"), entry.EntityId));
        Assert.Contains("Umbenannt", projectEntries[1].Payload);
    }

    [Fact]
    public void Suche_findet_nach_name_und_kunde()
    {
        using var fixture = new TestDatabase();
        var service = new ProjectService(fixture.Database, fixture.LoginAsAdmin());
        service.Create(Input(Path.Combine(fixture.Root, "a"), name: "Schule Nord"));
        service.Create(Input(Path.Combine(fixture.Root, "b"), name: "Büro Süd"));

        Assert.Single(service.Search("Schule"));
        Assert.Single(service.Search("Süd"));
        Assert.Equal(2, service.Search(null).Count);
    }

    [Fact]
    public void Ohne_anlege_recht_kein_neues_projekt()
    {
        using var fixture = new TestDatabase();
        var viewer = fixture.CreateAndLogin("leser", "Leser12345", "Betrachter");
        var service = new ProjectService(fixture.Database, viewer);

        Assert.Throws<PermissionDeniedException>(() => service.Create(Input(Path.Combine(fixture.Root, "p"))));
    }
}
