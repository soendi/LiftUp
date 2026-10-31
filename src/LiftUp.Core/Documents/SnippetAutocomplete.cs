using System.Net;
using System.Text.RegularExpressions;
using LiftUp.Core.Domain;

namespace LiftUp.Core.Documents;

/// <summary>
/// Suche für die „/“-Vervollständigung im Dokumenteditor: findet Textbausteine,
/// deren Name oder Kategorie den getippten Text enthält. Exakte Treffer stehen
/// oben, danach Namen mit dem Text am Anfang, danach beliebige Fundstellen.
/// </summary>
public static class SnippetAutocomplete
{
    public const int DefaultLimit = 7;

    private static readonly Regex TagPattern = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex WhitespacePattern = new(@"\s+", RegexOptions.Compiled);

    public static IReadOnlyList<TextSnippet> Search(
        IEnumerable<TextSnippet> snippets,
        string? query,
        int limit = DefaultLimit)
    {
        var term = (query ?? string.Empty).Trim();
        var matches = new List<(TextSnippet Snippet, int Rank)>();

        foreach (var snippet in snippets)
        {
            if (snippet.IsArchived)
            {
                continue;
            }

            var rank = Rank(snippet, term);
            if (rank >= 0)
            {
                matches.Add((snippet, rank));
            }
        }

        return matches
            .OrderBy(match => match.Rank)
            .ThenBy(match => match.Snippet.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(Math.Max(limit, 1))
            .Select(match => match.Snippet)
            .ToArray();
    }

    /// <summary>Reiner Text eines Bausteins ohne HTML – Grundlage der Inhaltssuche.</summary>
    public static string PlainText(string? html) =>
        WhitespacePattern.Replace(
            WebUtility.HtmlDecode(TagPattern.Replace(html ?? string.Empty, " ")),
            " ").Trim();

    /// <summary>Kurze Textvorschau eines Bausteins für die Vorschlagsliste.</summary>
    public static string Preview(string? html, int maxLength = 70)
    {
        var text = PlainText(html);

        if (text.Length <= maxLength)
        {
            return text;
        }

        return text[..Math.Max(maxLength - 1, 1)].TrimEnd() + "…";
    }

    private static int Rank(TextSnippet snippet, string term)
    {
        if (term.Length == 0)
        {
            return 0;
        }

        var name = snippet.Name;
        if (name.Equals(term, StringComparison.CurrentCultureIgnoreCase))
        {
            return 0;
        }

        if (name.StartsWith(term, StringComparison.CurrentCultureIgnoreCase))
        {
            return 1;
        }

        if (name.Contains(term, StringComparison.CurrentCultureIgnoreCase))
        {
            return 2;
        }

        var category = snippet.Category ?? string.Empty;
        if (category.Contains(term, StringComparison.CurrentCultureIgnoreCase))
        {
            return 3;
        }

        // Auch der Bausteintext zählt: „/lorem“ findet einen Baustein, in dem
        // Lorem ipsum steht, selbst wenn sein Name anders lautet.
        if (PlainText(snippet.Html).Contains(term, StringComparison.CurrentCultureIgnoreCase))
        {
            return 4;
        }

        return -1;
    }
}
