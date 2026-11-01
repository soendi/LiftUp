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
        Assert.Equal("Anlagebezeichnung", value.Detail);
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
        Assert.Equal("Servicefirma", value.Detail);
    }

    [Fact]
    public void Leere_zeilen_und_unbekannte_suche_liefern_nichts()
    {
        var rows = new[] { Row(("AssetName", " "), ("Location", "")) };

        Assert.Empty(ProjectTableValues.Search(ProjectTableCatalog.Zustandsanalyse, rows, "Aufzugsanlage"));
        Assert.Empty(ProjectTableValues.Search(ProjectTableCatalog.Zustandsanalyse, [], "Aufzugsanlage"));
    }
}
