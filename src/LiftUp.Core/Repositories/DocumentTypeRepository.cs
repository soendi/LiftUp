using LiftUp.Core.Data;
using LiftUp.Core.Domain;
using Microsoft.Data.Sqlite;

namespace LiftUp.Core.Repositories;

public sealed class DocumentTypeRepository
{
    public IReadOnlyList<DocumentType> GetAll(SqliteConnection connection, SqliteTransaction? transaction, bool includeArchived)
    {
        var filter = includeArchived ? string.Empty : " AND IsArchived = 0";

        using var command = Db.Command(connection, $"""
            SELECT Id, Name, SortOrder, IsArchived
            FROM DocumentTypes
            WHERE 1 = 1{filter}
            ORDER BY SortOrder, Name COLLATE NOCASE;
            """, transaction);

        using var reader = command.ExecuteReader();
        var types = new List<DocumentType>();
        while (reader.Read())
        {
            types.Add(Map(reader));
        }

        return types;
    }

    public DocumentType? GetByName(SqliteConnection connection, SqliteTransaction? transaction, string name)
    {
        using var command = Db.Command(connection, """
            SELECT Id, Name, SortOrder, IsArchived FROM DocumentTypes WHERE Name = $name;
            """, transaction);
        command.With("$name", name);

        using var reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public DocumentType? GetById(SqliteConnection connection, SqliteTransaction? transaction, Guid id)
    {
        using var command = Db.Command(connection, """
            SELECT Id, Name, SortOrder, IsArchived FROM DocumentTypes WHERE Id = $id;
            """, transaction);
        command.With("$id", id.ToString("D"));

        using var reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    private static DocumentType Map(SqliteDataReader reader) => new()
    {
        Id = reader.Guid(0),
        Name = reader.Text(1),
        SortOrder = reader.Int32(2),
        IsArchived = reader.Bool(3),
    };

    public bool NameExists(SqliteConnection connection, SqliteTransaction? transaction, string name, Guid? excludingId)
    {
        using var command = Db.Command(connection, """
            SELECT COUNT(1) FROM DocumentTypes
            WHERE Name = $name AND ($exclude IS NULL OR Id <> $exclude);
            """, transaction);
        command.With("$name", name).With("$exclude", excludingId?.ToString("D"));
        return Convert.ToInt64(command.ExecuteScalar()) > 0;
    }

    public int NextSortOrder(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = Db.Command(connection, "SELECT COALESCE(MAX(SortOrder), 0) + 1 FROM DocumentTypes;", transaction);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public void Insert(SqliteConnection connection, SqliteTransaction? transaction, DocumentType type)
    {
        using var command = Db.Command(connection, """
            INSERT INTO DocumentTypes (Id, Name, SortOrder, IsArchived, CreatedUtc)
            VALUES ($id, $name, $sort, 0, $createdUtc);
            """, transaction);
        command.With("$id", type.Id.ToString("D"))
            .With("$name", type.Name)
            .With("$sort", type.SortOrder)
            .With("$createdUtc", DateTime.UtcNow.ToDb());
        command.ExecuteNonQuery();
    }

    /// <summary>Benennt den Typ um und zieht die Zuordnung bestehender Vorlagen mit.</summary>
    public void Rename(SqliteConnection connection, SqliteTransaction? transaction, Guid id, string oldName, string newName)
    {
        using var typeCommand = Db.Command(connection,
            "UPDATE DocumentTypes SET Name = $name WHERE Id = $id;", transaction);
        typeCommand.With("$id", id.ToString("D")).With("$name", newName);
        typeCommand.ExecuteNonQuery();

        using var templateCommand = Db.Command(connection,
            "UPDATE Templates SET DocumentType = $newName WHERE DocumentType = $oldName;", transaction);
        templateCommand.With("$oldName", oldName).With("$newName", newName);
        templateCommand.ExecuteNonQuery();
    }

    public void SetArchived(SqliteConnection connection, SqliteTransaction? transaction, Guid id, bool archived)
    {
        using var command = Db.Command(connection,
            "UPDATE DocumentTypes SET IsArchived = $archived WHERE Id = $id;", transaction);
        command.With("$id", id.ToString("D")).With("$archived", archived ? 1 : 0);
        command.ExecuteNonQuery();
    }

    public int CountTemplatesUsing(SqliteConnection connection, SqliteTransaction? transaction, string name)
    {
        using var command = Db.Command(connection, """
            SELECT COUNT(1) FROM Templates WHERE DocumentType = $name AND IsDeleted = 0;
            """, transaction);
        command.With("$name", name);
        return Convert.ToInt32(command.ExecuteScalar());
    }
}
