using System.Globalization;
using System.Text.RegularExpressions;
using LiftUp.Core.Documents;
using LiftUp.Core.Domain;
using Xunit;

namespace LiftUp.Core.Tests;

/// <summary>Die Tabellenblätter müssen in die A4-Querseite passen.</summary>
public sealed class ProjectTableLayoutTests
{
    private static ProjectTableRow Row() => new()
    {
        Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["AssetName"] = "8609861",
            ["Drive"] = "gut",
            ["EsbaBuffer"] = "in Ordnung",
        },
    };

    [Fact]
    public void Spaltenbreiten_fuellen_genau_die_seite()
    {
        var html = ProjectTableSheet.Render(ProjectTableCatalog.Zustandsanalyse, [Row()], pageBreak: false);

        Assert.Contains("table-layout:fixed", html);
        Assert.Contains("<colgroup>", html);

        var widths = Regex.Matches(html, "<col style=\"width:([0-9.]+)%\"")
            .Select(match => double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture))
            .ToList();

        Assert.Equal(ProjectTableCatalog.Zustandsanalyse.Columns.Count, widths.Count);
        Assert.InRange(widths.Sum(), 99.5, 100.5);
    }

    [Fact]
    public void Lange_ueberschriften_mit_kurzen_werten_stehen_gedreht()
    {
        var html = ProjectTableSheet.Render(ProjectTableCatalog.Zustandsanalyse, [Row()], pageBreak: false);

        Assert.Contains("writing-mode:vertical-rl", html);

        // Lange Bewertungs- und Zahlenspalten sind gedreht …
        Assert.Contains(">Regulator (Geschwindigkeitsbegrenzer)</div>", html);
        Assert.Contains(">Anzahl Wartungen pro Jahr</div>", html);

        // … Textspalten mit langen Einträgen und kurze Titel bleiben waagerecht.
        Assert.Contains(">Anlagebezeichnung</th>", html);
        Assert.Contains(">Baujahr</th>", html);
    }

    [Fact]
    public void Lange_werte_duerfen_umbrechen()
    {
        var html = ProjectTableSheet.Render(ProjectTableCatalog.Zustandsanalyse, [Row()], pageBreak: false);

        Assert.Contains("overflow-wrap:anywhere", html);
    }
}
