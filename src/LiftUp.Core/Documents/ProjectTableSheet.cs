using System.Globalization;
using System.Text;
using LiftUp.Core.Domain;

namespace LiftUp.Core.Documents;

/// <summary>
/// Erzeugt aus einer projektbezogenen Tabelle ein eigenes Blatt für das PDF – aufgebaut wie die
/// Excel-Vorlage: Gruppenköpfe, Spaltenköpfe und die farbigen Bewertungen. Das Blatt beginnt immer
/// auf einer neuen Seite und steht hinter der Titelseite der Vorlage.
/// </summary>
public static class ProjectTableSheet
{
    /// <summary>HTML des Blattes; leer, wenn es keine Zeilen gibt.</summary>
    /// <param name="pageBreak">Beim Anhängen an das Dokument <c>true</c>, als eigenes Blatt <c>false</c>.</param>
    /// <param name="availableWidthMm">
    /// Druckbare Breite der Seite. Ist sie gesetzt, bekommt die Tabelle genau diese Breite und
    /// die Spalten teilen sie sich nach ihrem Bedarf; ohne Angabe teilen sie sich in Prozent.
    /// </param>
    public static string Render(
        ProjectTableDefinition definition,
        IReadOnlyList<ProjectTableRow> rows,
        bool pageBreak = true,
        double availableWidthMm = 0)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(rows);

        if (rows.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        builder.Append(pageBreak ? "<div style=\"page-break-before: always;\">" : "<div>");
        builder.Append("<h2 style=\"font-size:13pt;margin:0 0 6pt 0;\">")
            .Append(Encode(definition.Title))
            .Append("</h2>");

        AppendLegend(builder, definition);

        // Feste Spaltenbreiten und gedrehte Überschriften halten die Tabelle in der
        // Seitenbreite; ohne table-layout würde der Browser die Spalten wieder aufblähen.
        var tableWidth = availableWidthMm > 0 ? Mm(availableWidthMm) : "100%";
        builder.Append($"<table style=\"width:{tableWidth};table-layout:fixed;border-collapse:collapse;font-size:6.8pt;\">");
        AppendColumnWidths(builder, definition.Columns, availableWidthMm);
        AppendHead(builder, definition.Columns);
        builder.Append("<tbody>");

        foreach (var row in rows)
        {
            builder.Append("<tr>");
            foreach (var column in definition.Columns)
            {
                var value = row.Values.TryGetValue(column.Key, out var found) ? found : string.Empty;

                // Bewertungen zeigen nur die Farbe; was sie bedeutet, steht in der Legende.
                if (column.Kind == ProjectTableColumnKind.Rating)
                {
                    var fill = RatingBackground(value) ?? "#ffffff";
                    builder.Append($"<td style=\"border:1px solid #9aa3ac;padding:1.2pt;background:{fill};box-sizing:border-box;\">&#160;</td>");
                    continue;
                }

                var align = column.Kind == ProjectTableColumnKind.Number ? "right" : "left";
                var style = $"border:1px solid #9aa3ac;padding:1.2pt;text-align:{align};vertical-align:top;overflow-wrap:anywhere;box-sizing:border-box;";

                builder.Append($"<td style=\"{style}\">").Append(EncodeCell(value)).Append("</td>");
            }

            builder.Append("</tr>");
        }

        builder.Append("</tbody></table></div>");
        return builder.ToString();
    }

    /// <summary>
    /// Die erfassten Angaben als Wertetabelle für den Bericht: links die Beschriftung, rechts
    /// die Werte – bis zu zwei Aufzüge nebeneinander. Die Anlagebezeichnung steht als erste
    /// Zeile in der Tabelle, eine Überschrift darüber entfällt. Leere Angaben bleiben als
    /// Zeile stehen, damit der Bericht immer gleich aufgebaut ist.
    /// </summary>
    public static string RenderDetails(ProjectTableDefinition definition, IReadOnlyList<ProjectTableRow> rows)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(rows);

        if (rows.Count == 0 || definition.Columns.Count == 0)
        {
            return string.Empty;
        }

        // Bis zu zwei Aufzüge je Tabelle: Beschriftung, dann die Werte der Aufzüge nebeneinander.
        const int perTable = 2;
        var builder = new StringBuilder();

        for (var start = 0; start < rows.Count; start += perTable)
        {
            var lifts = rows.Skip(start).Take(perTable).ToArray();

            builder.Append("<table style=\"width:100%;border-collapse:collapse;font-size:10.5pt;table-layout:fixed;\">")
                .Append("<colgroup><col style=\"width:46mm;\" />");
            foreach (var _ in lifts)
            {
                builder.Append("<col />");
            }

            builder.Append("</colgroup>");

            foreach (var column in definition.Columns)
            {
                builder.Append("<tr><td style=\"padding:1.8mm 2mm;border-bottom:1px solid #cbd2d9;color:#52606d;vertical-align:top;\">")
                    .Append(Encode(column.Label))
                    .Append(":</td>");

                foreach (var lift in lifts)
                {
                    var value = lift.Values.TryGetValue(column.Key, out var found)
                        ? (found ?? string.Empty).Trim()
                        : string.Empty;

                    builder.Append("<td style=\"padding:1.8mm 2mm;border-bottom:1px solid #cbd2d9;vertical-align:top;overflow-wrap:anywhere;\">")
                        .Append(value.Length == 0 ? "&#160;" : EncodeCell(value))
                        .Append("</td>");
                }

                builder.Append("</tr>");
            }

            builder.Append("</table>");

            if (start + perTable < rows.Count)
            {
                builder.Append("<div style=\"height:6mm;\"></div>");
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Das Blatt als eigenes Dokument für eine liegende Seite. Es wird getrennt gedruckt und
    /// danach an das übrige PDF gehängt, damit die breiten Tabellen Platz haben.
    /// </summary>
    public static string RenderDocument(
        ProjectTableDefinition definition,
        IReadOnlyList<ProjectTableRow> rows,
        PdfPageSettings? settings = null)
    {
        var body = Render(definition, rows, pageBreak: false, availableWidthMm: AvailableWidthMm(settings));
        if (body.Length == 0)
        {
            return string.Empty;
        }

        var margin = (settings?.MarginMm is { } value ? Math.Max(value, 0) : 15)
            .ToString("0.##", CultureInfo.InvariantCulture);
        var size = PageSize(settings);

        return "<!DOCTYPE html>\n<html lang=\"de\">\n<head>\n  <meta charset=\"utf-8\" />\n  <style>\n"
            + $"    @page {{ size: {size}; margin: {margin}mm; }}\n"
            + "    body { font-family: 'Segoe UI', Arial, sans-serif; font-size: 8pt; color: #1f2933; margin: 0; }\n"
            + "  </style>\n</head>\n<body>"
            + body
            + "</body>\n</html>";
    }

    /// <summary>Papierformat des Blattes als CSS-Wert.</summary>
    private static string PageSize(PdfPageSettings? settings)
    {
        var format = string.Equals(settings?.Format, "A3", StringComparison.OrdinalIgnoreCase) ? "A3" : "A4";
        return settings?.Landscape == true ? format + " landscape" : format;
    }

    /// <summary>Hinweis und Legende unter dem Titel – gegliedert wie in der Excel-Vorlage.</summary>
    private static void AppendLegend(StringBuilder builder, ProjectTableDefinition definition)
    {
        if (!string.IsNullOrWhiteSpace(definition.Note))
        {
            builder.Append("<p style=\"font-size:7.5pt;margin:0 0 8pt 0;\">")
                .Append(Encode(definition.Note))
                .Append("</p>");
        }

        if (definition.Legend.Count == 0)
        {
            return;
        }

        builder.Append("<div style=\"font-size:7pt;line-height:1.35;margin:0 0 8pt 0;\">");
        foreach (var item in definition.Legend)
        {
            builder.Append("<div style=\"margin:0 0 1pt 0;\"><b>")
                .Append(Encode(item.Title))
                .Append(":</b> ")
                .Append(Encode(item.Details))
                .Append("</div>");
        }

        builder.Append("</div>");
    }

    /// <summary>
    /// Gewicht einer Spalte: Farbfelder und Zahlen brauchen nur die Breite ihrer Werte,
    /// Textspalten behalten die gewünschte Breite.
    /// </summary>
    private static double ColumnWeight(ProjectTableColumn column) => column.Kind switch
    {
        // Bewertungen zeigen nur ein Farbfeld, Zahlen brauchen wenig Platz.
        ProjectTableColumnKind.Rating => 6,
        ProjectTableColumnKind.Number or ProjectTableColumnKind.Date => 11,

        // Textspalten: die gewünschte Breite der Definition, aber gebremst – die gedrehten
        // Überschriften brauchen keine Breite, und lange Texte brechen in der Zelle um.
        _ => Math.Clamp(column.Width * 0.14, 15, 24),
    };

    /// <summary>
    /// Spaltenbreiten in Prozent der Seitenbreite: Die gedrehten Überschriften brauchen keine
    /// Breite mehr, also bestimmen die Werte den Platz. Zusammen ergibt das genau 100 %,
    /// damit die Tabelle auf die A4-Querseite passt.
    /// </summary>
    private static void AppendColumnWidths(
        StringBuilder builder,
        IReadOnlyList<ProjectTableColumn> columns,
        double availableWidthMm)
    {
        var weights = columns.Select(ColumnWeight).ToArray();
        var total = weights.Sum();
        builder.Append("<colgroup>");
        for (var index = 0; index < columns.Count; index++)
        {
            var share = total > 0 ? weights[index] / total : 1.0 / columns.Count;
            var width = availableWidthMm > 0
                ? Mm(share * availableWidthMm)
                : (share * 100).ToString("0.##", CultureInfo.InvariantCulture) + "%";
            builder.Append($"<col style=\"width:{width}\" />");
        }

        builder.Append("</colgroup>");
    }

    /// <summary>Breite als CSS-Wert in Millimetern.</summary>
    private static string Mm(double value) =>
        value.ToString("0.##", CultureInfo.InvariantCulture) + "mm";

    /// <summary>
    /// Druckbare Breite der Seite in Millimetern – abzüglich der Ränder links und rechts.
    /// Die Tabelle bekommt genau diese Breite, damit rechts nichts abgeschnitten wird.
    /// </summary>
    private static double AvailableWidthMm(PdfPageSettings? settings)
    {
        var (widthMm, heightMm) = string.Equals(settings?.Format, "A3", StringComparison.OrdinalIgnoreCase)
            ? (297.0, 420.0)
            : (210.0, 297.0);

        if (settings?.Landscape == true)
        {
            (widthMm, heightMm) = (heightMm, widthMm);
        }

        var margin = settings?.MarginMm is { } value ? Math.Max(value, 0) : 15;
        return Math.Max(widthMm - 2 * margin, 60);
    }

    /// <summary>
    /// Kopfzeilen: zuerst die Gruppen (mit Spannen), darunter die Spalten selbst – alle
    /// Überschriften um 90° gedreht, damit die schmalen Spalten nicht am Titel hängen.
    /// </summary>
    private static void AppendHead(StringBuilder builder, IReadOnlyList<ProjectTableColumn> columns)
    {
        builder.Append("<thead>");

        if (columns.Any(column => column.Group.Length > 0))
        {
            builder.Append("<tr>");
            var index = 0;
            while (index < columns.Count)
            {
                var group = columns[index].Group;
                var span = 1;
                while (index + span < columns.Count && columns[index + span].Group == group)
                {
                    span++;
                }

                builder.Append($"<th colspan=\"{span}\" style=\"border:1px solid #9aa3ac;padding:1.2pt;background:{GroupBackground(group)};box-sizing:border-box;\">")
                    .Append(Encode(group))
                    .Append("</th>");
                index += span;
            }

            builder.Append("</tr>");
        }

        builder.Append("<tr>");
        foreach (var column in columns)
        {
            const string style =
                "border:1px solid #9aa3ac;padding:1.2pt;text-align:left;vertical-align:bottom;background:#eef1f4;box-sizing:border-box;";

            builder.Append($"<th style=\"{style}\">")
                .Append("<div style=\"writing-mode:vertical-rl;transform:rotate(180deg);white-space:nowrap;\">")
                .Append(Encode(column.Label))
                .Append("</div></th>");
        }

        builder.Append("</tr></thead>");
    }

    /// <summary>Farbe der Bewertung – dieselbe Bedeutung wie in der Legende der Excel-Vorlage.</summary>
    private static string? RatingBackground(string value) => value.Trim().ToLowerInvariant() switch
    {
        "gut" or "in ordnung" or "verfügbar" => "#a9d18e",
        "mässig" or "bedingt verfügbar" => "#ffe699",
        "schlecht" or "nicht in ordnung" or "nicht verfügbar" => "#ff9999",
        _ => null,
    };

    /// <summary>Hintergrund der Gruppenköpfe, an die Farben der Vorlage angelehnt.</summary>
    private static string GroupBackground(string group) => group switch
    {
        "Anlagendaten" => "#f4b183",
        "Analysepunkte" => "#d5c6e0",
        "ESBA" => "#c5d9b0",
        "Ersatzteilverfgbk." => "#cfe0ea",
        "Variante 1 – Modernisierung" => "#9dc3e6",
        "Variante 2 – Ersatzanlage" => "#f0b7b7",
        _ => "#eef1f4",
    };

    /// <summary>
    /// Wie in den übrigen Dokumenten nur die HTML-Zeichen ersetzen: Umlaute bleiben lesbar
    /// und im PDF weiter durchsuchbar.
    /// </summary>
    /// <summary>Zellinhalt: HTML-sicher, mit Zeilenumbrüchen als &lt;br /&gt;.</summary>
    private static string EncodeCell(string? value) => Encode(value)
        .Replace("\r\n", "\n", StringComparison.Ordinal)
        .Replace("\n", "<br />", StringComparison.Ordinal);

    private static string Encode(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&#39;")
            .Replace("\r\n", "<br />")
            .Replace("\n", "<br />");
    }
}
