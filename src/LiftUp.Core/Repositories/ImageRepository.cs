using LiftUp.Core.Data;
using LiftUp.Core.Domain;
using Microsoft.Data.Sqlite;

namespace LiftUp.Core.Repositories;

public sealed class ImageRepository
{
    private const string Columns =
        "Id, Name, Category, Description, Tags, ProjectId, FileName, FilePath, ContentType, SizeBytes, " +
        "IsArchived, CreatedUtc, CreatedBy, RowVersion";

    /// <summary>
    /// Sucht Bilder. Ohne Projekt enthält das Ergebnis die globale Bibliothek, mit Projekt
    /// zusätzlich dessen eigene Bilder. <paramref name="allProjects"/> hebt die
    /// Projektgrenze auf (für „&amp;&amp;bild“ im Dokumenteditor).
    /// </summary>
    public IReadOnlyList<ImageListItem> Search(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid? projectId,
        string? search,
        bool includeArchived,
        bool allProjects = false)
    {
        var conditions = new List<string>();

        if (!allProjects)
        {
            // Ohne Projekt: nur die globale Bibliothek. Mit Projekt: globale plus dessen Bilder.
            conditions.Add(projectId is null
                ? "ProjectId IS NULL"
                : "(ProjectId IS NULL OR ProjectId = $projectId)");
        }

        if (!includeArchived)
        {
            conditions.Add("IsArchived = 0");
        }

        // Jedes Suchwort muss irgendwo passen: Name, Kategorie, Schlagworte oder Beschreibung.
        var terms = (search ?? string.Empty)
            .Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();

        for (var index = 0; index < terms.Length; index++)
        {
            conditions.Add(
                $"(Name LIKE $term{index} OR Category LIKE $term{index} OR Tags LIKE $term{index} " +
                $"OR Description LIKE $term{index})");
        }

        using var command = Db.Command(connection, $"""
            SELECT Id, Name, Category, Tags, ProjectId, FilePath, SizeBytes, IsArchived, CreatedUtc
            FROM Images
            WHERE {string.Join(" AND ", conditions)}
            ORDER BY ProjectId IS NULL DESC, Category COLLATE NOCASE, Name COLLATE NOCASE;
            """, transaction);

        if (!allProjects && projectId is not null)
        {
            command.With("$projectId", projectId.Value.ToString("D"));
        }

        for (var index = 0; index < terms.Length; index++)
        {
            command.With($"$term{index}", $"%{terms[index]}%");
        }

        using var reader = command.ExecuteReader();
        var items = new List<ImageListItem>();
        while (reader.Read())
        {
            var size = reader.Int64(6);
            items.Add(new ImageListItem
            {
                Id = reader.Guid(0),
                Name = reader.Text(1),
                Category = reader.Text(2),
                Tags = reader.Text(3),
                IsGlobal = reader.IsDBNull(4),
                FilePath = reader.Text(5),
                SizeText = size < 1024 ? $"{size} B" : $"{size / 1024.0:0.0} KB",
                IsArchived = reader.Bool(7),
                CreatedText = reader.Date(8).ToLocalTime().ToString("dd.MM.yyyy HH:mm"),
            });
        }

        return items;
    }

    public LibraryImage? GetById(SqliteConnection connection, SqliteTransaction? transaction, Guid id)
    {
        using var command = Db.Command(connection, $"SELECT {Columns} FROM Images WHERE Id = $id;", transaction);
        command.With("$id", id.ToString("D"));
        using var reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    /// <summary>Alle aktiven Bilder, die für ein Projekt sichtbar sind (globale zuerst).</summary>
    public IReadOnlyList<LibraryImage> GetActive(SqliteConnection connection, SqliteTransaction? transaction, Guid? projectId)
    {
        using var command = Db.Command(connection, $"""
            SELECT {Columns} FROM Images
            WHERE IsArchived = 0 AND (ProjectId IS NULL OR ProjectId = $projectId)
            ORDER BY CASE WHEN ProjectId IS NULL THEN 0 ELSE 1 END, Name COLLATE NOCASE;
            """, transaction);
        command.With("$projectId", projectId?.ToString("D"));

        using var reader = command.ExecuteReader();
        var images = new List<LibraryImage>();
        while (reader.Read())
        {
            images.Add(Map(reader));
        }

        return images;
    }

    public bool NameExists(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string name,
        Guid? projectId,
        Guid? excludingId)
    {
        using var command = Db.Command(connection, """
            SELECT COUNT(1) FROM Images
            WHERE Name = $name
              AND ((ProjectId IS NULL AND $projectId IS NULL) OR ProjectId = $projectId)
              AND ($exclude IS NULL OR Id <> $exclude);
            """, transaction);
        command.With("$name", name)
            .With("$projectId", projectId?.ToString("D"))
            .With("$exclude", excludingId?.ToString("D"));
        return Convert.ToInt64(command.ExecuteScalar()) > 0;
    }

    public void Insert(SqliteConnection connection, SqliteTransaction? transaction, LibraryImage image)
    {
        using var command = Db.Command(connection, $"""
            INSERT INTO Images ({Columns}) VALUES
                ($id, $name, $category, $description, $tags, $projectId, $fileName, $filePath, $contentType, $sizeBytes,
                 $archived, $createdUtc, $createdBy, 1);
            """, transaction);
        command.With("$id", image.Id.ToString("D"))
            .With("$name", image.Name)
            .With("$category", image.Category)
            .With("$description", image.Description)
            .With("$tags", image.Tags)
            .With("$projectId", image.ProjectId?.ToString("D"))
            .With("$fileName", image.FileName)
            .With("$filePath", image.FilePath)
            .With("$contentType", image.ContentType)
            .With("$sizeBytes", image.SizeBytes)
            .With("$archived", image.IsArchived ? 1 : 0)
            .With("$createdUtc", image.CreatedUtc.ToDb())
            .With("$createdBy", image.CreatedBy);
        command.ExecuteNonQuery();
    }

    public long Update(SqliteConnection connection, SqliteTransaction? transaction, LibraryImage image)
    {
        using var command = Db.Command(connection, """
            UPDATE Images
            SET Name = $name, Category = $category, Description = $description, Tags = $tags,
                RowVersion = RowVersion + 1
            WHERE Id = $id
            RETURNING RowVersion;
            """, transaction);
        command.With("$id", image.Id.ToString("D"))
            .With("$name", image.Name)
            .With("$category", image.Category)
            .With("$description", image.Description)
            .With("$tags", image.Tags);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    public void Delete(SqliteConnection connection, SqliteTransaction? transaction, Guid id)
    {
        using var command = Db.Command(connection, "DELETE FROM Images WHERE Id = $id;", transaction);
        command.With("$id", id.ToString("D"));
        command.ExecuteNonQuery();
    }

    public long SetArchived(SqliteConnection connection, SqliteTransaction? transaction, Guid id, bool archived)
    {
        using var command = Db.Command(connection, """
            UPDATE Images SET IsArchived = $archived, RowVersion = RowVersion + 1
            WHERE Id = $id RETURNING RowVersion;
            """, transaction);
        command.With("$id", id.ToString("D")).With("$archived", archived ? 1 : 0);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static LibraryImage Map(SqliteDataReader reader) => new()
    {
        Id = reader.Guid(0),
        Name = reader.Text(1),
        Category = reader.TextOrNull(2),
        Description = reader.TextOrNull(3),
        Tags = reader.TextOrNull(4),
        ProjectId = reader.TextOrNull(5) is { } projectId ? Guid.Parse(projectId) : null,
        FileName = reader.Text(6),
        FilePath = reader.Text(7),
        ContentType = reader.Text(8),
        SizeBytes = reader.Int64(9),
        IsArchived = reader.Bool(10),
        CreatedUtc = reader.Date(11),
        CreatedBy = reader.TextOrNull(12),
        RowVersion = reader.Int64(13),
    };
}
