using LiftUp.Core.Data;
using LiftUp.Core.Domain;
using Microsoft.Data.Sqlite;

namespace LiftUp.Core.Repositories;

public sealed class TemplateSectionRepository
{
    public IReadOnlyList<TemplateSection> GetByTemplate(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid templateId)
    {
        using var command = Db.Command(connection, """
            SELECT Id, TemplateId, Title, SortOrder
            FROM TemplateSections
            WHERE TemplateId = $templateId
            ORDER BY SortOrder, Title COLLATE NOCASE;
            """, transaction);
        command.With("$templateId", templateId.ToString("D"));

        using var reader = command.ExecuteReader();
        var sections = new List<TemplateSection>();
        while (reader.Read())
        {
            sections.Add(new TemplateSection
            {
                Id = reader.Guid(0),
                TemplateId = reader.Guid(1),
                Title = reader.Text(2),
                SortOrder = reader.GetInt32(3),
            });
        }

        return sections;
    }

    /// <summary>Ersetzt alle Abschnitte einer Vorlage durch die übergebenen Titel.</summary>
    public void ReplaceAll(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid templateId,
        IReadOnlyList<string> titles,
        string actor)
    {
        using (var delete = Db.Command(connection, "DELETE FROM TemplateSections WHERE TemplateId = $templateId;", transaction))
        {
            delete.With("$templateId", templateId.ToString("D"));
            delete.ExecuteNonQuery();
        }

        for (var index = 0; index < titles.Count; index++)
        {
            using var insert = Db.Command(connection, """
                INSERT INTO TemplateSections (Id, TemplateId, Title, SortOrder, CreatedUtc, CreatedBy, RowVersion)
                VALUES ($id, $templateId, $title, $sortOrder, $createdUtc, $createdBy, 1);
                """, transaction);
            insert.With("$id", Guid.NewGuid().ToString("D"))
                .With("$templateId", templateId.ToString("D"))
                .With("$title", titles[index])
                .With("$sortOrder", index)
                .With("$createdUtc", DateTime.UtcNow.ToDb())
                .With("$createdBy", actor);
            insert.ExecuteNonQuery();
        }
    }
}
