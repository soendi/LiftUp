using LiftUp.Core.Data;
using LiftUp.Core.Domain;
using Microsoft.Data.Sqlite;

namespace LiftUp.Core.Repositories;

public sealed class TemplateRepository
{
    public IReadOnlyList<TemplateListItem> GetAll(SqliteConnection connection, SqliteTransaction? transaction, bool includeInactive)
    {
        var filter = includeInactive ? string.Empty : " AND IsActive = 1";

        using var command = Db.Command(connection, $"""
            SELECT Id, Name, DocumentType, CurrentVersion, IsActive, UpdatedUtc
            FROM Templates
            WHERE IsDeleted = 0{filter}
            ORDER BY Name COLLATE NOCASE;
            """, transaction);

        using var reader = command.ExecuteReader();
        var items = new List<TemplateListItem>();
        while (reader.Read())
        {
            items.Add(new TemplateListItem
            {
                Id = reader.Guid(0),
                Name = reader.Text(1),
                DocumentType = reader.Text(2),
                CurrentVersion = reader.Int32(3),
                IsActive = reader.Bool(4),
                UpdatedText = reader.Date(5).ToLocalTime().ToString("dd.MM.yyyy HH:mm"),
            });
        }

        return items;
    }

    public DocumentTemplate? GetById(SqliteConnection connection, SqliteTransaction? transaction, Guid id)
    {
        using var command = Db.Command(connection, """
            SELECT Id, Name, DocumentType, CurrentVersion, IsActive, CreatedUtc, CreatedBy,
                   UpdatedUtc, UpdatedBy, RowVersion, IsDeleted
            FROM Templates WHERE Id = $id AND IsDeleted = 0;
            """, transaction);
        command.With("$id", id.ToString("D"));
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new DocumentTemplate
        {
            Id = reader.Guid(0),
            Name = reader.Text(1),
            DocumentType = reader.Text(2),
            CurrentVersion = reader.Int32(3),
            IsActive = reader.Bool(4),
            CreatedUtc = reader.Date(5),
            CreatedBy = reader.TextOrNull(6),
            UpdatedUtc = reader.Date(7),
            UpdatedBy = reader.TextOrNull(8),
            RowVersion = reader.Int64(9),
            IsDeleted = reader.Bool(10),
        };
    }

    public TemplateVersion? GetVersion(SqliteConnection connection, SqliteTransaction? transaction, Guid templateId, int version)
    {
        using var command = Db.Command(connection, """
            SELECT Id, TemplateId, Version, Html, CreatedUtc, CreatedBy
            FROM TemplateVersions WHERE TemplateId = $templateId AND Version = $version;
            """, transaction);
        command.With("$templateId", templateId.ToString("D")).With("$version", version);
        using var reader = command.ExecuteReader();
        return reader.Read() ? MapVersion(reader) : null;
    }

    public TemplateVersion? GetLatestVersion(SqliteConnection connection, SqliteTransaction? transaction, Guid templateId)
    {
        using var command = Db.Command(connection, """
            SELECT Id, TemplateId, Version, Html, CreatedUtc, CreatedBy
            FROM TemplateVersions WHERE TemplateId = $templateId
            ORDER BY Version DESC LIMIT 1;
            """, transaction);
        command.With("$templateId", templateId.ToString("D"));
        using var reader = command.ExecuteReader();
        return reader.Read() ? MapVersion(reader) : null;
    }

    public IReadOnlyList<TemplateVersion> GetVersions(SqliteConnection connection, SqliteTransaction? transaction, Guid templateId)
    {
        using var command = Db.Command(connection, """
            SELECT Id, TemplateId, Version, Html, CreatedUtc, CreatedBy
            FROM TemplateVersions WHERE TemplateId = $templateId
            ORDER BY Version DESC;
            """, transaction);
        command.With("$templateId", templateId.ToString("D"));
        using var reader = command.ExecuteReader();
        var versions = new List<TemplateVersion>();
        while (reader.Read())
        {
            versions.Add(MapVersion(reader));
        }

        return versions;
    }

    private static TemplateVersion MapVersion(SqliteDataReader reader) => new()
    {
        Id = reader.Guid(0),
        TemplateId = reader.Guid(1),
        Version = reader.Int32(2),
        Html = reader.Text(3),
        CreatedUtc = reader.Date(4),
        CreatedBy = reader.Text(5),
    };

    public bool NameExists(SqliteConnection connection, SqliteTransaction? transaction, string name, Guid? excludingId)
    {
        using var command = Db.Command(connection, """
            SELECT COUNT(1) FROM Templates
            WHERE Name = $name AND IsDeleted = 0 AND ($exclude IS NULL OR Id <> $exclude);
            """, transaction);
        command.With("$name", name).With("$exclude", excludingId?.ToString("D"));
        return Convert.ToInt64(command.ExecuteScalar()) > 0;
    }

    public void Insert(SqliteConnection connection, SqliteTransaction? transaction, DocumentTemplate template)
    {
        using var command = Db.Command(connection, """
            INSERT INTO Templates
                (Id, Name, DocumentType, CurrentVersion, IsActive, CreatedUtc, CreatedBy,
                 UpdatedUtc, UpdatedBy, RowVersion, IsDeleted)
            VALUES
                ($id, $name, $type, $version, $active, $createdUtc, $createdBy, $updatedUtc, $updatedBy, 1, 0);
            """, transaction);
        command.With("$id", template.Id.ToString("D"))
            .With("$name", template.Name)
            .With("$type", template.DocumentType)
            .With("$version", template.CurrentVersion)
            .With("$active", template.IsActive ? 1 : 0)
            .With("$createdUtc", template.CreatedUtc.ToDb())
            .With("$createdBy", template.CreatedBy)
            .With("$updatedUtc", template.UpdatedUtc.ToDb())
            .With("$updatedBy", template.UpdatedBy);
        command.ExecuteNonQuery();
    }

    public long UpdateMeta(SqliteConnection connection, SqliteTransaction? transaction, DocumentTemplate template)
    {
        using var command = Db.Command(connection, """
            UPDATE Templates
            SET Name = $name, DocumentType = $type, CurrentVersion = $version, IsActive = $active,
                UpdatedUtc = $updatedUtc, UpdatedBy = $updatedBy, RowVersion = RowVersion + 1
            WHERE Id = $id
            RETURNING RowVersion;
            """, transaction);
        command.With("$id", template.Id.ToString("D"))
            .With("$name", template.Name)
            .With("$type", template.DocumentType)
            .With("$version", template.CurrentVersion)
            .With("$active", template.IsActive ? 1 : 0)
            .With("$updatedUtc", template.UpdatedUtc.ToDb())
            .With("$updatedBy", template.UpdatedBy);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    /// <summary>Löscht die Vorlage weich, damit der Abgleich die Löschung sieht.</summary>
    public long SoftDelete(SqliteConnection connection, SqliteTransaction? transaction, Guid id)
    {
        using var command = Db.Command(connection, """
            UPDATE Templates
            SET IsDeleted = 1, UpdatedUtc = $updatedUtc, RowVersion = RowVersion + 1
            WHERE Id = $id AND IsDeleted = 0
            RETURNING RowVersion;
            """, transaction);
        command.With("$id", id.ToString("D")).With("$updatedUtc", DateTime.UtcNow.ToDb());
        return Convert.ToInt64(command.ExecuteScalar());
    }

    public void InsertVersion(SqliteConnection connection, SqliteTransaction? transaction, TemplateVersion version)
    {
        using var command = Db.Command(connection, """
            INSERT INTO TemplateVersions (Id, TemplateId, Version, Html, CreatedUtc, CreatedBy)
            VALUES ($id, $templateId, $version, $html, $createdUtc, $createdBy);
            """, transaction);
        command.With("$id", version.Id.ToString("D"))
            .With("$templateId", version.TemplateId.ToString("D"))
            .With("$version", version.Version)
            .With("$html", version.Html)
            .With("$createdUtc", version.CreatedUtc.ToDb())
            .With("$createdBy", version.CreatedBy);
        command.ExecuteNonQuery();
    }
}
