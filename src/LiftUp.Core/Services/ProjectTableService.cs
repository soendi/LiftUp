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

        // Beide Tabellen führen dieselben Aufzüge: Je Zeile der Zustandsanalyse entsteht in
        // der Investitionsplanung die passende Zeile mit den übereinstimmenden Angaben.
        if (string.Equals(definition.Key, ProjectTableCatalog.ZustandsanalyseKey, StringComparison.OrdinalIgnoreCase))
        {
            SyncInvestitionsplanung(connection, transaction, projectId, saved);
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
    /// </summary>
    private void SyncInvestitionsplanung(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid projectId,
        IReadOnlyList<ProjectTableRow> lifts)
    {
        var definition = ProjectTableCatalog.Investitionsplanung;
        var remaining = new List<ProjectTableRow>(
            _tables.GetRows(connection, transaction, projectId, definition.Key));
        var merged = new List<ProjectTableRowInput>(lifts.Count + remaining.Count);

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

        // Zeilen ohne Aufzug in der Zustandsanalyse bleiben mit ihren Angaben erhalten.
        foreach (var row in remaining)
        {
            merged.Add(new ProjectTableRowInput(
                new Dictionary<string, string>(row.Values, StringComparer.OrdinalIgnoreCase)));
        }

        _tables.ReplaceRows(connection, transaction, projectId, definition.Key, merged, _actor.UserName);
    }

    private static string Value(ProjectTableRow row, string key) =>
        row.Values.TryGetValue(key, out var found) ? (found ?? string.Empty).Trim() : string.Empty;

    private static ProjectTableDefinition RequireDefinition(string tableKey) =>
        ProjectTableCatalog.Find(tableKey ?? string.Empty)
        ?? throw new ValidationException($"Unbekannte Tabelle \"{tableKey}\".");
}
