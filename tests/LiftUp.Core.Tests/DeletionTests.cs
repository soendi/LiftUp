using System.Text;
using LiftUp.Core.Documents;
using LiftUp.Core.Domain;
using LiftUp.Core.Services;
using Xunit;

namespace LiftUp.Core.Tests;

public sealed class DeletionTests
{
    private sealed class PassthroughRenderer : IHtmlToPdfRenderer
    {
        public Task<byte[]> RenderAsync(string html, PdfPageSettings settings, CancellationToken cancellationToken = default)
            => Task.FromResult(Encoding.UTF8.GetBytes("%PDF-1.4"));
    }

    private static string CreateImage(TestDatabase fixture, string name = "logo.png")
    {
        var path = Path.Combine(fixture.Root, name);
        File.WriteAllBytes(path, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        return path;
    }

    private static Project CreateProject(TestDatabase fixture, Session session, string name) =>
        new ProjectService(fixture.Database, session).Create(new ProjectInput
        {
            Name = name,
            FolderPath = Path.Combine(fixture.Root, name.Replace(' ', '-')),
            CreateFolderIfMissing = true,
        });

    [Fact]
    public void Textbaustein_laesst_sich_loeschen()
    {
        using var fixture = new TestDatabase();
        var snippets = new SnippetService(fixture.Database, fixture.LoginAsAdmin());
        var snippet = snippets.Create(new SnippetInput { Name = "Gruss", Html = "<p>Servus</p>" });

        snippets.Delete(snippet.Id);

        Assert.Empty(snippets.GetAll(includeArchived: true));
        Assert.Throws<ValidationException>(() => snippets.Get(snippet.Id));
    }

    [Fact]
    public void Gelöschter_textbaustein_erscheint_nicht_mehr_in_dokumenten()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var snippets = new SnippetService(fixture.Database, session);
        var snippet = snippets.Create(new SnippetInput { Name = "Gruss", Html = "<p>Servus</p>" });
        var templates = new TemplateService(fixture.Database, session);
        var template = templates.Create(new TemplateInput { Name = "Brief", Html = "{{snippet:Gruss}}" });
        var project = CreateProject(fixture, session, "Projekt Eins");

        snippets.Delete(snippet.Id);

        var html = new DocumentService(fixture.Database, session, new PassthroughRenderer())
            .RenderHtml(project.Id, template.Id);

        Assert.DoesNotContain("Servus", html);
    }

    [Fact]
    public void Bild_laesst_sich_samt_datei_loeschen()
    {
        using var fixture = new TestDatabase();
        var images = new ImageService(fixture.Database, fixture.LoginAsAdmin(), Path.Combine(fixture.Root, "bilder"));
        var image = images.Import(CreateImage(fixture), new ImageInput { Name = "Logo" });

        var warning = images.Delete(image.Id);

        Assert.Null(warning);
        Assert.False(File.Exists(image.FilePath));
        Assert.Empty(images.Search(null, null));
        Assert.Null(images.GetImageTag(null, "Logo"));
    }

    [Fact]
    public async Task Dokument_laesst_sich_samt_pdf_loeschen()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var project = CreateProject(fixture, session, "Projekt Zwei");
        var template = new TemplateService(fixture.Database, session)
            .Create(new TemplateInput { Name = "Angebot", Html = "<p>x</p>" });

        var documents = new DocumentService(fixture.Database, session, new PassthroughRenderer());
        var document = await documents.CreatePdfAsync(project.Id, template.Id);
        Assert.True(File.Exists(document.FilePath));

        var warning = documents.DeleteDocument(document.Id);

        Assert.Null(warning);
        Assert.False(File.Exists(document.FilePath));
        Assert.Empty(documents.GetDocuments(project.Id));
        Assert.Throws<ValidationException>(() => documents.DeleteDocument(document.Id));
    }

    [Fact]
    public void Vorlage_laesst_sich_loeschen()
    {
        using var fixture = new TestDatabase();
        var templates = new TemplateService(fixture.Database, fixture.LoginAsAdmin());
        var template = templates.Create(new TemplateInput { Name = "Angebot", Html = "<p>x</p>" });

        templates.Delete(template.Id);

        Assert.DoesNotContain(templates.GetAll(), item => item.Id == template.Id);
        Assert.Throws<ValidationException>(() => templates.Get(template.Id));
    }

    [Fact]
    public void Loeschen_erfordert_das_passende_recht()
    {
        using var fixture = new TestDatabase();
        var admin = fixture.LoginAsAdmin();
        var snippet = new SnippetService(fixture.Database, admin)
            .Create(new SnippetInput { Name = "Gruss", Html = "<p>Servus</p>" });
        var template = new TemplateService(fixture.Database, admin)
            .Create(new TemplateInput { Name = "Angebot", Html = "<p>x</p>" });
        var images = new ImageService(fixture.Database, admin, Path.Combine(fixture.Root, "bilder"));
        var image = images.Import(CreateImage(fixture), new ImageInput { Name = "Logo" });

        var leser = fixture.CreateAndLogin("leser", "Leser12345", "Leser");

        Assert.Throws<PermissionDeniedException>(() =>
            new SnippetService(fixture.Database, leser).Delete(snippet.Id));
        Assert.Throws<PermissionDeniedException>(() =>
            new TemplateService(fixture.Database, leser).Delete(template.Id));
        Assert.Throws<PermissionDeniedException>(() =>
            new ImageService(fixture.Database, leser, Path.Combine(fixture.Root, "bilder")).Delete(image.Id));
    }
}
