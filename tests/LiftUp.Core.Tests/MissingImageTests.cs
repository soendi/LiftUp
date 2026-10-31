using System.Text;
using LiftUp.Core.Documents;
using LiftUp.Core.Domain;
using LiftUp.Core.Services;
using Xunit;

namespace LiftUp.Core.Tests;

public sealed class MissingImageTests
{
    private sealed class RecordingRenderer : IHtmlToPdfRenderer
    {
        public int Calls { get; private set; }

        public Task<byte[]> RenderAsync(string html, PdfPageSettings settings, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(Encoding.UTF8.GetBytes("%PDF-1.4"));
        }
    }

    private static string CreateImage(TestDatabase fixture, string name = "logo.png")
    {
        var path = Path.Combine(fixture.Root, name);
        File.WriteAllBytes(path, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        return path;
    }

    private static (Project Project, DocumentTemplate Template) Setup(
        TestDatabase fixture,
        Session session,
        string html = "<h1>{{project.name}}</h1><p>{{bild:Logo}}</p>")
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
    public void Fehlendes_bild_wird_gemeldet()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var (project, template) = Setup(fixture, session);
        var documents = new DocumentService(fixture.Database, session, new RecordingRenderer());

        var missing = documents.FindMissingImages(project.Id, template.Id);

        Assert.Equal(["Logo"], missing);
    }

    [Fact]
    public void Vorhandenes_bild_wird_nicht_gemeldet()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var (project, template) = Setup(fixture, session);

        new ImageService(fixture.Database, session, Path.Combine(fixture.Root, "bilder"))
            .Import(CreateImage(fixture), new ImageInput { Name = "Logo" });

        var documents = new DocumentService(fixture.Database, session, new RecordingRenderer());

        Assert.Empty(documents.FindMissingImages(project.Id, template.Id));
    }

    [Fact]
    public void Fehlende_datei_gilt_als_fehlendes_bild()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var (project, template) = Setup(fixture, session);

        var image = new ImageService(fixture.Database, session, Path.Combine(fixture.Root, "bilder"))
            .Import(CreateImage(fixture), new ImageInput { Name = "Logo" });
        File.Delete(image.FilePath);

        var documents = new DocumentService(fixture.Database, session, new RecordingRenderer());

        Assert.Equal(["Logo"], documents.FindMissingImages(project.Id, template.Id));
    }

    [Fact]
    public void Pdf_wird_bei_fehlendem_bild_nicht_erzeugt()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var (project, template) = Setup(fixture, session);
        var renderer = new RecordingRenderer();
        var documents = new DocumentService(fixture.Database, session, renderer);

        var exception = Assert.ThrowsAsync<ValidationException>(() =>
            documents.CreatePdfFromSectionsAsync(
                project.Id,
                template.Id,
                [new DocumentSectionContent("Inhalt", "<p>Text</p>")])).GetAwaiter().GetResult();

        Assert.Contains("Logo", exception.Message);
        Assert.Equal(0, renderer.Calls);

        var folder = Path.Combine(project.FolderPath, "03_PDF");
        Assert.False(Directory.Exists(folder) && Directory.GetFiles(folder).Length > 0);
    }

    [Fact]
    public async Task Einfaches_pdf_wird_ebenfalls_blockiert()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var (project, template) = Setup(fixture, session);
        var renderer = new RecordingRenderer();
        var documents = new DocumentService(fixture.Database, session, renderer);

        await Assert.ThrowsAsync<ValidationException>(() =>
            documents.CreatePdfAsync(project.Id, template.Id));

        Assert.Equal(0, renderer.Calls);
    }

    [Fact]
    public void Projektbild_verdeckt_gleichnamiges_globales_bild()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var (project, template) = Setup(fixture, session);
        var images = new ImageService(fixture.Database, session, Path.Combine(fixture.Root, "bilder"));

        images.Import(CreateImage(fixture, "global.png"), new ImageInput { Name = "Logo" });
        var projectImage = images.Import(
            CreateImage(fixture, "projekt.png"),
            new ImageInput { Name = "Logo", ProjectId = project.Id });
        File.Delete(projectImage.FilePath);

        var documents = new DocumentService(fixture.Database, session, new RecordingRenderer());

        Assert.Equal(["Logo"], documents.FindMissingImages(project.Id, template.Id));
    }
}
