using LiftUp.Core.Data;
using LiftUp.Core.Domain;
using Microsoft.Data.Sqlite;

namespace LiftUp.Core.Repositories;

public sealed class ImageRepository
{
    private const string Columns =
        "Id, Name, Category, Description, ProjectId, FileName, FilePath, ContentType, SizeBytes, " +
        "IsArchived, CreatedUtc, CreatedBy, RowVersion";

    public IReadOnlyList<ImageListItem> Search(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid? projectId,
        string? search,
        bool includeArchived)
    {
        var conditions = new List<string>();

        // Ohne Projekt: nur die globale Bibliothek. Mit Projekt: globale plus dessen Bilder.
        if (projectId is null)
        {
            conditions.Add("ProjectId IS NULL");
        }
        else
        {
            conditions.Add("(ProjectId IS NULL OR ProjectId = $projectId)");
        }

        if (!includeArchived)
        {
            conditions.Add("IsArchived = 0");
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            conditions.Add("(Name LIKE $pattern OR Category LIKE $pattern OR Description LIKE $pattern)");
        }

        using var command = Db.Command(connection, $"""
            SELECT Id, Name, Category, ProjectId, FilePath, SizeBytes, IsArchived, CreatedUtc
            FROM Images
            WHERE {string.Join(" AND ", conditions)}
            ORDER BY ProjectId IS NULL DESC, Category COLLATE NOCASE, Name COLLATE NOCASE;
            """, transaction);

        if (projectId is not null)
        {
            command.With("$projectId", projectId.Value.ToString("D"));
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            command.With("$pattern", $"%{search.Trim()}%");
        }

        using var reader = command.ExecuteReader();
        var items = new List<ImageListItem>();
        while (reader.Read())
        {
            var size = reader.Int64(5);
            items.Add(new ImageListItem
            {
                Id = reader.Guid(0),
                Name = reader.Text(1),
                Category = reader.Text(2),
                IsGlobal = reader.IsDBNull(3),
                FilePath = reader.Text(4),
                SizeText = size < 1024 ? $"{size} B" : $"{size / 1024.0:0.0} KB",
                IsArchived = reader.Bool(6),
                CreatedText = reader.Date(7).ToLocalTime().ToString("dd.MM.yyyy HH:mm"),
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
                ($id, $name, $category, $description, $projectId, $fileName, $filePath, $contentType, $sizeBytes,
                 $archived, $createdUtc, $createdBy, 1);
            """, transaction);
        command.With("$id", image.Id.ToString("D"))
            .With("$name", image.Name)
            .With("$category", image.Category)
            .With("$description", image.Description)
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
        ProjectId = reader.TextOrNull(4) is { } projectId ? Guid.Parse(projectId) : null,
        FileName = reader.Text(5),
        FilePath = reader.Text(6),
        ContentType = reader.Text(7),
        SizeBytes = reader.Int64(8),
        IsArchived = reader.Bool(9),
        CreatedUtc = reader.Date(10),
        CreatedBy = reader.TextOrNull(11),
        RowVersion = reader.Int64(12),
    };
}
