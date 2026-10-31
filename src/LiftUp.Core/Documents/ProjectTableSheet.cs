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
    public static string Render(ProjectTableDefinition definition, IReadOnlyList<ProjectTableRow> rows)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(rows);

        if (rows.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        builder.Append("<div style=\"page-break-before: always;\">");
        builder.Append("<h2 style=\"font-size:13pt;margin:0 0 6pt 0;\">")
            .Append(Encode(definition.Title))
            .Append("</h2>");

        if (!string.IsNullOrWhiteSpace(definition.Note))
        {
            builder.Append("<p style=\"font-size:7.5pt;margin:0 0 8pt 0;\">")
                .Append(Encode(definition.Note))
                .Append("</p>");
        }

        builder.Append("<table style=\"width:100%;border-collapse:collapse;font-size:7.5pt;\">");
        AppendHead(builder, definition.Columns);
        builder.Append("<tbody>");

        foreach (var row in rows)
        {
            builder.Append("<tr>");
            foreach (var column in definition.Columns)
            {
                var value = row.Values.TryGetValue(column.Key, out var found) ? found : string.Empty;
                var align = column.Kind == ProjectTableColumnKind.Number ? "right" : "left";
                var background = column.Kind == ProjectTableColumnKind.Rating ? RatingBackground(value) : null;
                var style = background is null
                    ? $"border:1px solid #9aa3ac;padding:3pt;text-align:{align};"
                    : $"border:1px solid #9aa3ac;padding:3pt;text-align:{align};background:{background};";

                builder.Append($"<td style=\"{style}\">").Append(Encode(value)).Append("</td>");
            }

            builder.Append("</tr>");
        }

        builder.Append("</tbody></table></div>");
        return builder.ToString();
    }

    /// <summary>Kopfzeilen: zuerst die Gruppen (mit Spannen), darunter die Spalten selbst.</summary>
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

                builder.Append($"<th colspan=\"{span}\" style=\"border:1px solid #9aa3ac;padding:3pt;background:{GroupBackground(group)};\">")
                    .Append(Encode(group))
                    .Append("</th>");
                index += span;
            }

            builder.Append("</tr>");
        }

        builder.Append("<tr>");
        foreach (var column in columns)
        {
            builder.Append("<th style=\"border:1px solid #9aa3ac;padding:3pt;text-align:left;background:#eef1f4;\">")
                .Append(Encode(column.Label))
                .Append("</th>");
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
        "Ersatzteilverfügbarkeit" => "#cfe0ea",
        "Variante 1 – Modernisierung" => "#9dc3e6",
        "Variante 2 – Ersatzanlage" => "#f0b7b7",
        _ => "#eef1f4",
    };

    /// <summary>
    /// Wie in den übrigen Dokumenten nur die HTML-Zeichen ersetzen: Umlaute bleiben lesbar
    /// und im PDF weiter durchsuchbar.
    /// </summary>
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
