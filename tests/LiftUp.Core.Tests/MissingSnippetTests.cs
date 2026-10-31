using System.Text;
using LiftUp.Core.Documents;
using LiftUp.Core.Domain;
using LiftUp.Core.Services;
using Xunit;

namespace LiftUp.Core.Tests;

public sealed class MissingSnippetTests
{
    private sealed class NoopRenderer : IHtmlToPdfRenderer
    {
        public Task<byte[]> RenderAsync(string html, PdfPageSettings settings, CancellationToken cancellationToken = default)
            => Task.FromResult(Encoding.UTF8.GetBytes("%PDF-1.4"));
    }

    private static (Project Project, DocumentTemplate Template) Setup(TestDatabase fixture, Session session, string html)
    {
        var project = new ProjectService(fixture.Database, session).Create(new ProjectInput
        {
            Name = "Projekt",
            FolderPath = Path.Combine(fixture.Root, "projekt"),
            CreateFolderIfMissing = true,
        });

        var template = new TemplateService(fixture.Database, session)
            .Create(new TemplateInput { Name = "Angebot", Html = html });

        return (project, template);
    }

    [Fact]
    public void Fehlender_textbaustein_wird_gemeldet()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var (project, template) = Setup(fixture, session, "<p>{{snippet:Garantie}}</p><p>{{bild:Logo}}</p>");
        var documents = new DocumentService(fixture.Database, session, new NoopRenderer());

        var missing = documents.FindMissingPlaceholders(project.Id, template.Id);

        Assert.Equal(["Garantie"], missing.Snippets);
        Assert.Equal(["Logo"], missing.Images);
        Assert.True(missing.Any);
    }

    [Fact]
    public void Vorhandener_textbaustein_wird_nicht_gemeldet()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var (project, template) = Setup(fixture, session, "<p>{{snippet:Garantie}}</p>");
        new SnippetService(fixture.Database, session)
            .Create(new SnippetInput { Name = "Garantie", Html = "<p>24 Monate</p>" });
        var documents = new DocumentService(fixture.Database, session, new NoopRenderer());

        var missing = documents.FindMissingPlaceholders(project.Id, template.Id);

        Assert.False(missing.Any);
        Assert.Empty(missing.Snippets);
        Assert.Empty(documents.FindMissingImages(project.Id, template.Id));
    }

    [Fact]
    public void Erzeugen_wird_bei_fehlendem_textbaustein_blockiert()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var (project, template) = Setup(fixture, session, "<p>{{snippet:Garantie}}</p>");
        var documents = new DocumentService(fixture.Database, session, new NoopRenderer());

        var exception = Assert.Throws<ValidationException>(() =>
            documents.CreatePdfAsync(project.Id, template.Id).GetAwaiter().GetResult());

        Assert.Contains("Garantie", exception.Message);
        Assert.Contains("Textbaustein", exception.Message);
        Assert.False(Directory.Exists(Path.Combine(project.FolderPath, "03_PDF")) &&
                     Directory.GetFiles(Path.Combine(project.FolderPath, "03_PDF")).Length > 0);
    }

    [Fact]
    public void Textbaustein_platzhalter_entfernen_erzeugt_neue_version()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var (project, template) = Setup(fixture, session, "<p>Text</p><p>{{SNIPPET:Garantie}}</p>");
        var templates = new TemplateService(fixture.Database, session);
        var documents = new DocumentService(fixture.Database, session, new NoopRenderer());

        var updated = templates.RemoveSnippetPlaceholder(template.Id, "garantie");

        Assert.Equal(2, updated.CurrentVersion);
        Assert.DoesNotContain("snippet", templates.GetLatestVersion(template.Id).Html, StringComparison.OrdinalIgnoreCase);
        Assert.False(documents.FindMissingPlaceholders(project.Id, template.Id).Any);
    }

    [Fact]
    public void Textbaustein_platzhalter_entfernen_erfordert_recht()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var (_, template) = Setup(fixture, session, "<p>{{snippet:Garantie}}</p>");
        var leser = fixture.CreateAndLogin("leser", "Leser12345", "Leser");

        Assert.Throws<PermissionDeniedException>(() =>
            new TemplateService(fixture.Database, leser).RemoveSnippetPlaceholder(template.Id, "Garantie"));
    }
}
