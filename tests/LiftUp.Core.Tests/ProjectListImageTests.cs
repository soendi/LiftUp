using LiftUp.Core.Domain;
using LiftUp.Core.Services;
using Xunit;

namespace LiftUp.Core.Tests;

/// <summary>Die Projektliste führt das Projektbild mit, damit die Übersicht es zeigen kann.</summary>
public sealed class ProjectListImageTests
{
    [Fact]
    public void Projektliste_kennt_das_projektbild()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var projects = new ProjectService(fixture.Database, session);

        var folder = Path.Combine(fixture.Root, "quelle");
        Directory.CreateDirectory(folder);
        var image = Path.Combine(folder, "bild.png");
        File.WriteAllBytes(image, [0x89, 0x50, 0x4E, 0x47]);

        var project = projects.Create(new ProjectInput
        {
            Name = "Mit Bild",
            FolderPath = Path.Combine(fixture.Root, "p1"),
            ImagePath = image,
        });

        var item = Assert.Single(projects.Search(new ProjectQuery()));
        Assert.Equal(project.Id, item.Id);
        Assert.Equal(project.ImagePath, item.ImagePath);
        Assert.False(string.IsNullOrWhiteSpace(item.ImagePath));
    }
}
