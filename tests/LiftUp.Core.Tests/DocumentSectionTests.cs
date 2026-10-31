using System.Text;
using LiftUp.Core.Documents;
using LiftUp.Core.Domain;
using LiftUp.Core.Services;
using Xunit;

namespace LiftUp.Core.Tests;

public sealed class DocumentSectionTests
{
    private sealed class FakeRenderer : IHtmlToPdfRenderer
    {
        public string? LastHtml { get; private set; }

        public Task<byte[]> RenderAsync(string html, PdfPageSettings settings, CancellationToken cancellationToken = default)
        {
            LastHtml = html;
            return Task.FromResult(Encoding.UTF8.GetBytes("%PDF-1.4"));
        }
    }

    [Fact]
    public void Abschnitte_werden_in_der_gespeicherten_reihenfolge_gelesen()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var templates = new TemplateService(fixture.Database, session);
        var template = templates.Create(new TemplateInput
        {
            Name = "Bericht",
            Html = "<html><body><h1>Titel</h1></body></html>",
        });

        templates.SaveSections(template.Id, ["Einleitung", "  Details  ", "", "Fazit"]);

        var sections = templates.GetSections(template.Id);
        Assert.Equal(["Einleitung", "Details", "Fazit"], sections.Select(section => section.Title));
    }

    [Fact]
    public void Abschnitte_werden_bei_jedem_speichern_ersetzt()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var templates = new TemplateService(fixture.Database, session);
        var template = templates.Create(new TemplateInput { Name = "Protokoll", Html = "<html><body></body></html>" });

        templates.SaveSections(template.Id, ["Teilnehmer", "Beschlüsse"]);
        templates.SaveSections(template.Id, ["Traktanden"]);

        Assert.Equal(["Traktanden"], templates.GetSections(template.Id).Select(section => section.Title));
    }

    [Fact]
    public void Dokument_verbindet_Titelseite_und_Abschnitte()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var projects = new ProjectService(fixture.Database, session);
        var templates = new TemplateService(fixture.Database, session);
        var documents = new DocumentService(fixture.Database, session, new FakeRenderer());

        var project = projects.Create(new ProjectInput
        {
            Name = "Musterweg",
            FolderPath = Path.Combine(fixture.Root, "projekt"),
            CreateFolderIfMissing = true,
        });
        var template = templates.Create(new TemplateInput
        {
            Name = "Bericht",
            Html = "<html><body><h1>{{project.name}}</h1></body></html>",
        });

        var html = documents.RenderHtml(project.Id, template.Id,
        [
            new DocumentSectionContent("Einleitung", "<p>Einstieg</p>"),
            new DocumentSectionContent("Ohne Inhalt", "   "),
        ]);

        Assert.Contains("<h1>Musterweg</h1>", html);
        Assert.Contains("Einleitung", html);
        Assert.Contains("<p>Einstieg</p>", html);
        Assert.DoesNotContain("Ohne Inhalt", html);
        Assert.True(
            html.IndexOf("<h1>", StringComparison.Ordinal) < html.IndexOf("<h2", StringComparison.Ordinal),
            "Die Titelseite muss vor den Abschnitten stehen.");
    }

    [Fact]
    public void Absenderdaten_stehen_als_Platzhalter_bereit()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var projects = new ProjectService(fixture.Database, session);
        var templates = new TemplateService(fixture.Database, session);
        var documents = new DocumentService(fixture.Database, session, new FakeRenderer());

        new CompanyProfileService(fixture.Database, session).Save(new CompanyProfile
        {
            Name = "Muster Bau AG",
            Contact = "Max Muster",
            Email = "max@muster.ch",
            Phone = "+41 79 000 00 00",
        });

        var project = projects.Create(new ProjectInput
        {
            Name = "Musterweg",
            FolderPath = Path.Combine(fixture.Root, "projekt"),
            CreateFolderIfMissing = true,
        });
        var template = templates.Create(new TemplateInput
        {
            Name = "Titelblatt",
            Html = "<body>{{company.name}}|{{company.contact}}|{{company.email}}|{{company.phone}}</body>",
        });

        var html = documents.RenderHtml(project.Id, template.Id);

        Assert.Contains("Muster Bau AG|Max Muster|max@muster.ch|+41 79 000 00 00", html);
    }
}
