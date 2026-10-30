using LiftUp.Core.Data;
using LiftUp.Core.Domain;
using LiftUp.Core.Repositories;

namespace LiftUp.Core.Services;

/// <summary>Zentrale Verwaltung der Dokumenttypen, die Vorlagen zugeordnet werden.</summary>
public sealed class DocumentTypeService
{
    private readonly Database _database;
    private readonly Session _actor;
    private readonly DocumentTypeRepository _types = new();
    private readonly ChangeLogWriter _changes;

    public DocumentTypeService(Database database, Session actor)
    {
        _database = database;
        _actor = actor;
        _changes = new ChangeLogWriter(database);
    }

    public IReadOnlyList<DocumentType> GetAll(bool includeArchived = false)
    {
        RequireRead();
        using var connection = _database.Open();
        return _types.GetAll(connection, null, includeArchived);
    }

    public DocumentType Create(DocumentTypeInput input)
    {
        _actor.Require(Permission.ManageTemplates);
        ArgumentNullException.ThrowIfNull(input);

        var name = RequireName(input.Name);

        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        if (_types.NameExists(connection, transaction, name, null))
        {
            throw new ValidationException($"Der Dokumenttyp \"{name}\" existiert bereits.");
        }

        var type = new DocumentType
        {
            Id = Guid.NewGuid(),
            Name = name,
            SortOrder = _types.NextSortOrder(connection, transaction),
        };

        _types.Insert(connection, transaction, type);
        _changes.Record(connection, transaction, "DocumentType", type.Id.ToString("D"), "Create",
            new { type.Name }, 1);
        transaction.Commit();

        return type;
    }

    public DocumentType Rename(Guid id, string newName)
    {
        _actor.Require(Permission.ManageTemplates);
        var name = RequireName(newName);

        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        var type = _types.GetById(connection, transaction, id)
                   ?? throw new ValidationException("Der Dokumenttyp wurde nicht gefunden.");

        if (string.Equals(type.Name, name, StringComparison.Ordinal))
        {
            transaction.Commit();
            return type;
        }

        if (_types.NameExists(connection, transaction, name, id))
        {
            throw new ValidationException($"Der Dokumenttyp \"{name}\" existiert bereits.");
        }

        _types.Rename(connection, transaction, id, type.Name, name);

        _changes.Record(connection, transaction, "DocumentType", id.ToString("D"), "Rename",
            new { Old = type.Name, New = name }, 1);
        transaction.Commit();

        return new DocumentType { Id = type.Id, Name = name, SortOrder = type.SortOrder, IsArchived = type.IsArchived };
    }

    public void SetArchived(Guid id, bool archived)
    {
        _actor.Require(Permission.ManageTemplates);

        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        var type = _types.GetById(connection, transaction, id)
                   ?? throw new ValidationException("Der Dokumenttyp wurde nicht gefunden.");

        // Ein Typ, dem noch Vorlagen zugeordnet sind, bleibt aktiv – sonst verschwinden
        // diese Vorlagen aus der Auswahl.
        if (archived)
        {
            var used = _types.CountTemplatesUsing(connection, transaction, type.Name);
            if (used > 0)
            {
                throw new ValidationException(
                    $"Der Typ \"{type.Name}\" wird noch von {used} Vorlage(n) verwendet. Bitte zuerst umbenennen oder löschen.");
            }
        }

        _types.SetArchived(connection, transaction, id, archived);
        _changes.Record(connection, transaction, "DocumentType", id.ToString("D"),
            archived ? "Archive" : "Restore", new { type.Name }, 1);
        transaction.Commit();
    }

    private void RequireRead()
    {
        if (!_actor.Can(Permission.ManageTemplates) && !_actor.Can(Permission.CreateDocuments))
        {
            throw new PermissionDeniedException(Permission.ManageTemplates);
        }
    }

    private static string RequireName(string? name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            throw new ValidationException("Bitte einen Namen für den Dokumenttyp angeben.");
        }

        return trimmed;
    }
}
