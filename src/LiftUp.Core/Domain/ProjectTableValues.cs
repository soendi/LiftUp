namespace LiftUp.Core.Domain;

/// <summary>Ein gefülltes Feld der Datenaufnahme, wie es „//“ zum Einfügen anbietet.</summary>
public sealed record ProjectTableValue(string Text, string Detail);

/// <summary>
/// Suche für die „//“-Eingabe: findet die gefüllten Felder einer Tabelle, damit etwa eine
/// Anlagebezeichnung aus der Zustandsanalyse ohne Abschreiben in den Text kommt.
/// Sind mehrere Standorte erfasst, trägt die Anlagebezeichnung ihren Standort mit
/// („Aufzugsanlage 938702809 (Standort xy)“), damit gleiche Namen unterscheidbar bleiben.
/// </summary>
public static class ProjectTableValues
{
    public const int DefaultLimit = 7;

    /// <summary>Spalte mit der Bezeichnung des Aufzugs.</summary>
    private const string NameColumn = "AssetName";

    /// <summary>Spalte mit dem Standort.</summary>
    private const string LocationColumn = "Location";

    /// <summary>
    /// Treffer zur Eingabe: exakte Werte zuerst, dann Werte mit dem Text am Anfang, danach
    /// beliebige Fundstellen. Jeder Wert erscheint einmal.
    /// </summary>
    public static IReadOnlyList<ProjectTableValue> Search(
        ProjectTableDefinition definition,
        IReadOnlyList<ProjectTableRow> rows,
        string? query,
        int limit = DefaultLimit)
    {
        var term = (query ?? string.Empty).Trim();

        var locations = rows
            .Select(row => Value(row, LocationColumn))
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        var withLocation = locations.Length > 1;
        var seen = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        var matches = new List<(ProjectTableValue Value, int Rank)>();

        foreach (var row in rows)
        {
            foreach (var column in definition.Columns)
            {
                var value = Value(row, column.Key);
                if (value.Length == 0)
                {
                    continue;
                }

                var text = value;
                if (withLocation && column.Key == NameColumn)
                {
                    var location = Value(row, LocationColumn);
                    if (location.Length > 0)
                    {
                        text = $"{value} ({location})";
                    }
                }

                var rank = Rank(value, term);
                if (rank < 0 || !seen.Add(text))
                {
                    continue;
                }

                matches.Add((new ProjectTableValue(text, column.Label), rank));
            }
        }

        return matches
            .OrderBy(match => match.Rank)
            .ThenBy(match => match.Value.Text, StringComparer.CurrentCultureIgnoreCase)
            .Take(Math.Max(limit, 1))
            .Select(match => match.Value)
            .ToArray();
    }

    private static string Value(ProjectTableRow row, string key) =>
        row.Values.TryGetValue(key, out var value) ? value.Trim() : string.Empty;

    private static int Rank(string value, string term)
    {
        if (term.Length == 0)
        {
            return 0;
        }

        if (value.Equals(term, StringComparison.CurrentCultureIgnoreCase))
        {
            return 0;
        }

        if (value.StartsWith(term, StringComparison.CurrentCultureIgnoreCase))
        {
            return 1;
        }

        return value.Contains(term, StringComparison.CurrentCultureIgnoreCase) ? 2 : -1;
    }
}
