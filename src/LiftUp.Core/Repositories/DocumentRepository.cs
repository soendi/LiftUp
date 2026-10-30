using LiftUp.Core.Data;
using LiftUp.Core.Domain;
using Microsoft.Data.Sqlite;

namespace LiftUp.Core.Repositories;

public sealed class DocumentRepository
{
    public void Insert(SqliteConnection connection, SqliteTransaction? transaction, ProjectDocument document)
    {
        using var command = Db.Command(connection, """
            INSERT INTO ProjectDocuments
                (Id, ProjectId, TemplateId, TemplateName, TemplateVersion, FileName, FilePath,
                 SizeBytes, CreatedUtc, CreatedBy, RowVersion, IsDeleted)
            VALUES
                ($id, $projectId, $templateId, $templateName, $templateVersion, $fileName, $filePath,
                 $sizeBytes, $createdUtc, $createdBy, 1, 0);
            """, transaction);
        command.With("$id", document.Id.ToString("D"))
            .With("$projectId", document.ProjectId.ToString("D"))
            .With("$templateId", document.TemplateId.ToString("D"))
            .With("$templateName", document.TemplateName)
            .With("$templateVersion", document.TemplateVersion)
            .With("$fileName", document.FileName)
            .With("$filePath", document.FilePath)
            .With("$sizeBytes", document.SizeBytes)
            .With("$createdUtc", document.CreatedUtc.ToDb())
            .With("$createdBy", document.CreatedBy);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<ProjectDocument> GetByProject(SqliteConnection connection, SqliteTransaction? transaction, Guid projectId)
    {
        using var command = Db.Command(connection, """
            SELECT Id, ProjectId, TemplateId, TemplateName, TemplateVersion, FileName, FilePath,
                   SizeBytes, CreatedUtc, CreatedBy
            FROM ProjectDocuments
            WHERE ProjectId = $projectId AND IsDeleted = 0
            ORDER BY CreatedUtc DESC;
            """, transaction);
        command.With("$projectId", projectId.ToString("D"));
        using var reader = command.ExecuteReader();
        var documents = new List<ProjectDocument>();
        while (reader.Read())
        {
            documents.Add(new ProjectDocument
            {
                Id = reader.Guid(0),
                ProjectId = reader.Guid(1),
                TemplateId = reader.Guid(2),
                TemplateName = reader.Text(3),
                TemplateVersion = reader.Int32(4),
                FileName = reader.Text(5),
                FilePath = reader.Text(6),
                SizeBytes = reader.Int64(7),
                CreatedUtc = reader.Date(8),
                CreatedBy = reader.Text(9),
            });
        }

        return documents;
    }
}

public sealed class SnippetRepository
{
    public IReadOnlyList<TextSnippetListItem> GetAll(SqliteConnection connection, SqliteTransaction? transaction, bool includeArchived)
    {
        var filter = includeArchived ? string.Empty : " AND IsArchived = 0";

        using var command = Db.Command(connection, $"""
            SELECT Id, Name, Category, Version, IsArchived, UpdatedUtc
            FROM TextSnippets
            WHERE 1 = 1{filter}
            ORDER BY Category COLLATE NOCASE, Name COLLATE NOCASE;
            """, transaction);

        using var reader = command.ExecuteReader();
        var items = new List<TextSnippetListItem>();
        while (reader.Read())
        {
            items.Add(new TextSnippetListItem
            {
                Id = reader.Guid(0),
                Name = reader.Text(1),
                Category = reader.Text(2),
                Version = reader.Int32(3),
                IsArchived = reader.Bool(4),
                UpdatedText = reader.Date(5).ToLocalTime().ToString("dd.MM.yyyy HH:mm"),
            });
        }

        return items;
    }

    public TextSnippet? GetById(SqliteConnection connection, SqliteTransaction? transaction, Guid id)
    {
        using var command = Db.Command(connection, """
            SELECT Id, Name, Category, Html, IsArchived, Version, CreatedUtc, CreatedBy,
                   UpdatedUtc, UpdatedBy, RowVersion
            FROM TextSnippets WHERE Id = $id;
            """, transaction);
        command.With("$id", id.ToString("D"));
        using var reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public IReadOnlyDictionary<string, string> GetActiveByName(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = Db.Command(connection, "SELECT Name, Html FROM TextSnippets WHERE IsArchived = 0;", transaction);
        using var reader = command.ExecuteReader();
        var snippets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            snippets[reader.Text(0)] = reader.Text(1);
        }

        return snippets;
    }

    public bool NameExists(SqliteConnection connection, SqliteTransaction? transaction, string name, Guid? excludingId)
    {
        using var command = Db.Command(connection, """
            SELECT COUNT(1) FROM TextSnippets
            WHERE Name = $name AND ($exclude IS NULL OR Id <> $exclude);
            """, transaction);
        command.With("$name", name).With("$exclude", excludingId?.ToString("D"));
        return Convert.ToInt64(command.ExecuteScalar()) > 0;
    }

    public void Insert(SqliteConnection connection, SqliteTransaction? transaction, TextSnippet snippet)
    {
        using var command = Db.Command(connection, """
            INSERT INTO TextSnippets
                (Id, Name, Category, Html, IsArchived, Version, CreatedUtc, CreatedBy,
                 UpdatedUtc, UpdatedBy, RowVersion)
            VALUES
                ($id, $name, $category, $html, 0, 1, $createdUtc, $createdBy, $updatedUtc, $updatedBy, 1);
            """, transaction);
        command.With("$id", snippet.Id.ToString("D"))
            .With("$name", snippet.Name)
            .With("$category", snippet.Category)
            .With("$html", snippet.Html)
            .With("$createdUtc", snippet.CreatedUtc.ToDb())
            .With("$createdBy", snippet.CreatedBy)
            .With("$updatedUtc", snippet.UpdatedUtc.ToDb())
            .With("$updatedBy", snippet.UpdatedBy);
        command.ExecuteNonQuery();
    }

    public long Update(SqliteConnection connection, SqliteTransaction? transaction, TextSnippet snippet)
    {
        using var command = Db.Command(connection, """
            UPDATE TextSnippets
            SET Name = $name, Category = $category, Html = $html, IsArchived = $archived,
                Version = Version + 1, UpdatedUtc = $updatedUtc, UpdatedBy = $updatedBy, RowVersion = RowVersion + 1
            WHERE Id = $id
            RETURNING RowVersion;
            """, transaction);
        command.With("$id", snippet.Id.ToString("D"))
            .With("$name", snippet.Name)
            .With("$category", snippet.Category)
            .With("$html", snippet.Html)
            .With("$archived", snippet.IsArchived ? 1 : 0)
            .With("$updatedUtc", DateTime.UtcNow.ToDb())
            .With("$updatedBy", snippet.UpdatedBy);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static TextSnippet Map(SqliteDataReader reader) => new()
    {
        Id = reader.Guid(0),
        Name = reader.Text(1),
        Category = reader.TextOrNull(2),
        Html = reader.Text(3),
        IsArchived = reader.Bool(4),
        Version = reader.Int32(5),
        CreatedUtc = reader.Date(6),
        CreatedBy = reader.TextOrNull(7),
        UpdatedUtc = reader.Date(8),
        UpdatedBy = reader.TextOrNull(9),
        RowVersion = reader.Int64(10),
    };
}
