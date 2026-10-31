using System.Text;
using LiftUp.Core.Domain;

namespace LiftUp.Core.Documents;

/// <summary>
/// Baut aus der Titelseite einer Vorlage und den ausgefüllten Abschnitten ein
/// vollständiges HTML-Dokument. Jeder Abschnitt beginnt auf einer neuen Seite,
/// damit Berichte mit Untertiteln sauber umbrechen.
/// </summary>
public static class DocumentComposition
{
    public static string Build(string titlePageHtml, IReadOnlyList<DocumentSectionContent> sections)
    {
        var html = titlePageHtml ?? string.Empty;
        var body = new StringBuilder();

        foreach (var section in sections ?? [])
        {
            if (section is null || string.IsNullOrWhiteSpace(section.Html))
            {
                continue;
            }

            body.Append("<section class=\"liftup-section\" style=\"page-break-before: always;\">");
            if (!string.IsNullOrWhiteSpace(section.Title))
            {
                body.Append("<h2 style=\"font-size:15pt;margin:0 0 4mm 0;\">")
                    .Append(Encode(section.Title.Trim()))
                    .Append("</h2>");
            }

            body.Append(section.Html);
            body.Append("</section>");
        }

        if (body.Length == 0)
        {
            return html;
        }

        var bodyEnd = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        if (bodyEnd >= 0)
        {
            return html.Insert(bodyEnd, body.ToString());
        }

        if (string.IsNullOrWhiteSpace(html))
        {
            return "<!DOCTYPE html><html lang=\"de\"><body>" + body + "</body></html>";
        }

        return "<!DOCTYPE html><html lang=\"de\"><body>" + html + body + "</body></html>";
    }

    private static string Encode(string value) => value
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;");
}
