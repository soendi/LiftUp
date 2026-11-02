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
    public void Investitionsplanung_druckt_ohne_rechnerspalte()
    {
        var rows = new[]
        {
            new ProjectTableRow
            {
                Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Year"] = "2026",
                    ["AssetName"] = "8609861",
                },
            },
        };

        var html = ProjectTableSheet.Render(
            ProjectTableCatalog.Investitionsplanung, rows, pageBreak: false, availableWidthMm: 260);

        // Der Rechner ist ein Bedienelement der Ansicht und hat im Druck nichts zu suchen:
        // Das Blatt beginnt mit „Jahr“.
        Assert.DoesNotContain("∑", html);

        var headers = Regex.Matches(html, "<th(?=[ >])(?<attrs>[^>]*)>(?<label>.*?)</th>", RegexOptions.Singleline)
            .Select(match => (Attrs: match.Groups["attrs"].Value, Label: match.Groups["label"].Value))
            .ToArray();

        var columns = headers.Where(header => !header.Attrs.Contains("colspan", StringComparison.Ordinal)).ToArray();
        Assert.Equal(ProjectTableCatalog.Investitionsplanung.Columns.Count, columns.Length);
        Assert.Contains("Jahr", columns[0].Label);
        Assert.Contains("Anlagebezeichnung", columns[1].Label);
    }

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
    public void Querformat_tabelle_passt_genau_auf_die_seite()
    {
        var html = ProjectTableSheet.RenderDocument(
            ProjectTableCatalog.Zustandsanalyse,
            [Row()],
            new PdfPageSettings(Landscape: true));

        // A4 quer (297 mm) abzüglich zweimal 15 mm Rand.
        Assert.Contains("width:267mm;table-layout:fixed", html);

        var widths = Regex.Matches(html, "<col style=\"width:([0-9.]+)mm\"")
            .Select(match => double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture))
            .ToList();

        Assert.Equal(ProjectTableCatalog.Zustandsanalyse.Columns.Count, widths.Count);
        Assert.InRange(widths.Sum(), 266.0, 268.0);

        // Keine Spalte ist doppelt so breit, wie sie sein müsste.
        Assert.InRange(widths.Max(), 10, 30);
        Assert.InRange(widths.Min(), 3, 8);
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
