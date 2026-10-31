using LiftUp.Core.Domain;
using LiftUp.Core.Services;
using Xunit;

namespace LiftUp.Core.Tests;

public sealed class ImageTagBatchTests
{
    private static ImageService Images(TestDatabase fixture, Session session) =>
        new(fixture.Database, session, Path.Combine(fixture.Root, "bilder"));

    private static string CreateImage(TestDatabase fixture, string name)
    {
        var path = Path.Combine(fixture.Root, name);
        File.WriteAllBytes(path, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        return path;
    }

    private static LibraryImage Import(TestDatabase fixture, ImageService images, string name, string? tags = null) =>
        images.Import(CreateImage(fixture, $"{name}.png"), new ImageInput { Name = name, Tags = tags });

    [Fact]
    public void Schlagwort_wird_fuer_mehrere_bilder_hinzugefuegt()
    {
        using var fixture = new TestDatabase();
        var images = Images(fixture, fixture.LoginAsAdmin());
        var first = Import(fixture, images, "Erstes", "plan");
        var second = Import(fixture, images, "Zweites", "foto");

        var changed = images.SetTags([first.Id, second.Id], "rot, wichtig", null);

        Assert.Equal(2, changed);
        Assert.Equal("plan, rot, wichtig", images.Get(first.Id).Tags);
        Assert.Equal("foto, rot, wichtig", images.Get(second.Id).Tags);
        Assert.Equal(2, images.Search(null, "wichtig").Count);
    }

    [Fact]
    public void Schlagworte_werden_bei_allen_entfernt()
    {
        using var fixture = new TestDatabase();
        var images = Images(fixture, fixture.LoginAsAdmin());
        var first = Import(fixture, images, "Erstes", "rot, blau, logo");
        var second = Import(fixture, images, "Zweites", "rot");

        var changed = images.SetTags([first.Id, second.Id], null, "rot, logo");

        Assert.Equal(2, changed);
        Assert.Equal("blau", images.Get(first.Id).Tags);
        Assert.Null(images.Get(second.Id).Tags);
    }

    [Fact]
    public void Hinzufuegen_und_entfernen_gleichzeitig()
    {
        using var fixture = new TestDatabase();
        var images = Images(fixture, fixture.LoginAsAdmin());
        var image = Import(fixture, images, "Bild", "alt");

        var changed = images.SetTags([image.Id], "neu", "alt");

        Assert.Equal(1, changed);
        Assert.Equal("neu", images.Get(image.Id).Tags);
    }

    [Fact]
    public void Unveraenderte_bilder_zaehlen_nicht()
    {
        using var fixture = new TestDatabase();
        var images = Images(fixture, fixture.LoginAsAdmin());
        var image = Import(fixture, images, "Bild", "rot");

        Assert.Equal(0, images.SetTags([image.Id], "rot", null));
        Assert.Equal(0, images.SetTags([image.Id], null, "blau"));
        Assert.Equal("rot", images.Get(image.Id).Tags);
    }

    [Fact]
    public void Grossschreibung_und_doppelte_werden_vereinheitlicht()
    {
        using var fixture = new TestDatabase();
        var images = Images(fixture, fixture.LoginAsAdmin());
        var image = Import(fixture, images, "Bild", "Rot");

        images.SetTags([image.Id], "rot, NEU, neu", null);

        Assert.Equal("Rot, NEU", images.Get(image.Id).Tags);
    }

    [Fact]
    public void Sammelaenderung_erfordert_das_recht_bilder_zu_verwalten()
    {
        using var fixture = new TestDatabase();
        var image = Import(fixture, Images(fixture, fixture.LoginAsAdmin()), "Bild", "rot");

        var leser = fixture.CreateAndLogin("leser", "Leser12345", "Leser");

        Assert.Throws<PermissionDeniedException>(() =>
            Images(fixture, leser).SetTags([image.Id], "blau", null));
    }
}
