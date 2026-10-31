using LiftUp.Core.Data;
using LiftUp.Core.Domain;
using LiftUp.Core.Repositories;

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

        _changes.Record(connection, transaction, "ProjectTable", projectId.ToString("D"), "Update",
            new { Tabelle = definition.Key, Zeilen = saved.Count }, saved.Count == 0 ? 1 : saved[0].RowVersion);
        transaction.Commit();

        return saved;
    }

    private static ProjectTableDefinition RequireDefinition(string tableKey) =>
        ProjectTableCatalog.Find(tableKey ?? string.Empty)
        ?? throw new ValidationException($"Unbekannte Tabelle \"{tableKey}\".");
}
