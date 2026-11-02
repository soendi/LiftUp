using LiftUp.Core.Domain;
using LiftUp.Core.Services;
using Xunit;

namespace LiftUp.Core.Tests;

public sealed class ProjectVariantStorageTests
{
    [Fact]
    public void Benennung_der_Varianten_bleibt_am_Projekt_gespeichert()
    {
        using var fixture = new TestDatabase();
        var service = new ProjectService(fixture.Database, fixture.LoginAsAdmin());
        var project = service.Create(new ProjectInput
        {
            Name = "Sanierung Musterweg",
            Number = "P-100",
            FolderPath = Path.Combine(fixture.Root, "Projekte", "P-100"),
            Status = ProjectStatus.Active,
            CreateFolderIfMissing = true,
        });

        service.SaveVariantLabels(project.Id, " Instandsetzung 2027 ", "Neuanlage 2028-2029");

        var reloaded = service.GetProject(project.Id);
        Assert.Equal("Instandsetzung 2027", reloaded.Variant1Label);
        Assert.Equal("Neuanlage 2028-2029", reloaded.Variant2Label);
    }

    [Fact]
    public void Neues_Projekt_hat_keine_eigene_Benennung()
    {
        using var fixture = new TestDatabase();
        var service = new ProjectService(fixture.Database, fixture.LoginAsAdmin());
        var project = service.Create(new ProjectInput
        {
            Name = "Neubau",
            Number = "P-101",
            FolderPath = Path.Combine(fixture.Root, "Projekte", "P-101"),
            Status = ProjectStatus.Active,
            CreateFolderIfMissing = true,
        });

        var reloaded = service.GetProject(project.Id);
        Assert.Null(reloaded.Variant1Label);
        Assert.Null(reloaded.Variant2Label);

        // Ohne eigene Benennung greift der Standard im Kopf der Tabelle.
        var definition = ProjectTableCatalog.WithVariantLabels(
            ProjectTableCatalog.Investitionsplanung,
            reloaded.Variant1Label,
            reloaded.Variant2Label,
            new DateOnly(2026, 11, 2));
        Assert.Contains(definition.Columns, column => column.Group == "Variante 1 – Modernisierung 2026-2027");
    }
}
