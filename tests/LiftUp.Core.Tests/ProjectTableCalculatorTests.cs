using LiftUp.Core.Domain;
using LiftUp.Core.Services;
using Xunit;

namespace LiftUp.Core.Tests;

/// <summary>
/// Die Preise je Massnahme aus dem Fenster „Massnahmen und Kosten“ haben keine eigene Spalte.
/// Sie gehören trotzdem zur Zeile der Investitionsplanung und dürfen beim Speichern der
/// Zustandsanalyse nicht verschwinden – sonst stehen beim nächsten Öffnen des Rechners nur
/// noch die Massnahmen ohne Kosten da.
/// </summary>
public sealed class ProjectTableCalculatorTests
{
    private const string Breakdown =
        "[{\"name\":\"Steuerung ersetzen\",\"price\":\"42000\"},{\"name\":\"Türen ersetzen\",\"price\":\"18000\"}]";

    private static ProjectInput Input(string folder) => new()
    {
        Name = "Rechner-Test",
        FolderPath = folder,
        Status = ProjectStatus.Active,
        CreateFolderIfMissing = true,
    };

    private static ProjectTableRowInput Row(params (string Key, string Value)[] values) =>
        new(values.ToDictionary(pair => pair.Key, pair => pair.Value));

    [Fact]
    public void Preise_je_massnahme_bleiben_beim_speichern_der_zustandsanalyse()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var projects = new ProjectService(fixture.Database, session);
        var tables = new ProjectTableService(fixture.Database, session);
        var project = projects.Create(Input(Path.Combine(fixture.Root, "p1")));

        tables.Save(project.Id, ProjectTableCatalog.ZustandsanalyseKey,
            [Row(("AssetName", "8609861"), ("Location", "Messligfeldstrasse 1"))]);

        tables.Save(project.Id, ProjectTableCatalog.InvestitionsplanungKey,
        [
            Row(
                ("AssetName", "8609861"),
                ("Location", "Messligfeldstrasse 1"),
                ("ModernMeasure", "Steuerung ersetzen\nTüren ersetzen"),
                ("ModernCost", "60'000.00"),
                ("ModernBreakdown", Breakdown)),
        ]);

        // Schon das Speichern der Investitionsplanung muss die Preise behalten.
        var afterSave = tables.Get(project.Id, ProjectTableCatalog.InvestitionsplanungKey);
        Assert.Equal(Breakdown, afterSave[0].Values["ModernBreakdown"]);

        // Die Zustandsanalyse wird erneut gespeichert: Der Abgleich darf die Preise nicht verlieren.
        tables.Save(project.Id, ProjectTableCatalog.ZustandsanalyseKey,
            [Row(("AssetName", "8609861"), ("Location", "Messligfeldstrasse 1"))]);

        var afterSync = tables.Get(project.Id, ProjectTableCatalog.InvestitionsplanungKey);
        Assert.Equal("Steuerung ersetzen\nTüren ersetzen", afterSync[0].Values["ModernMeasure"]);
        Assert.Equal(Breakdown, afterSync[0].Values["ModernBreakdown"]);

        // Auch ein Projektwechsel und ein erneutes Öffnen der Tabelle ändern nichts.
        var reopened = tables.Get(project.Id, ProjectTableCatalog.InvestitionsplanungKey);
        Assert.Equal(Breakdown, reopened[0].Values["ModernBreakdown"]);
    }
}
