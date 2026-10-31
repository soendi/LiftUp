using System.Text;
using LiftUp.Core.Documents;
using LiftUp.Core.Domain;
using LiftUp.Core.Services;
using Xunit;

namespace LiftUp.Core.Tests;

public sealed class DocumentTests
{
    private sealed class FakeRenderer : IHtmlToPdfRenderer
    {
        public string? LastHtml { get; private set; }

        public Task<byte[]> RenderAsync(string html, PdfPageSettings settings, CancellationToken cancellationToken = default)
        {
            LastHtml = html;
            return Task.FromResult(Encoding.UTF8.GetBytes("%PDF-1.4 " + html.Length));
        }
    }

    private static (ProjectService Projects, TemplateService Templates, DocumentService Documents, SnippetService Snippets, FakeRenderer Renderer)
        Services(TestDatabase fixture)
    {
        var session = fixture.LoginAsAdmin();
        var renderer = new FakeRenderer();
        return (
            new ProjectService(fixture.Database, session),
            new TemplateService(fixture.Database, session),
            new DocumentService(fixture.Database, session, renderer),
            new SnippetService(fixture.Database, session),
            renderer);
    }

    private static ProjectInput Input(string folder, string name = "Neubau Musterweg", string customer = "Muster Bau GmbH") => new()
    {
        Name = name,
        Customer = customer,
        Contacts = [new ProjectContactInput { Name = "Frau Beispiel" }],
        FolderPath = folder,
        Status = ProjectStatus.Active,
        CreateFolderIfMissing = true,
    };

    [Fact]
    public void Platzhalter_werden_ersetzt()
    {
        using var fixture = new TestDatabase();
        var services = Services(fixture);
        var project = services.Projects.Create(Input(Path.Combine(fixture.Root, "p1")));
        var template = services.Templates.Create(new TemplateInput
        {
            Name = "Angebot",
            Html = "<h1>{{project.name}}</h1><p>{{project.number}} – {{project.customer}} – {{date}}</p>",
        });

        var html = services.Documents.RenderHtml(project.Id, template.Id);

        Assert.Contains("Neubau Musterweg", html);
        Assert.Contains(project.Number!, html);
        Assert.Contains("Muster Bau GmbH", html);
        Assert.DoesNotContain("{{", html);
    }

    [Fact]
    public void Werte_werden_html_kodiert()
    {
        using var fixture = new TestDatabase();
        var services = Services(fixture);
        var project = services.Projects.Create(Input(Path.Combine(fixture.Root, "p1"), customer: "Müller & Söhne <b>"));
        var template = services.Templates.Create(new TemplateInput
        {
            Name = "Kunde",
            Html = "<p>{{project.customer}}</p>",
        });

        var html = services.Documents.RenderHtml(project.Id, template.Id);

        Assert.Contains("Müller &amp; Söhne &lt;b&gt;", html);
        Assert.DoesNotContain("<b>", html);
    }

    [Fact]
    public void Textbaustein_wird_roh_eingesetzt()
    {
        using var fixture = new TestDatabase();
        var services = Services(fixture);
        var project = services.Projects.Create(Input(Path.Combine(fixture.Root, "p1")));
        services.Snippets.Create(new SnippetInput
        {
            Name = "Gruss",
            Html = "<p><strong>Freundliche Grüsse</strong></p>",
        });

        var template = services.Templates.Create(new TemplateInput
        {
            Name = "Brief",
            Html = "{{snippet:Gruss}}",
        });

        var html = services.Documents.RenderHtml(project.Id, template.Id);

        Assert.Contains("<strong>Freundliche Grüsse</strong>", html);
    }

    [Fact]
    public async Task Pdf_landet_im_projektordner_und_haelt_die_versionsnummer()
    {
        using var fixture = new TestDatabase();
        var services = Services(fixture);
        var project = services.Projects.Create(Input(Path.Combine(fixture.Root, "p1")));
        var template = services.Templates.Create(new TemplateInput
        {
            Name = "Angebot",
            Html = "<h1>{{project.name}}</h1>",
        });

        var document = await services.Documents.CreatePdfAsync(project.Id, template.Id);

        Assert.True(File.Exists(document.FilePath));
        Assert.Equal("03_PDF", Path.GetFileName(Path.GetDirectoryName(document.FilePath)));
        Assert.Equal(1, document.TemplateVersion);
        Assert.Contains("Neubau Musterweg", services.Renderer.LastHtml);

        var history = services.Documents.GetDocuments(project.Id);
        Assert.Single(history);
        Assert.Equal(document.FileName, history[0].FileName);
    }

    [Fact]
    public async Task Alte_pdfs_verweisen_weiter_auf_ihre_vorlagenversion()
    {
        using var fixture = new TestDatabase();
        var services = Services(fixture);
        var project = services.Projects.Create(Input(Path.Combine(fixture.Root, "p1")));
        var template = services.Templates.Create(new TemplateInput
        {
            Name = "Angebot",
            Html = "<p>Fassung eins</p>",
        });

        var oldDocument = await services.Documents.CreatePdfAsync(project.Id, template.Id);

        services.Templates.SaveNewVersion(template.Id, new TemplateInput
        {
            Name = "Angebot",
            Html = "<p>Fassung zwei</p>",
        });

        var newDocument = await services.Documents.CreatePdfAsync(project.Id, template.Id);

        Assert.Equal(1, oldDocument.TemplateVersion);
        Assert.Equal(2, newDocument.TemplateVersion);
        Assert.Contains("Fassung eins", services.Templates.GetVersion(template.Id, 1).Html);
        Assert.Contains("Fassung zwei", services.Templates.GetVersion(template.Id, 2).Html);
        Assert.True(File.Exists(oldDocument.FilePath));
    }

    [Fact]
    public void Neue_version_erhoeht_zaehler_und_haelt_alte_fassung()
    {
        using var fixture = new TestDatabase();
        var services = Services(fixture);
        var template = services.Templates.Create(new TemplateInput { Name = "Lieferschein", Html = "eins" });

        var updated = services.Templates.SaveNewVersion(template.Id, new TemplateInput { Name = "Lieferschein", Html = "zwei" });

        Assert.Equal(2, updated.CurrentVersion);
        Assert.Equal(2, services.Templates.GetVersions(template.Id).Count);
    }

    [Fact]
    public async Task Pdf_erfordert_das_recht_dokumente_zu_erstellen()
    {
        using var fixture = new TestDatabase();
        var admin = fixture.LoginAsAdmin();
        var project = new ProjectService(fixture.Database, admin).Create(Input(Path.Combine(fixture.Root, "p1")));
        var template = new TemplateService(fixture.Database, admin)
            .Create(new TemplateInput { Name = "Angebot", Html = "<p>x</p>" });

        var leser = fixture.CreateAndLogin("leser", "Leser12345", "Leser");
        var documents = new DocumentService(fixture.Database, leser, new FakeRenderer());

        await Assert.ThrowsAsync<PermissionDeniedException>(() =>
            documents.CreatePdfAsync(project.Id, template.Id));
    }
}
