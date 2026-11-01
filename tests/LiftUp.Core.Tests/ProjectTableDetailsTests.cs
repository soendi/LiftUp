using LiftUp.Core.Documents;
using LiftUp.Core.Domain;
using Xunit;

namespace LiftUp.Core.Tests;

/// <summary>
/// „1. Anlagedaten“ im Bericht: Die erfassten Angaben stehen als Wertetabelle im Dokument,
/// nicht nur die leere Tabelle der Vorlage.
/// </summary>
public sealed class ProjectTableDetailsTests
{
    private static ProjectTableRow Row(params (string Key, string Value)[] values) => new()
    {
        Values = values.ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase),
    };

    [Fact]
    public void Erfasste_angaben_stehen_als_wertetabelle()
    {
        var html = ProjectTableSheet.RenderDetails(
            ProjectTableCatalog.Anlagedaten,
            [Row(("AssetName", "8609861"), ("Year", "1987"), ("Drive", "Hydraulik"))]);

        Assert.Contains("Anlagebezeichnung:", html);
        Assert.Contains("8609861", html);
        Assert.Contains("Jahrgang:", html);
        Assert.Contains("1987", html);
        Assert.Contains("Antrieb:", html);
        Assert.Contains("Hydraulik", html);
    }

    [Fact]
    public void Ein_aufzug_kommt_ohne_ueberschrift()
    {
        var html = ProjectTableSheet.RenderDetails(
            ProjectTableCatalog.Anlagedaten,
            [Row(("AssetName", "8609861"), ("Location", "Messligfeldstrasse 1"))]);

        Assert.DoesNotContain("<p style", html);
    }

    [Fact]
    public void Mehrere_aufzuege_bekommen_eine_ueberschrift()
    {
        var html = ProjectTableSheet.RenderDetails(
            ProjectTableCatalog.Anlagedaten,
            [
                Row(("AssetName", "8609861"), ("Location", "Messligfeldstrasse 1")),
                Row(("AssetName", "8609862"), ("Location", "Bahnhofstrasse 4")),
            ]);

        Assert.Contains("8609861 · Messligfeldstrasse 1", html);
        Assert.Contains("8609862 · Bahnhofstrasse 4", html);

        // Je Aufzug eine eigene Tabelle.
        Assert.Equal(2, html.Split("<table").Length - 1);
    }

    [Fact]
    public void Leere_angaben_bleiben_als_zeile()
    {
        var html = ProjectTableSheet.RenderDetails(ProjectTableCatalog.Anlagedaten, [Row(("AssetName", "8609861"))]);

        // Die Zeile bleibt im Bericht, nur der Wert fehlt – wie in der Vorlage.
        Assert.Contains("Auffahrsockel:", html);
        Assert.Contains("&#160;", html);
    }

    [Fact]
    public void Ohne_zeilen_bleibt_das_dokument_unveraendert()
    {
        Assert.Empty(ProjectTableSheet.RenderDetails(ProjectTableCatalog.Anlagedaten, []));
    }

    [Fact]
    public void Haupttitel_wird_erkannt()
    {
        Assert.True(ProjectAnalysisCatalog.IsAnlagedatenTitle("1. Anlagedaten"));
        Assert.True(ProjectAnalysisCatalog.IsAnlagedatenTitle("Anlagedaten"));
        Assert.False(ProjectAnalysisCatalog.IsAnlagedatenTitle("2. Analyse"));
        Assert.False(ProjectAnalysisCatalog.IsAnlagedatenTitle(string.Empty));
    }
}
