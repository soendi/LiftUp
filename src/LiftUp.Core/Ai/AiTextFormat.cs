using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace LiftUp.Core.Ai;

/// <summary>
/// Übersetzt zwischen dem HTML des Editors und dem reinen Text, den das Modell bekommt.
/// Absätze bleiben als Zeilen erhalten: Das Modell soll sie nicht zusammenziehen, und beim
/// Zurückschreiben entsteht für jede Zeile wieder ein Absatz.
/// </summary>
public static class AiTextFormat
{
    private static readonly Regex BreakTag = new("<br\\s*/?>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BlockEnd = new("</(p|div|h[1-6]|blockquote|pre|figcaption)>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LineEnd = new("</(li|tr)>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ListItem = new("<li[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AnyTag = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex Blanks = new(@"[ \t\u00a0]+", RegexOptions.Compiled);

    /// <summary>Reiner Text eines Editorinhalts, Zeile für Zeile; Leerzeilen fallen weg.</summary>
    public static string ToPlainText(string? html)
    {
        var text = html ?? string.Empty;
        text = BreakTag.Replace(text, "\n");
        // Ein Absatz trennt mit Leerzeile – so bleibt die Gliederung für das Modell sichtbar.
        text = BlockEnd.Replace(text, "\n\n");
        text = LineEnd.Replace(text, "\n");
        text = ListItem.Replace(text, "• ");
        text = AnyTag.Replace(text, string.Empty);
        text = WebUtility.HtmlDecode(text).Replace('\u00a0', ' ');

        var lines = text
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n')
            .Select(line => Blanks.Replace(line, " ").Trim())
            .ToArray();

        return string.Join("\n", Collapse(lines));
    }

    /// <summary>Absätze als HTML; jede Zeile bleibt eine Zeile, Leerzeilen trennen Absätze.</summary>
    public static string ToHtml(string? text)
    {
        var lines = (text ?? string.Empty)
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n');

        var builder = new StringBuilder();
        var paragraph = new List<string>();

        void Flush()
        {
            if (paragraph.Count == 0)
            {
                return;
            }

            builder.Append("<p>")
                .Append(string.Join("<br />", paragraph.Select(Escape)))
                .Append("</p>");
            paragraph.Clear();
        }

        foreach (var raw in lines)
        {
            var line = Blanks.Replace(raw, " ").Trim();
            if (line.Length == 0)
            {
                Flush();
                continue;
            }

            paragraph.Add(line);
        }

        Flush();
        return builder.ToString();
    }

    /// <summary>
    /// Maskiert nur, was im HTML eine Bedeutung hätte. Umlaute bleiben stehen – so bleibt der
    /// Text im Editor und im PDF lesbar, auch wenn ihn jemand im HTML ansieht.
    /// </summary>
    private static string Escape(string text) =>
        text.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal);

    /// <summary>Fasst Leerzeilen zusammen und schneidet sie am Anfang und Ende ab.</summary>
    private static IEnumerable<string> Collapse(IReadOnlyList<string> lines)
    {
        var previousBlank = true;
        var lastContent = -1;

        for (var index = 0; index < lines.Count; index++)
        {
            if (lines[index].Length > 0)
            {
                lastContent = index;
            }
        }

        for (var index = 0; index <= lastContent; index++)
        {
            var blank = lines[index].Length == 0;
            if (blank && previousBlank)
            {
                continue;
            }

            yield return lines[index];
            previousBlank = blank;
        }
    }
}
