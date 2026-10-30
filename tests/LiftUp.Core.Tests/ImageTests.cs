using System.Text;
using LiftUp.Core.Documents;
using LiftUp.Core.Domain;
using LiftUp.Core.Services;
using Xunit;

namespace LiftUp.Core.Tests;

public sealed class ImageTests
{
    private sealed class PassthroughRenderer : IHtmlToPdfRenderer
    {
        public Task<byte[]> RenderAsync(string html, PdfPageSettings settings, CancellationToken cancellationToken = default)
            => Task.FromResult(Encoding.UTF8.GetBytes(html));
    }

    private static string CreateImage(TestDatabase fixture, string name = "logo.png")
    {
        var path = Path.Combine(fixture.Root, name);
        File.WriteAllBytes(path, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01, 0x02]);
        return path;
    }

    [Fact]
    public void Bild_wird_in_die_bibliothek_kopiert()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var images = new ImageService(fixture.Database, session, Path.Combine(fixture.Root, "bilder"));

        var source = CreateImage(fixture);
        var image = images.Import(source, new ImageInput { Name = "Logo", Category = "Allgemein" });

        Assert.True(File.Exists(image.FilePath));
        Assert.Equal("image/png", image.ContentType);
        Assert.NotEqual(source, image.FilePath);
        Assert.True(File.Exists(source));

        var listed = images.Search(null, null).Single();
        Assert.Equal("Logo", listed.Name);
        Assert.Equal("{{bild:Logo}}", listed.Placeholder);
        Assert.True(listed.IsGlobal);
    }

    [Fact]
    public void Bild_wird_als_data_uri_in_die_vorlage_eingesetzt()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var images = new ImageService(fixture.Database, session, Path.Combine(fixture.Root, "bilder"));
        images.Import(CreateImage(fixture), new ImageInput { Name = "Logo" });

        var project = new ProjectService(fixture.Database, session).Create(new ProjectInput
        {
            Name = "Mit Logo",
            FolderPath = Path.Combine(fixture.Root, "p1"),
            CreateFolderIfMissing = true,
        });

        var template = new TemplateService(fixture.Database, session).Create(new TemplateInput
        {
            Name = "Mit Bild",
            Html = "<p>{{bild:Logo}}</p>",
        });

        var documents = new DocumentService(fixture.Database, session, new PassthroughRenderer());
        var html = documents.RenderHtml(project.Id, template.Id);

        Assert.Contains("data:image/png;base64,", html);
        Assert.Contains("<img", html);
        Assert.DoesNotContain("{{", html);
    }

    [Fact]
    public void Projektbild_hat_vorrang_vor_gleichnamigem_globalen_bild()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var images = new ImageService(fixture.Database, session, Path.Combine(fixture.Root, "bilder"));
        var project = new ProjectService(fixture.Database, session).Create(new ProjectInput
        {
            Name = "Projekt",
            FolderPath = Path.Combine(fixture.Root, "p1"),
            CreateFolderIfMissing = true,
        });

        images.Import(CreateImage(fixture, "global.png"), new ImageInput { Name = "Logo" });
        var projectImage = images.Import(
            CreateImage(fixture, "projekt.png"),
            new ImageInput { Name = "Logo", ProjectId = project.Id });

        var tag = images.GetImageTag(project.Id, "Logo");

        Assert.NotNull(tag);
        Assert.Equal("image/png", projectImage.ContentType);
        Assert.Contains("data:image/png;base64,", tag);
    }

    [Fact]
    public void Doppelter_name_wird_abgelehnt()
    {
        using var fixture = new TestDatabase();
        var images = new ImageService(fixture.Database, fixture.LoginAsAdmin(), Path.Combine(fixture.Root, "bilder"));
        var source = CreateImage(fixture);

        images.Import(source, new ImageInput { Name = "Logo" });

        var exception = Assert.Throws<ValidationException>(() => images.Import(source, new ImageInput { Name = "Logo" }));
        Assert.Contains("bereits ein Bild", exception.Message);
    }

    [Fact]
    public void Unbekannter_dateityp_wird_abgelehnt()
    {
        using var fixture = new TestDatabase();
        var images = new ImageService(fixture.Database, fixture.LoginAsAdmin(), Path.Combine(fixture.Root, "bilder"));
        var path = Path.Combine(fixture.Root, "notiz.txt");
        File.WriteAllText(path, "kein Bild");

        Assert.Throws<ValidationException>(() => images.Import(path, new ImageInput { Name = "Notiz" }));
    }

    [Fact]
    public void Archiviertes_bild_verschwindet_aus_der_liste()
    {
        using var fixture = new TestDatabase();
        var images = new ImageService(fixture.Database, fixture.LoginAsAdmin(), Path.Combine(fixture.Root, "bilder"));
        var image = images.Import(CreateImage(fixture), new ImageInput { Name = "Logo" });

        images.SetArchived(image.Id, archived: true);

        Assert.Empty(images.Search(null, null));
        Assert.Single(images.Search(null, null, includeArchived: true));
        Assert.Null(images.GetImageTag(null, "Logo"));
    }
}
