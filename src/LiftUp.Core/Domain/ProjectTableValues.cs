namespace LiftUp.Core.Domain;

/// <summary>Ein gefülltes Feld einer Tabelle, wie es „//“ zum Einfügen anbietet.</summary>
public sealed record ProjectTableValue(string Text, string Detail);

/// <summary>
/// Suche für die „//“-Eingabe: findet die gefüllten Felder der Tabellen, damit etwa eine
/// Anlagebezeichnung aus der Zustandsanalyse ohne Abschreiben in den Text kommt. Gesucht wird
/// über die Spaltenüberschrift („//Standort“) und über den Wert selbst („//Aufzugsanlage“);
/// der Zusatztext nennt Spalte und Herkunft („Jahr · Investitionsplanung“).
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

    /// <summary>Treffer in einer einzelnen Tabelle.</summary>
    public static IReadOnlyList<ProjectTableValue> Search(
        ProjectTableDefinition definition,
        IReadOnlyList<ProjectTableRow> rows,
        string? query,
        int limit = DefaultLimit) =>
        Search([(definition, rows)], query, limit);

    /// <summary>
    /// Treffer über mehrere Tabellen. Jeder Vorschlag nennt seine Herkunft, damit „//Jahr“ das
    /// Baujahr der Anlage und das Jahr der Investitionsplanung auseinanderhält. Mit
    /// <paramref name="projectId"/> zählen nur Zeilen dieses Projekts – Werte aus einem anderen
    /// Projekt erscheinen nie, auch wenn sie versehentlich mitgeliefert würden.
    /// </summary>
    public static IReadOnlyList<ProjectTableValue> Search(
        IReadOnlyList<(ProjectTableDefinition Definition, IReadOnlyList<ProjectTableRow> Rows)> tables,
        string? query,
        int limit = DefaultLimit,
        Guid? projectId = null)
    {
        ArgumentNullException.ThrowIfNull(tables);

        var term = (query ?? string.Empty).Trim();
        var matches = new List<(ProjectTableValue Value, int Rank, int Table)>(tables.Count);

        for (var table = 0; table < tables.Count; table++)
        {
            var (definition, rows) = tables[table];

            // Jede Tabelle führt ihre Werte einmal auf; gleiche Werte in zwei Tabellen bleiben
            // getrennt, weil sie aus verschiedenen Stellen stammen.
            var seen = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
            var locations = rows
                .Select(row => Value(row, LocationColumn))
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            var withLocation = locations.Length > 1;

            foreach (var row in rows)
            {
                // Nur das gewählte Projekt; Zeilen ohne Projektangabe (etwa Vorschlagszeilen)
                // bleiben zulässig.
                if (projectId is { } selected && row.ProjectId != Guid.Empty && row.ProjectId != selected)
                {
                    continue;
                }

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

                    // Zuerst die Spaltenüberschrift: „//Standort“ bietet alle erfassten Standorte
                    // an, auch wenn in keiner Zelle das Wort „Standort“ steht. Danach der Wert.
                    var rank = Rank(column.Label, term);
                    if (rank < 0)
                    {
                        rank = Rank(value, term);
                    }

                    if (rank < 0 || !seen.Add(text))
                    {
                        continue;
                    }

                    matches.Add((
                        new ProjectTableValue(text, $"{column.Label} · {definition.Key}"),
                        rank,
                        table));
                }
            }
        }

        return matches
            .OrderBy(match => match.Rank)
            .ThenBy(match => match.Value.Text, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(match => match.Table)
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
