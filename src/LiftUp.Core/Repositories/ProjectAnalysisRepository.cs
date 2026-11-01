using LiftUp.Core.Data;
using LiftUp.Core.Domain;
using Microsoft.Data.Sqlite;

namespace LiftUp.Core.Repositories;

/// <summary>
/// Erfasste Texte der Analyse je Projekt und Unterpunkt. Gespeichert wird nur, was
/// wirklich Text enthält; geänderte Unterpunkte ersetzen den bisherigen Stand.
/// </summary>
public sealed class ProjectAnalysisRepository
{
    public List<ProjectAnalysisText> Get(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid projectId)
    {
        using var command = Db.Command(connection, """
            SELECT Id, ProjectId, SectionKey, Html, UpdatedUtc, UpdatedBy, RowVersion
            FROM ProjectAnalysisTexts
            WHERE ProjectId = $projectId AND IsDeleted = 0
            ORDER BY SectionKey;
            """, transaction);
        command.With("$projectId", projectId.ToString("D"));

        using var reader = command.ExecuteReader();
        var texts = new List<ProjectAnalysisText>();
        while (reader.Read())
        {
            texts.Add(new ProjectAnalysisText
            {
                Id = reader.Guid(0),
                ProjectId = reader.Guid(1),
                SectionKey = reader.Text(2),
                Html = reader.Text(3),
                UpdatedUtc = reader.Date(4),
                UpdatedBy = reader.TextOrNull(5),
                RowVersion = reader.Int64(6),
            });
        }

        return texts;
    }

    /// <summary>
    /// Schreibt die Texte neu. Bisherige Einträge werden weich gelöscht, damit ein späterer
    /// Abgleich die Änderung nachvollziehen kann.
    /// </summary>
    public List<ProjectAnalysisText> Replace(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid projectId,
        IReadOnlyList<ProjectAnalysisTextInput> texts,
        string changedBy)
    {
        using (var retire = Db.Command(connection, """
            UPDATE ProjectAnalysisTexts
            SET IsDeleted = 1, RowVersion = RowVersion + 1
            WHERE ProjectId = $projectId AND IsDeleted = 0;
            """, transaction))
        {
            retire.With("$projectId", projectId.ToString("D"));
            retire.ExecuteNonQuery();
        }

        var now = DateTime.UtcNow;
        var saved = new List<ProjectAnalysisText>(texts.Count);

        foreach (var input in texts)
        {
            var text = new ProjectAnalysisText
            {
                ProjectId = projectId,
                SectionKey = input.SectionKey,
                Html = input.Html,
                UpdatedUtc = now,
                UpdatedBy = changedBy,
            };

            using var insert = Db.Command(connection, """
                INSERT INTO ProjectAnalysisTexts
                    (Id, ProjectId, SectionKey, Html, CreatedUtc, CreatedBy, UpdatedUtc, UpdatedBy,
                     RowVersion, IsDeleted)
                VALUES ($id, $projectId, $sectionKey, $html, $createdUtc, $createdBy, $updatedUtc,
                        $updatedBy, 1, 0);
                """, transaction);
            insert.With("$id", text.Id.ToString("D"))
                .With("$projectId", projectId.ToString("D"))
                .With("$sectionKey", text.SectionKey)
                .With("$html", text.Html)
                .With("$createdUtc", now.ToDb())
                .With("$createdBy", changedBy)
                .With("$updatedUtc", now.ToDb())
                .With("$updatedBy", changedBy);
            insert.ExecuteNonQuery();

            saved.Add(text);
        }

        return saved;
    }
}
