using LiftUp.Core.Domain;
using LiftUp.Core.Services;
using Xunit;

namespace LiftUp.Core.Tests;

/// <summary>
/// Die Investitionsplanung führt dieselben Aufzüge wie die Zustandsanalyse: Jede Zeile
/// dort legt die passende Zeile an und übernimmt die gemeinsamen Angaben.
/// </summary>
public sealed class ProjectTableSyncTests
{
    private static ProjectInput Input(string folder) => new()
    {
        Name = "Messligfeld 1 und 3, Sennewald",
        FolderPath = folder,
        Status = ProjectStatus.Active,
        CreateFolderIfMissing = true,
    };

    private static ProjectTableRowInput Row(params (string Key, string Value)[] values) =>
        new(values.ToDictionary(pair => pair.Key, pair => pair.Value));

    private static ProjectTableRowInput Lift(string number, string location, string year, string type) =>
        Row(
            ("AssetName", number),
            ("Service", "Schindler"),
            ("Location", location),
            ("Type", type),
            ("Year", year),
            ("Drive", "schlecht"));

    [Fact]
    public void Jede_zeile_der_zustandsanalyse_legt_eine_investitionszeile_an()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var projects = new ProjectService(fixture.Database, session);
        var tables = new ProjectTableService(fixture.Database, session);
        var project = projects.Create(Input(Path.Combine(fixture.Root, "p1")));

        tables.Save(project.Id, ProjectTableCatalog.ZustandsanalyseKey,
        [
            Lift("8609861", "Messligfeldstrasse 1", "1994", "PA"),
            Lift("8609862", "Messligfeldstrasse 3", "2001", "WA"),
        ]);

        var investment = tables.Get(project.Id, ProjectTableCatalog.InvestitionsplanungKey);

        Assert.Equal(2, investment.Count);
        Assert.Equal("8609861", investment[0].Values["AssetName"]);
        Assert.Equal("Messligfeldstrasse 1", investment[0].Values["Location"]);
        Assert.Equal("Schindler", investment[0].Values["Service"]);
        Assert.Equal("1994", investment[0].Values["Year"]);
        Assert.Equal("PA", investment[0].Values["Type"]);
        Assert.Equal("8609862", investment[1].Values["AssetName"]);

        // Eigene Angaben der Investitionsplanung starten leer.
        Assert.Equal(string.Empty, investment[0].Values["ModernMeasure"]);
    }

    [Fact]
    public void Eigene_angaben_der_investitionsplanung_bleiben_erhalten()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var projects = new ProjectService(fixture.Database, session);
        var tables = new ProjectTableService(fixture.Database, session);
        var project = projects.Create(Input(Path.Combine(fixture.Root, "p1")));

        tables.Save(project.Id, ProjectTableCatalog.ZustandsanalyseKey,
            [Lift("8609861", "Messligfeldstrasse 1", "1994", "PA")]);

        tables.Save(project.Id, ProjectTableCatalog.InvestitionsplanungKey,
        [
            Row(
                ("AssetName", "8609861"),
                ("Location", "Messligfeldstrasse 1"),
                ("ModernMeasure", "Steuerung ersetzen"),
                ("ModernCost", "42000"),
                ("ModernNote", "Ersatzteile nicht mehr verfügbar")),
        ]);

        // Der Standort ändert sich in der Zustandsanalyse: Die Massnahme bleibt.
        tables.Save(project.Id, ProjectTableCatalog.ZustandsanalyseKey,
            [Lift("8609861", "Messligfeldstrasse 9", "1994", "PA")]);

        var investment = Assert.Single(tables.Get(project.Id, ProjectTableCatalog.InvestitionsplanungKey));
        Assert.Equal("Messligfeldstrasse 9", investment.Values["Location"]);
        Assert.Equal("Steuerung ersetzen", investment.Values["ModernMeasure"]);
        Assert.Equal("42000", investment.Values["ModernCost"]);
        Assert.Equal("Ersatzteile nicht mehr verfügbar", investment.Values["ModernNote"]);
    }

    [Fact]
    public void Umbenannter_aufzug_behaelt_seine_investitionsangaben()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var projects = new ProjectService(fixture.Database, session);
        var tables = new ProjectTableService(fixture.Database, session);
        var project = projects.Create(Input(Path.Combine(fixture.Root, "p1")));

        tables.Save(project.Id, ProjectTableCatalog.ZustandsanalyseKey,
        [
            Lift("8609861", "Messligfeldstrasse 1", "1994", "PA"),
            Lift("8609862", "Messligfeldstrasse 3", "2001", "WA"),
        ]);

        tables.Save(project.Id, ProjectTableCatalog.InvestitionsplanungKey,
        [
            Row(("AssetName", "8609861"), ("ModernMeasure", "Aggregat ersetzen")),
            Row(("AssetName", "8609862"), ("ModernMeasure", "Türen ersetzen")),
        ]);

        // Nur die erste Anlage wird umbenannt.
        tables.Save(project.Id, ProjectTableCatalog.ZustandsanalyseKey,
        [
            Lift("8609861-N", "Messligfeldstrasse 1", "1994", "PA"),
            Lift("8609862", "Messligfeldstrasse 3", "2001", "WA"),
        ]);

        var investment = tables.Get(project.Id, ProjectTableCatalog.InvestitionsplanungKey);

        Assert.Equal(2, investment.Count);
        Assert.Equal("8609861-N", investment[0].Values["AssetName"]);
        Assert.Equal("Aggregat ersetzen", investment[0].Values["ModernMeasure"]);
        Assert.Equal("8609862", investment[1].Values["AssetName"]);
        Assert.Equal("Türen ersetzen", investment[1].Values["ModernMeasure"]);
    }

    [Fact]
    public void Speichern_der_investitionsplanung_aendert_die_zustandsanalyse_nicht()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var projects = new ProjectService(fixture.Database, session);
        var tables = new ProjectTableService(fixture.Database, session);
        var project = projects.Create(Input(Path.Combine(fixture.Root, "p1")));

        tables.Save(project.Id, ProjectTableCatalog.InvestitionsplanungKey,
            [Row(("AssetName", "8609861"), ("ModernMeasure", "Steuerung ersetzen"))]);

        Assert.Empty(tables.Get(project.Id, ProjectTableCatalog.ZustandsanalyseKey));
    }
}
