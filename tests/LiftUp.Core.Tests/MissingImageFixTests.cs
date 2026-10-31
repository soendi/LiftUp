using System.Text;
using LiftUp.Core.Documents;
using LiftUp.Core.Domain;
using LiftUp.Core.Services;
using Xunit;

namespace LiftUp.Core.Tests;

public sealed class MissingImageFixTests
{
    private sealed class NoopRenderer : IHtmlToPdfRenderer
    {
        public Task<byte[]> RenderAsync(string html, PdfPageSettings settings, CancellationToken cancellationToken = default)
            => Task.FromResult(Encoding.UTF8.GetBytes("%PDF-1.4"));
    }

    private static string CreateImage(TestDatabase fixture, string name)
    {
        var path = Path.Combine(fixture.Root, name);
        File.WriteAllBytes(path, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01]);
        return path;
    }

    private static (Project Project, DocumentTemplate Template) Setup(
        TestDatabase fixture,
        Session session,
        string html = "<p>{{bild:Logo}}</p>")
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
    public void Ersetzen_aktualisiert_ein_fehlendes_bild()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var (project, template) = Setup(fixture, session);
        var images = new ImageService(fixture.Database, session, Path.Combine(fixture.Root, "bilder"));
        var documents = new DocumentService(fixture.Database, session, new NoopRenderer());

        var image = images.Import(CreateImage(fixture, "alt.png"), new ImageInput { Name = "Logo" });
        File.Delete(image.FilePath);
        Assert.Equal(["Logo"], documents.FindMissingImages(project.Id, template.Id));

        var replacement = CreateImage(fixture, "neu.png");
        var updated = images.ReplaceFile(project.Id, "Logo", replacement);

        Assert.Equal("Logo", updated.Name);
        Assert.True(File.Exists(updated.FilePath));
        Assert.Equal("image/png", updated.ContentType);
        Assert.Empty(documents.FindMissingImages(project.Id, template.Id));
        Assert.Single(images.Search(project.Id, null));
    }

    [Fact]
    public void Ersetzen_legt_ein_bild_an_wenn_keines_existiert()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var (project, template) = Setup(fixture, session);
        var images = new ImageService(fixture.Database, session, Path.Combine(fixture.Root, "bilder"));
        var documents = new DocumentService(fixture.Database, session, new NoopRenderer());

        Assert.Equal(["Logo"], documents.FindMissingImages(project.Id, template.Id));

        images.ReplaceFile(project.Id, "Logo", CreateImage(fixture, "logo.png"));

        Assert.Empty(documents.FindMissingImages(project.Id, template.Id));
        Assert.Equal("Logo", Assert.Single(images.Search(project.Id, null)).Name);
    }

    [Fact]
    public void Platzhalter_entfernen_erzeugt_eine_neue_version()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var (project, template) = Setup(fixture, session, "<p>Text</p><p>{{bild:Logo}}</p>");
        var templates = new TemplateService(fixture.Database, session);
        var documents = new DocumentService(fixture.Database, session, new NoopRenderer());

        var updated = templates.RemoveImagePlaceholder(template.Id, "Logo");

        Assert.Equal(2, updated.CurrentVersion);
        Assert.DoesNotContain("{{bild:Logo}}", templates.GetLatestVersion(template.Id).Html);
        Assert.Contains("<p>Text</p>", templates.GetLatestVersion(template.Id).Html);
        Assert.Empty(documents.FindMissingImages(project.Id, template.Id));
        Assert.Contains("{{bild:Logo}}", templates.GetVersion(template.Id, 1).Html);
    }

    [Fact]
    public void Platzhalter_entfernen_ignoriert_grossschreibung_und_leerzeichen()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var (_, template) = Setup(fixture, session, "<p>{{ BILD : logo }}</p>");
        var templates = new TemplateService(fixture.Database, session);

        templates.RemoveImagePlaceholder(template.Id, "LOGO");

        Assert.DoesNotContain("bild", templates.GetLatestVersion(template.Id).Html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ersetzen_erfordert_das_recht_bilder_zu_verwalten()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var (project, _) = Setup(fixture, session);
        var leser = fixture.CreateAndLogin("leser", "Leser12345", "Leser");

        Assert.Throws<PermissionDeniedException>(() =>
            new ImageService(fixture.Database, leser, Path.Combine(fixture.Root, "bilder"))
                .ReplaceFile(project.Id, "Logo", CreateImage(fixture, "logo.png")));
    }

    [Fact]
    public void Platzhalter_entfernen_erfordert_das_recht_vorlagen_zu_verwalten()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var (_, template) = Setup(fixture, session);
        var leser = fixture.CreateAndLogin("leser", "Leser12345", "Leser");

        Assert.Throws<PermissionDeniedException>(() =>
            new TemplateService(fixture.Database, leser).RemoveImagePlaceholder(template.Id, "Logo"));
    }
}
