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
    public void Alle_ueberschriften_stehen_gedreht()
    {
        var html = ProjectTableSheet.Render(ProjectTableCatalog.Zustandsanalyse, [Row()], pageBreak: false);

        // Jede Spalte bekommt die gedrehte Überschrift – auch die kurzen Titel.
        Assert.Equal(
            ProjectTableCatalog.Zustandsanalyse.Columns.Count,
            Regex.Matches(html, "writing-mode:vertical-rl").Count);

        Assert.Contains(">Regulator (Geschwindigkeitsbegrenzer)</div>", html);
        Assert.Contains(">Anzahl Wartungen pro Jahr</div>", html);
        Assert.Contains(">Anlagebezeichnung</div>", html);
        Assert.Contains(">Baujahr</div>", html);
        Assert.Contains(">Ersatzteilverfgbk.</th>", html);
    }

    [Fact]
    public void Bewertungen_zeigen_nur_die_farbe()
    {
        var html = ProjectTableSheet.Render(ProjectTableCatalog.Zustandsanalyse, [Row()], pageBreak: false);

        // Der Text der Bewertung fehlt in der Zelle, die Farbe bleibt.
        Assert.DoesNotContain(">gut</td>", html);
        Assert.DoesNotContain(">in Ordnung</td>", html);
        Assert.Contains("background:#a9d18e;", html);

        // Die Legende erklärt die Farben weiterhin.
        Assert.Contains("grün = gut, gelb = mässig, rot = schlecht.", html);
    }

    [Fact]
    public void Lange_werte_duerfen_umbrechen()
    {
        var html = ProjectTableSheet.Render(ProjectTableCatalog.Zustandsanalyse, [Row()], pageBreak: false);

        Assert.Contains("overflow-wrap:anywhere", html);
    }
}
