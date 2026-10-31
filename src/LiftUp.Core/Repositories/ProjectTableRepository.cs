using System.Text.Json;
using LiftUp.Core.Data;
using LiftUp.Core.Domain;
using Microsoft.Data.Sqlite;

namespace LiftUp.Core.Repositories;

/// <summary>
/// Zeilen der projektbezogenen Tabellen (Zustandsanalyse, Investitionsplanung). Die Werte
/// liegen als JSON je Zeile, damit sich Spalten ohne Schemaänderung ergänzen lassen.
/// </summary>
public sealed class ProjectTableRepository
{
    public List<ProjectTableRow> GetRows(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid projectId,
        string tableKey)
    {
        using var command = Db.Command(connection, """
            SELECT Id, ProjectId, TableKey, Data, SortOrder, RowVersion
            FROM ProjectTableRows
            WHERE ProjectId = $projectId AND TableKey = $tableKey AND IsDeleted = 0
            ORDER BY SortOrder, Id;
            """, transaction);
        command.With("$projectId", projectId.ToString("D")).With("$tableKey", tableKey);

        using var reader = command.ExecuteReader();
        var rows = new List<ProjectTableRow>();
        while (reader.Read())
        {
            rows.Add(new ProjectTableRow
            {
                Id = reader.Guid(0),
                ProjectId = reader.Guid(1),
                TableKey = reader.Text(2),
                Values = Deserialize(reader.Text(3)),
                SortOrder = reader.Int32(4),
                RowVersion = reader.Int64(5),
            });
        }

        return rows;
    }

    /// <summary>
    /// Schreibt die Zeilen neu. Bisherige Zeilen werden weich gelöscht, damit ein späterer
    /// Abgleich die Änderung nachvollziehen kann.
    /// </summary>
    public List<ProjectTableRow> ReplaceRows(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid projectId,
        string tableKey,
        IReadOnlyList<ProjectTableRowInput> rows,
        string changedBy)
    {
        using (var retire = Db.Command(connection, """
            UPDATE ProjectTableRows
            SET IsDeleted = 1, RowVersion = RowVersion + 1
            WHERE ProjectId = $projectId AND TableKey = $tableKey AND IsDeleted = 0;
            """, transaction))
        {
            retire.With("$projectId", projectId.ToString("D")).With("$tableKey", tableKey);
            retire.ExecuteNonQuery();
        }

        var now = DateTime.UtcNow;
        var saved = new List<ProjectTableRow>(rows.Count);

        for (var index = 0; index < rows.Count; index++)
        {
            var row = new ProjectTableRow
            {
                ProjectId = projectId,
                TableKey = tableKey,
                SortOrder = index,
                Values = new Dictionary<string, string>(
                    rows[index].Values.ToDictionary(pair => pair.Key, pair => pair.Value ?? string.Empty),
                    StringComparer.OrdinalIgnoreCase),
            };

            using var insert = Db.Command(connection, """
                INSERT INTO ProjectTableRows
                    (Id, ProjectId, TableKey, Data, SortOrder, CreatedUtc, CreatedBy, UpdatedUtc, UpdatedBy,
                     RowVersion, IsDeleted)
                VALUES ($id, $projectId, $tableKey, $data, $sortOrder, $createdUtc, $createdBy, $updatedUtc,
                        $updatedBy, 1, 0);
                """, transaction);
            insert.With("$id", row.Id.ToString("D"))
                .With("$projectId", projectId.ToString("D"))
                .With("$tableKey", tableKey)
                .With("$data", JsonSerializer.Serialize(row.Values))
                .With("$sortOrder", row.SortOrder)
                .With("$createdUtc", now.ToDb())
                .With("$createdBy", changedBy)
                .With("$updatedUtc", now.ToDb())
                .With("$updatedBy", changedBy);
            insert.ExecuteNonQuery();

            row.RowVersion = 1;
            saved.Add(row);
        }

        return saved;
    }

    /// <summary>Zeilen ohne Projekt für die PDF-Erzeugung, aber mit bereits offener Verbindung.</summary>
    public IReadOnlyList<ProjectTableRow> GetRowsForPdf(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid projectId,
        string tableKey) => GetRows(connection, transaction, projectId, tableKey);

    private static Dictionary<string, string> Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                   is { } values
                ? new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
