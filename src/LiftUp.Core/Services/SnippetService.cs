using LiftUp.Core.Data;
using LiftUp.Core.Domain;
using LiftUp.Core.Repositories;

namespace LiftUp.Core.Services;

/// <summary>Zentrale Textbausteine, die in Vorlagen über <c>{{snippet:Name}}</c> erscheinen.</summary>
public sealed class SnippetService
{
    private readonly Database _database;
    private readonly Session _actor;
    private readonly SnippetRepository _snippets = new();
    private readonly ChangeLogWriter _changes;

    public SnippetService(Database database, Session actor)
    {
        _database = database;
        _actor = actor;
        _changes = new ChangeLogWriter(database, _actor.UserName);
    }

    public IReadOnlyList<TextSnippetListItem> GetAll(bool includeArchived = false)
    {
        _actor.Require(Permission.ViewProjects);
        using var connection = _database.Open();
        return _snippets.GetAll(connection, null, includeArchived);
    }

    public TextSnippet Get(Guid id)
    {
        _actor.Require(Permission.ViewProjects);
        using var connection = _database.Open();
        return _snippets.GetById(connection, null, id)
               ?? throw new ValidationException("Der Textbaustein wurde nicht gefunden.");
    }

    public TextSnippet Create(SnippetInput input)
    {
        _actor.Require(Permission.ManageSnippets);
        ArgumentNullException.ThrowIfNull(input);

        var name = RequireName(input.Name);
        var now = DateTime.UtcNow;

        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        if (_snippets.NameExists(connection, transaction, name, null))
        {
            throw new ValidationException($"Es gibt bereits einen Textbaustein mit dem Namen \"{name}\".");
        }

        var snippet = new TextSnippet
        {
            Name = name,
            Category = string.IsNullOrWhiteSpace(input.Category) ? null : input.Category.Trim(),
            Html = input.Html ?? string.Empty,
            CreatedUtc = now,
            CreatedBy = _actor.UserName,
            UpdatedUtc = now,
            UpdatedBy = _actor.UserName,
        };

        _snippets.Insert(connection, transaction, snippet);
        _changes.Record(connection, transaction, "Snippet", snippet.Id.ToString("D"), "Create",
            new { snippet.Name, snippet.Category }, 1);
        transaction.Commit();

        return snippet;
    }

    public TextSnippet Update(Guid id, SnippetInput input, bool archived)
    {
        _actor.Require(Permission.ManageSnippets);
        ArgumentNullException.ThrowIfNull(input);

        var name = RequireName(input.Name);

        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        var snippet = _snippets.GetById(connection, transaction, id)
                      ?? throw new ValidationException("Der Textbaustein wurde nicht gefunden.");

        if (_snippets.NameExists(connection, transaction, name, id))
        {
            throw new ValidationException($"Es gibt bereits einen Textbaustein mit dem Namen \"{name}\".");
        }

        snippet.Name = name;
        snippet.Category = string.IsNullOrWhiteSpace(input.Category) ? null : input.Category.Trim();
        snippet.Html = input.Html ?? string.Empty;
        snippet.IsArchived = archived;
        snippet.UpdatedBy = _actor.UserName;

        var rowVersion = _snippets.Update(connection, transaction, snippet);
        _changes.Record(connection, transaction, "Snippet", id.ToString("D"), "Update",
            new { snippet.Name, snippet.Category, snippet.IsArchived }, rowVersion);
        transaction.Commit();

        snippet.RowVersion = rowVersion;
        snippet.Version += 1;
        return snippet;
    }

    /// <summary>
    /// Entfernt den Textbaustein endgültig. Bereits erzeugte PDFs behalten ihren Inhalt,
    /// weil dort der fertige Text steht.
    /// </summary>
    public void Delete(Guid id)
    {
        _actor.Require(Permission.ManageSnippets);

        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        var snippet = _snippets.GetById(connection, transaction, id)
                      ?? throw new ValidationException("Der Textbaustein wurde nicht gefunden.");

        _snippets.Delete(connection, transaction, id);
        _changes.Record(connection, transaction, "Snippet", id.ToString("D"), "Delete",
            new { snippet.Name, snippet.Category }, snippet.RowVersion + 1);
        transaction.Commit();
    }

    private static string RequireName(string? name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            throw new ValidationException("Bitte einen Namen für den Textbaustein angeben.");
        }

        return trimmed;
    }
}
