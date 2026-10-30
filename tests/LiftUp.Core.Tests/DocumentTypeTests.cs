using LiftUp.Core.Domain;
using LiftUp.Core.Services;
using Xunit;

namespace LiftUp.Core.Tests;

public sealed class DocumentTypeTests
{
    [Fact]
    public void Standardtypen_sind_vorhanden()
    {
        using var fixture = new TestDatabase();
        var types = new DocumentTypeService(fixture.Database, fixture.LoginAsAdmin()).GetAll();

        Assert.Contains(types, type => type.Name == "Angebot");
        Assert.Contains(types, type => type.Name == "Dokument");
    }

    [Fact]
    public void Doppelter_typ_wird_abgelehnt()
    {
        using var fixture = new TestDatabase();
        var service = new DocumentTypeService(fixture.Database, fixture.LoginAsAdmin());

        service.Create(new DocumentTypeInput { Name = "Protokoll" });

        var exception = Assert.Throws<ValidationException>(() =>
            service.Create(new DocumentTypeInput { Name = "protokoll" }));
        Assert.Contains("existiert bereits", exception.Message);
    }

    [Fact]
    public void Umbenennen_zieht_vorlagen_mit()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var types = new DocumentTypeService(fixture.Database, session);
        var templates = new TemplateService(fixture.Database, session);

        var type = types.Create(new DocumentTypeInput { Name = "Protokoll" });
        templates.Create(new TemplateInput { Name = "Sitzung", DocumentType = "Protokoll", Html = "<p>x</p>" });

        types.Rename(type.Id, "Besprechung");

        var template = templates.GetAll().Single(item => item.Name == "Sitzung");
        Assert.Equal("Besprechung", template.DocumentType);
    }

    [Fact]
    public void Vorlagen_lassen_sich_nach_typ_filtern()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var templates = new TemplateService(fixture.Database, session);

        templates.Create(new TemplateInput { Name = "Angebot kurz", DocumentType = "Angebot", Html = "<p>a</p>" });
        templates.Create(new TemplateInput { Name = "Rechnung kurz", DocumentType = "Rechnung", Html = "<p>r</p>" });

        var angebote = templates.GetAll(documentType: "Angebot");

        Assert.Single(angebote);
        Assert.Equal("Angebot kurz", angebote[0].Name);

        // Die mitgelieferte Startvorlage zählt nicht mit.
        Assert.Equal(2, templates.GetAll().Count(item => item.Name != "Standard-Dokument"));
    }

    [Fact]
    public void Typ_mit_vorlagen_laesst_sich_nicht_archivieren()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var types = new DocumentTypeService(fixture.Database, session);
        var templates = new TemplateService(fixture.Database, session);

        var type = types.Create(new DocumentTypeInput { Name = "Protokoll" });
        templates.Create(new TemplateInput { Name = "Sitzung", DocumentType = "Protokoll", Html = "<p>x</p>" });

        var exception = Assert.Throws<ValidationException>(() => types.SetArchived(type.Id, archived: true));
        Assert.Contains("verwendet", exception.Message);
    }

    [Fact]
    public void Unbekannter_typ_wird_beim_speichern_abgelehnt()
    {
        using var fixture = new TestDatabase();
        var templates = new TemplateService(fixture.Database, fixture.LoginAsAdmin());

        var exception = Assert.Throws<ValidationException>(() =>
            templates.Create(new TemplateInput { Name = "X", DocumentType = "Gibtsnicht", Html = "<p>x</p>" }));
        Assert.Contains("nicht bekannt", exception.Message);
    }
}
