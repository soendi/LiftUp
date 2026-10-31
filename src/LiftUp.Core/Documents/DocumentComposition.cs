using System.Text;
using LiftUp.Core.Domain;

namespace LiftUp.Core.Documents;

/// <summary>
/// Baut aus der Titelseite einer Vorlage und den ausgefüllten Abschnitten ein
/// vollständiges HTML-Dokument. Die Titelseite bleibt für sich allein; die
/// Abschnitte folgen danach im Fluss, mit gleichmäßigem Abstand. Eine Überschrift
/// bleibt dabei mit ihrem ersten Absatz zusammen, damit oben keine einzelne
/// Zeile stehen bleibt.
/// </summary>
public static class DocumentComposition
{
    /// <summary>Ab dieser Länge wird der erste Block nicht mehr mit der Überschrift zusammengehalten.</summary>
    private const int MaxGroupedLength = 1600;

    private const string Style = """
        <style>
          .liftup-section { margin: 0; }
          .liftup-section + .liftup-section { margin-top: 7mm; }
          .liftup-section-first { page-break-before: always; break-before: page; }
          .liftup-section-start { page-break-inside: avoid; break-inside: avoid; }
          .liftup-section h2 {
            break-after: avoid;
            page-break-after: avoid;
            break-inside: avoid;
            margin: 0 0 4mm 0;
            font-size: 15pt;
          }
          .liftup-section p, .liftup-section li { orphans: 2; widows: 2; }
          .liftup-document body { orphans: 2; widows: 2; }
        </style>
        """;

    public static string Build(string titlePageHtml, IReadOnlyList<DocumentSectionContent> sections)
    {
        var html = titlePageHtml ?? string.Empty;
        var body = new StringBuilder();
        var isFirstSection = true;

        foreach (var section in sections ?? [])
        {
            if (section is null || string.IsNullOrWhiteSpace(section.Html))
            {
                continue;
            }

            body.Append("<section class=\"liftup-section")
                .Append(isFirstSection ? " liftup-section-first" : string.Empty)
                .Append("\">");

            var heading = string.IsNullOrWhiteSpace(section.Title)
                ? string.Empty
                : "<h2>" + Encode(section.Title.Trim()) + "</h2>";

            var (firstBlock, rest) = SplitFirstBlock(section.Html);
            if (heading.Length == 0)
            {
                body.Append(section.Html);
            }
            else if (firstBlock.Length > 0 && firstBlock.Length <= MaxGroupedLength)
            {
                // Überschrift und erster Absatz bleiben zusammen: Passt beides nicht mehr
                // aufs Blatt, beginnt der Abschnitt auf der nächsten Seite.
                body.Append("<div class=\"liftup-section-start\">")
                    .Append(heading)
                    .Append(firstBlock)
                    .Append("</div>")
                    .Append(rest);
            }
            else
            {
                body.Append(heading).Append(section.Html);
            }

            body.Append("</section>");
            isFirstSection = false;
        }

        if (body.Length == 0)
        {
            return html;
        }

        var withStyle = InsertStyle(html);
        var bodyEnd = withStyle.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        if (bodyEnd >= 0)
        {
            return withStyle.Insert(bodyEnd, body.ToString());
        }

        return "<!DOCTYPE html><html lang=\"de\"><head>" + Style + "</head><body>"
               + withStyle + body + "</body></html>";
    }

    /// <summary>Legt die Regeln für die Abschnitte in den Kopf des Dokuments.</summary>
    private static string InsertStyle(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        if (html.Contains("liftup-section-first", StringComparison.Ordinal))
        {
            return html;
        }

        var headEnd = html.LastIndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        return headEnd >= 0 ? html.Insert(headEnd, Style) : html;
    }

    /// <summary>
    /// Trennt den ersten Block (etwa einen Absatz) vom Rest. Grundlage ist die
    /// Verschachtelung der Tags; Text vor dem ersten Tag bleibt ungeteilt.
    /// </summary>
    internal static (string First, string Remaining) SplitFirstBlock(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return (string.Empty, html ?? string.Empty);
        }

        var start = 0;
        while (start < html.Length && char.IsWhiteSpace(html[start]))
        {
            start++;
        }

        if (start >= html.Length || html[start] != '<' ||
            html.AsSpan(start).StartsWith("<!--", StringComparison.Ordinal))
        {
            return (string.Empty, html);
        }

        var depth = 0;
        var index = start;
        while (index < html.Length)
        {
            var open = html.IndexOf('<', index);
            if (open < 0)
            {
                return (string.Empty, html);
            }

            var close = html.IndexOf('>', open + 1);
            if (close < 0)
            {
                return (string.Empty, html);
            }

            var tag = html[(open + 1)..close].Trim();

            if (tag.StartsWith("!--", StringComparison.Ordinal))
            {
                var commentEnd = html.IndexOf("-->", open, StringComparison.Ordinal);
                if (commentEnd < 0)
                {
                    return (string.Empty, html);
                }

                index = commentEnd + 3;
                if (depth <= 0)
                {
                    return (html[..index], html[index..]);
                }

                continue;
            }

            var isClosing = tag.StartsWith('/');
            var isSelfClosing = tag.EndsWith('/');
            var name = isClosing ? tag[1..].Trim() : tag;
            var separator = name.IndexOfAny(new[] { ' ', '\t', '\r', '\n', '/' });
            if (separator > 0)
            {
                name = name[..separator];
            }

            name = name.ToLowerInvariant();
            var isVoid = name is "br" or "img" or "hr" or "input" or "meta" or "link" or "source" or "col" or "wbr";

            if (isClosing)
            {
                depth--;
            }
            else if (!isSelfClosing && !isVoid)
            {
                depth++;
            }

            index = close + 1;
            if (depth <= 0)
            {
                return (html[..index], html[index..]);
            }
        }

        return (string.Empty, html);
    }

    private static string Encode(string value) => value
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;");
}
