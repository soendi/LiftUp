namespace LiftUp.Core.Domain;

/// <summary>
/// Schlagworte eines Bildes. Gespeichert werden sie als kommagetrennte Liste;
/// hier werden sie gelesen und für Filter verglichen.
/// </summary>
public static class ImageTags
{
    private static readonly char[] Separators = [',', ';', '\n', '\r', '\t'];

    /// <summary>Zerlegt die gespeicherte Liste in einzelne Schlagworte.</summary>
    public static IReadOnlyList<string> Split(string? tags) =>
        string.IsNullOrWhiteSpace(tags)
            ? []
            : tags.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Schlagworte, die den Suchtext enthalten. Leerer Text liefert alle Einträge –
    /// Grundlage für die Suche im Schlagwortmenü.
    /// </summary>
    public static IReadOnlyList<string> Filter(IEnumerable<string> tags, string? term)
    {
        var value = (term ?? string.Empty).Trim();
        return value.Length == 0
            ? tags.ToArray()
            : tags.Where(tag => tag.Contains(value, StringComparison.CurrentCultureIgnoreCase)).ToArray();
    }

    /// <summary>
    /// Wahr, wenn eines der gewählten Schlagworte am Bild steht. Eine leere Auswahl
    /// bedeutet „nicht filtern“.
    /// </summary>
    public static bool MatchesAny(string? tags, IReadOnlyCollection<string> selected)
    {
        if (selected.Count == 0)
        {
            return true;
        }

        var values = Split(tags);
        return values.Any(value => selected.Contains(value, StringComparer.CurrentCultureIgnoreCase));
    }
}
