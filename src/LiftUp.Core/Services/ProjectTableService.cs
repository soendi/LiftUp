using LiftUp.Core.Data;
using LiftUp.Core.Domain;
using LiftUp.Core.Repositories;
using Microsoft.Data.Sqlite;

namespace LiftUp.Core.Services;

/// <summary>
/// Pflegt die projektbezogenen Tabellen (Zustandsanalyse, Investitionsplanung) und
/// protokolliert jede Änderung wie die übrigen Stammdaten.
/// </summary>
public sealed class ProjectTableService
{
    private readonly Database _database;
    private readonly Session _actor;
    private readonly ProjectRepository _projects = new();
    private readonly ProjectTableRepository _tables = new();
    private readonly ChangeLogWriter _changes;

    public ProjectTableService(Database database, Session actor)
    {
        _database = database;
        _actor = actor;
        _changes = new ChangeLogWriter(database, _actor.UserName);
    }

    public IReadOnlyList<ProjectTableRow> Get(Guid projectId, string tableKey)
    {
        _actor.Require(Permission.ViewProjects);
        var definition = RequireDefinition(tableKey);

        using var connection = _database.Open();
        return _tables.GetRows(connection, null, projectId, definition.Key);
    }

    /// <summary>
    /// Ersetzt die Zeilen einer Tabelle. Vollständig leere Zeilen fallen weg, damit ein
    /// versehentlich angelegter leerer Datensatz nicht im PDF auftaucht.
    /// </summary>
    public IReadOnlyList<ProjectTableRow> Save(Guid projectId, string tableKey, IReadOnlyList<ProjectTableRowInput> rows)
    {
        _actor.Require(Permission.EditProjects);
        ArgumentNullException.ThrowIfNull(rows);
        var definition = RequireDefinition(tableKey);

        var prepared = new List<ProjectTableRowInput>();
        foreach (var row in rows)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var column in definition.Columns)
            {
                var value = row.Values.TryGetValue(column.Key, out var found) ? found : null;
                values[column.Key] = (value ?? string.Empty).Trim();
            }

            // Angaben ohne eigene Spalte – etwa die Preise je Massnahme aus dem Rechner –
            // bleiben im Datensatz; im PDF erscheinen nur die Spalten.
            foreach (var (key, value) in row.Values)
            {
                if (!values.ContainsKey(key))
                {
                    values[key] = (value ?? string.Empty).Trim();
                }
            }

            if (values.Values.All(string.IsNullOrEmpty))
            {
                continue;
            }

            prepared.Add(new ProjectTableRowInput(values));
        }

        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        _ = _projects.GetById(connection, transaction, projectId)
            ?? throw new ValidationException("Das Projekt wurde nicht gefunden.");

        var saved = _tables.ReplaceRows(
            connection, transaction, projectId, definition.Key, prepared, _actor.UserName);

        // Alle Tabellen führen dieselben Aufzüge: Je Zeile der Zustandsanalyse entsteht in
        // der Investitionsplanung und in den Anlagedaten die passende Zeile mit den
        // übereinstimmenden Angaben.
        if (string.Equals(definition.Key, ProjectTableCatalog.ZustandsanalyseKey, StringComparison.OrdinalIgnoreCase))
        {
            SyncInvestitionsplanung(connection, transaction, projectId, saved);
            SyncAnlagedaten(connection, transaction, projectId, saved);
        }

        _changes.Record(connection, transaction, "ProjectTable", projectId.ToString("D"), "Update",
            new { Tabelle = definition.Key, Zeilen = saved.Count }, saved.Count == 0 ? 1 : saved[0].RowVersion);
        transaction.Commit();

        return saved;
    }

    /// <summary>
    /// Hält die Investitionsplanung bei den Aufzügen der Zustandsanalyse: gemeinsame Spalten
    /// werden übernommen, eigene Angaben (Massnahme, Kosten, Erläuterung) bleiben stehen.
    /// Zugeordnet wird über die Anlagebezeichnung, bei einer Umbenennung über die Position.
    /// Zeilen zu einem gelöschten Aufzug fallen weg.
    /// </summary>
    private void SyncInvestitionsplanung(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid projectId,
        IReadOnlyList<ProjectTableRow> lifts)
    {
        var definition = ProjectTableCatalog.Investitionsplanung;
        var existing = _tables.GetRows(connection, transaction, projectId, definition.Key);
        var merged = MergeInvestitionsplanung(lifts, existing).Rows;

        _tables.ReplaceRows(connection, transaction, projectId, definition.Key, merged, _actor.UserName);
    }

    /// <summary>
    /// Zeilen der Investitionsplanung zu den Aufzügen der Zustandsanalyse. Was übrig bleibt,
    /// gehört zu einem gelöschten Aufzug – der Aufrufer wirft es weg.
    /// </summary>
    private static (List<ProjectTableRowInput> Rows, List<ProjectTableRow> Removed) MergeInvestitionsplanung(
        IReadOnlyList<ProjectTableRow> lifts,
        IReadOnlyList<ProjectTableRow> existing)
    {
        var definition = ProjectTableCatalog.Investitionsplanung;
        var remaining = new List<ProjectTableRow>(existing);
        var merged = new List<ProjectTableRowInput>(lifts.Count);

        for (var index = 0; index < lifts.Count; index++)
        {
            var lift = lifts[index];
            var name = Value(lift, "AssetName");

            ProjectTableRow? match = name.Length == 0
                ? null
                : remaining.FirstOrDefault(row =>
                    string.Equals(Value(row, "AssetName"), name, StringComparison.OrdinalIgnoreCase));

            // Umbenannt: Die Zeile an derselben Stelle gehört weiterhin zu diesem Aufzug –
            // solange sie nicht schon zu einem anderen Aufzug gehört.
            if (match is null && index < remaining.Count)
            {
                var candidate = remaining[index];
                var candidateName = Value(candidate, "AssetName");
                var claimed = candidateName.Length > 0 && lifts.Any(item =>
                    string.Equals(Value(item, "AssetName"), candidateName, StringComparison.OrdinalIgnoreCase));
                if (!claimed)
                {
                    match = candidate;
                }
            }

            if (match is not null)
            {
                remaining.Remove(match);
            }

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var column in definition.Columns)
            {
                values[column.Key] = match is not null && match.Values.TryGetValue(column.Key, out var previous)
                    ? previous
                    : string.Empty;
            }

            foreach (var key in ProjectTableCatalog.SharedWithInvestitionsplanung)
            {
                values[key] = Value(lift, key);
            }

            merged.Add(new ProjectTableRowInput(values));
        }

        return (merged, remaining);
    }

    /// <summary>
    /// Hält die Anlagedaten („1. Anlagedaten“ der Analyse) bei den Aufzügen der
    /// Zustandsanalyse: Anlagebezeichnung, Aufzugstyp, Jahrgang und Nutzlast werden
    /// übernommen, die übrigen Angaben bleiben stehen. Zugeordnet wird über die
    /// Anlagebezeichnung, bei einer Umbenennung über die Position; Zeilen zu einem gelöschten
    /// Aufzug fallen weg.
    /// </summary>
    private void SyncAnlagedaten(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid projectId,
        IReadOnlyList<ProjectTableRow> lifts)
    {
        var definition = ProjectTableCatalog.Anlagedaten;
        var existing = _tables.GetRows(connection, transaction, projectId, definition.Key);
        var merged = MergeAnlagedaten(lifts, existing).Rows;

        _tables.ReplaceRows(connection, transaction, projectId, definition.Key, merged, _actor.UserName);
    }

    /// <summary>
    /// Zeilen der Anlagedaten zu den Aufzügen der Zustandsanalyse. Was übrig bleibt, gehört zu
    /// einem gelöschten Aufzug – der Aufrufer wirft es weg.
    /// </summary>
    private static (List<ProjectTableRowInput> Rows, List<ProjectTableRow> Removed) MergeAnlagedaten(
        IReadOnlyList<ProjectTableRow> lifts,
        IReadOnlyList<ProjectTableRow> existing)
    {
        var definition = ProjectTableCatalog.Anlagedaten;
        var remaining = new List<ProjectTableRow>(existing);
        var merged = new List<ProjectTableRowInput>(lifts.Count);

        // Einen Standort kennen die Anlagedaten nicht: Bei mehreren Standorten trägt die
        // Anlagebezeichnung den Standort mit – wie in der Vorbelegung des Reiters.
        var locations = lifts
            .Select(row => Value(row, "Location"))
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        var withLocation = locations.Length > 1;
        var labels = lifts.Select(row => Label(row, withLocation)).ToArray();

        for (var index = 0; index < lifts.Count; index++)
        {
            var lift = lifts[index];
            var label = labels[index];
            var name = Value(lift, "AssetName");

            ProjectTableRow? match = null;
            if (label.Length > 0)
            {
                match = remaining.FirstOrDefault(row =>
                    string.Equals(Value(row, "AssetName"), label, StringComparison.OrdinalIgnoreCase));
            }

            // Der Reiter trug bisher nur die Anlagebezeichnung ohne Standort.
            if (match is null && name.Length > 0)
            {
                match = remaining.FirstOrDefault(row =>
                    string.Equals(Value(row, "AssetName"), name, StringComparison.OrdinalIgnoreCase));
            }

            // Umbenannt: Die Zeile an derselben Stelle gehört weiterhin zu diesem Aufzug –
            // solange sie nicht schon zu einem anderen Aufzug gehört.
            if (match is null && index < remaining.Count)
            {
                var candidate = remaining[index];
                var candidateName = Value(candidate, "AssetName");
                var claimed = candidateName.Length > 0
                    && (labels.Contains(candidateName, StringComparer.OrdinalIgnoreCase)
                        || lifts.Any(item => string.Equals(Value(item, "AssetName"), candidateName, StringComparison.OrdinalIgnoreCase)));
                if (!claimed)
                {
                    match = candidate;
                }
            }

            if (match is not null)
            {
                remaining.Remove(match);
            }

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var column in definition.Columns)
            {
                values[column.Key] = match is not null && match.Values.TryGetValue(column.Key, out var previous)
                    ? previous
                    : string.Empty;
            }

            foreach (var (target, source) in ProjectTableCatalog.AnlagedatenFromZustandsanalyse)
            {
                values[target] = string.Equals(target, "AssetName", StringComparison.OrdinalIgnoreCase)
                    ? label
                    : Value(lift, source);
            }

            // Eine Zeile ohne jede Angabe gehört nicht in den Bericht.
            if (values.Values.Any(value => value.Length > 0))
            {
                merged.Add(new ProjectTableRowInput(values));
            }
        }

        return (merged, remaining);
    }

    /// <summary>
    /// Prüft vor dem Speichern der Zustandsanalyse, ob dabei Zeilen anderer Tabellen gelöscht
    /// würden – Aufzüge, die in den neuen Zeilen nicht mehr stehen. Grundlage für die Rückfrage.
    /// </summary>
    public ProjectTableLoss FindLoss(Guid projectId, IReadOnlyList<ProjectTableRowInput> rows)
    {
        _actor.Require(Permission.EditProjects);
        ArgumentNullException.ThrowIfNull(rows);

        // Wie beim Speichern: Vollständig leere Zeilen entstehen gar nicht erst.
        var lifts = rows
            .Where(row => row.Values.Values.Any(value => !string.IsNullOrWhiteSpace(value)))
            .Select(row => new ProjectTableRow
            {
                ProjectId = projectId,
                TableKey = ProjectTableCatalog.ZustandsanalyseKey,
                Values = new Dictionary<string, string>(row.Values, StringComparer.OrdinalIgnoreCase),
            })
            .ToArray();

        using var connection = _database.Open();

        var investment = MergeInvestitionsplanung(lifts,
            _tables.GetRows(connection, null, projectId, ProjectTableCatalog.InvestitionsplanungKey)).Removed;
        var details = MergeAnlagedaten(lifts,
            _tables.GetRows(connection, null, projectId, ProjectTableCatalog.AnlagedatenKey)).Removed;

        if (investment.Count == 0 && details.Count == 0)
        {
            return ProjectTableLoss.Empty;
        }

        var names = investment.Concat(details)
            .Select(row => Value(row, "AssetName"))
            .Select(name => name.Length > 0 ? name : "ohne Anlagebezeichnung")
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        return new ProjectTableLoss(names, investment.Count, details.Count);
    }

    /// <summary>Anlagebezeichnung der Anlagedaten; bei mehreren Standorten mit Standort.</summary>
    private static string Label(ProjectTableRow lift, bool withLocation)
    {
        var name = Value(lift, "AssetName");
        var location = Value(lift, "Location");
        return withLocation && name.Length > 0 && location.Length > 0 ? $"{name} ({location})" : name;
    }

    private static string Value(ProjectTableRow row, string key) =>
        row.Values.TryGetValue(key, out var found) ? (found ?? string.Empty).Trim() : string.Empty;

    private static ProjectTableDefinition RequireDefinition(string tableKey) =>
        ProjectTableCatalog.Find(tableKey ?? string.Empty)
        ?? throw new ValidationException($"Unbekannte Tabelle \"{tableKey}\".");
}
