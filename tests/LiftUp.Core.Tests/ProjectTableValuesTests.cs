using LiftUp.Core.Domain;
using Xunit;

namespace LiftUp.Core.Tests;

/// <summary>Die „//“-Suche nach den gefüllten Feldern der Zustandsanalyse.</summary>
public sealed class ProjectTableValuesTests
{
    private static ProjectTableRow Row(params (string Key, string Value)[] values) => new()
    {
        TableKey = ProjectTableCatalog.ZustandsanalyseKey,
        Values = values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
    };

    [Fact]
    public void Anlagebezeichnung_wird_ueber_die_bezeichnung_gefunden()
    {
        var rows = new[]
        {
            Row(
                ("AssetName", "Aufzugsanlage 63879387"),
                ("Location", "Messligfeldstrasse 1"),
                ("Service", "Schindler")),
        };

        var value = Assert.Single(ProjectTableValues.Search(ProjectTableCatalog.Zustandsanalyse, rows, "Aufzugsanlage"));

        Assert.Equal("Aufzugsanlage 63879387", value.Text);
        Assert.Equal("Anlagebezeichnung · Zustandsanalyse", value.Detail);
    }

    [Fact]
    public void Mehrere_standorte_stehen_in_der_bezeichnung()
    {
        var rows = new[]
        {
            Row(("AssetName", "Aufzugsanlage 938702809"), ("Location", "Messligfeldstrasse 1")),
            Row(("AssetName", "Aufzugsanlage 938702810"), ("Location", "xy")),
        };

        var found = ProjectTableValues.Search(ProjectTableCatalog.Zustandsanalyse, rows, "Aufzugsanlage");

        Assert.Equal(
            ["Aufzugsanlage 938702809 (Messligfeldstrasse 1)", "Aufzugsanlage 938702810 (xy)"],
            found.Select(value => value.Text));
    }

    [Fact]
    public void Ein_einzelner_standort_bleibt_weg()
    {
        var rows = new[]
        {
            Row(("AssetName", "Aufzugsanlage 63879387"), ("Location", "Messligfeldstrasse 1")),
            Row(("AssetName", "Aufzugsanlage 63879388"), ("Location", "Messligfeldstrasse 1")),
        };

        var found = ProjectTableValues.Search(ProjectTableCatalog.Zustandsanalyse, rows, "Aufzugsanlage");

        Assert.Equal(
            ["Aufzugsanlage 63879387", "Aufzugsanlage 63879388"],
            found.Select(value => value.Text));
    }

    [Fact]
    public void Auch_andere_gefuellte_felder_werden_angeboten()
    {
        var rows = new[]
        {
            Row(("AssetName", "Aufzugsanlage 63879387"), ("Service", "Schindler"), ("Location", "Messligfeldstrasse 1")),
        };

        var value = Assert.Single(ProjectTableValues.Search(ProjectTableCatalog.Zustandsanalyse, rows, "schindler"));

        Assert.Equal("Schindler", value.Text);
        Assert.Equal("Servicefirma · Zustandsanalyse", value.Detail);
    }

    [Fact]
    public void Spaltenueberschrift_findet_die_werte_der_spalte()
    {
        var rows = new[]
        {
            Row(("AssetName", "Aufzugsanlage 63879387"), ("Location", "Messligfeldstrasse 1")),
            Row(("AssetName", "Aufzugsanlage 63879388"), ("Location", "Messligfeldstrasse 1")),
        };

        // „//Standort“ bietet die erfassten Standorte an, obwohl das Wort in keiner Zelle steht.
        var found = ProjectTableValues.Search(ProjectTableCatalog.Zustandsanalyse, rows, "Standort");

        Assert.Equal("Messligfeldstrasse 1", Assert.Single(found).Text);
        Assert.Equal("Standort · Zustandsanalyse", found[0].Detail);
    }

    [Fact]
    public void Teil_der_spaltenueberschrift_findet_die_spalte()
    {
        var rows = new[] { Row(("AssetName", "8609861"), ("Load", "630")) };

        var found = ProjectTableValues.Search(ProjectTableCatalog.Zustandsanalyse, rows, "Nutzlast");

        Assert.Equal("630", Assert.Single(found).Text);
        Assert.Equal("Nutzlast (kg) · Zustandsanalyse", found[0].Detail);
    }

    [Fact]
    public void Suche_ueber_mehrere_tabellen_nennt_die_herkunft()
    {
        var found = ProjectTableValues.Search(
            [
                (ProjectTableCatalog.Zustandsanalyse, new[] { Row(("Year", "1994")) }),
                (ProjectTableCatalog.Investitionsplanung, new[] { Row(("Year", "2026")) }),
                (ProjectTableCatalog.Anlagedaten, new[] { Row(("Year", "1987")) }),
            ],
            "Jahr");

        // „Jahr“ der Investitionsplanung zuerst, dann „Jahrgang“ und „Baujahr“.
        Assert.Equal(["2026", "1987", "1994"], found.Select(value => value.Text));
        Assert.Equal(
            [
                "Jahr · Investitionsplanung",
                "Jahrgang · Anlagedaten",
                "Baujahr · Zustandsanalyse",
            ],
            found.Select(value => value.Detail));
    }

    [Fact]
    public void Nur_zeilen_des_gewaehlten_projekts_zaehlen()
    {
        var project = Guid.NewGuid();
        var other = Guid.NewGuid();

        ProjectTableRow Lift(Guid projectId, string year) => new()
        {
            ProjectId = projectId,
            TableKey = ProjectTableCatalog.ZustandsanalyseKey,
            Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Year"] = year },
        };

        var found = ProjectTableValues.Search(
            [(ProjectTableCatalog.Zustandsanalyse, new[] { Lift(project, "1994"), Lift(other, "2001") })],
            "Jahr",
            projectId: project);

        Assert.Equal("1994", Assert.Single(found).Text);
    }

    [Fact]
    public void Leere_zeilen_und_unbekannte_suche_liefern_nichts()
    {
        var rows = new[] { Row(("AssetName", " "), ("Location", "")) };

        Assert.Empty(ProjectTableValues.Search(ProjectTableCatalog.Zustandsanalyse, rows, "Aufzugsanlage"));
        Assert.Empty(ProjectTableValues.Search(ProjectTableCatalog.Zustandsanalyse, [], "Aufzugsanlage"));
    }
}
