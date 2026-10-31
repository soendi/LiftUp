using LiftUp.Core.Data;
using LiftUp.Core.Domain;
using LiftUp.Core.Repositories;

namespace LiftUp.Core.Services;

/// <summary>
/// Verwaltet HTML-Vorlagen. Jedes Speichern erzeugt eine unveränderliche Version;
/// bereits erzeugte PDFs verweisen auf genau diese Version.
/// </summary>
public sealed class TemplateService
{
    private readonly Database _database;
    private readonly Session _actor;
    private readonly TemplateRepository _templates = new();
    private readonly TemplateSectionRepository _sections = new();
    private readonly DocumentTypeRepository _types = new();
    private readonly ChangeLogWriter _changes;

    public TemplateService(Database database, Session actor)
    {
        _database = database;
        _actor = actor;
        _changes = new ChangeLogWriter(database, _actor.UserName);
    }

    public IReadOnlyList<TemplateListItem> GetAll(bool includeInactive = true, string? documentType = null)
    {
        RequireRead();
        using var connection = _database.Open();
        var items = _templates.GetAll(connection, null, includeInactive);

        return string.IsNullOrWhiteSpace(documentType)
            ? items
            : items.Where(item => string.Equals(item.DocumentType, documentType.Trim(), StringComparison.OrdinalIgnoreCase))
                .ToArray();
    }

    public DocumentTemplate Get(Guid id)
    {
        RequireRead();
        using var connection = _database.Open();
        return _templates.GetById(connection, null, id)
               ?? throw new ValidationException("Die Vorlage wurde nicht gefunden.");
    }

    public TemplateVersion GetLatestVersion(Guid templateId)
    {
        RequireRead();
        using var connection = _database.Open();
        return _templates.GetLatestVersion(connection, null, templateId)
               ?? throw new ValidationException("Für diese Vorlage gibt es noch keine Version.");
    }

    public TemplateVersion GetVersion(Guid templateId, int version)
    {
        RequireRead();
        using var connection = _database.Open();
        return _templates.GetVersion(connection, null, templateId, version)
               ?? throw new ValidationException($"Version {version} der Vorlage wurde nicht gefunden.");
    }

    public IReadOnlyList<TemplateVersion> GetVersions(Guid templateId)
    {
        RequireRead();
        using var connection = _database.Open();
        return _templates.GetVersions(connection, null, templateId);
    }

    public DocumentTemplate Create(TemplateInput input)
    {
        _actor.Require(Permission.ManageTemplates);
        ArgumentNullException.ThrowIfNull(input);

        var name = RequireName(input.Name);
        var html = input.Html ?? string.Empty;
        var now = DateTime.UtcNow;

        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        if (_templates.NameExists(connection, transaction, name, null))
        {
            throw new ValidationException($"Es gibt bereits eine Vorlage mit dem Namen \"{name}\".");
        }

        var documentType = RequireKnownType(connection, transaction, input.DocumentType);

        var template = new DocumentTemplate
        {
            Name = name,
            DocumentType = documentType,
            CurrentVersion = 1,
            IsActive = input.IsActive,
            CreatedUtc = now,
            CreatedBy = _actor.UserName,
            UpdatedUtc = now,
            UpdatedBy = _actor.UserName,
        };

        _templates.Insert(connection, transaction, template);
        _templates.InsertVersion(connection, transaction, new TemplateVersion
        {
            Id = Guid.NewGuid(),
            TemplateId = template.Id,
            Version = 1,
            Html = html,
            CreatedUtc = now,
            CreatedBy = _actor.UserName,
        });

        _changes.Record(connection, transaction, "Template", template.Id.ToString("D"), "Create",
            new { template.Name, template.DocumentType, Version = 1 }, 1);
        transaction.Commit();

        return template;
    }

    /// <summary>Speichert Änderungen als neue Version; die alte bleibt unverändert erhalten.</summary>
    public DocumentTemplate SaveNewVersion(Guid templateId, TemplateInput input)
    {
        _actor.Require(Permission.ManageTemplates);
        ArgumentNullException.ThrowIfNull(input);

        var name = RequireName(input.Name);
        var now = DateTime.UtcNow;

        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        var template = _templates.GetById(connection, transaction, templateId)
                       ?? throw new ValidationException("Die Vorlage wurde nicht gefunden.");

        if (_templates.NameExists(connection, transaction, name, templateId))
        {
            throw new ValidationException($"Es gibt bereits eine Vorlage mit dem Namen \"{name}\".");
        }

        var nextVersion = Math.Max(template.CurrentVersion, 0) + 1;
        var documentType = RequireKnownType(connection, transaction, input.DocumentType);

        template.Name = name;
        template.DocumentType = documentType;
        template.CurrentVersion = nextVersion;
        template.IsActive = input.IsActive;
        template.UpdatedUtc = now;
        template.UpdatedBy = _actor.UserName;

        var rowVersion = _templates.UpdateMeta(connection, transaction, template);
        _templates.InsertVersion(connection, transaction, new TemplateVersion
        {
            Id = Guid.NewGuid(),
            TemplateId = template.Id,
            Version = nextVersion,
            Html = input.Html ?? string.Empty,
            CreatedUtc = now,
            CreatedBy = _actor.UserName,
        });

        _changes.Record(connection, transaction, "Template", template.Id.ToString("D"), "Update",
            new { template.Name, template.DocumentType, Version = nextVersion }, rowVersion);
        transaction.Commit();

        template.RowVersion = rowVersion;
        return template;
    }

    /// <summary>Abschnitte (Untertitel) der Vorlage in der gespeicherten Reihenfolge.</summary>
    public IReadOnlyList<TemplateSection> GetSections(Guid templateId)
    {
        RequireRead();
        using var connection = _database.Open();
        return _sections.GetByTemplate(connection, null, templateId);
    }

    /// <summary>Ersetzt die Abschnitte der Vorlage; leere Titel werden verworfen.</summary>
    public void SaveSections(Guid templateId, IReadOnlyList<string> titles)
    {
        _actor.Require(Permission.ManageTemplates);
        ArgumentNullException.ThrowIfNull(titles);

        var cleaned = titles
            .Select(title => (title ?? string.Empty).Trim())
            .Where(title => title.Length > 0)
            .ToList();

        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        var template = _templates.GetById(connection, transaction, templateId)
                       ?? throw new ValidationException("Die Vorlage wurde nicht gefunden.");

        _sections.ReplaceAll(connection, transaction, templateId, cleaned, _actor.UserName);
        _changes.Record(connection, transaction, "Template", templateId.ToString("D"), "Sections",
            new { Count = cleaned.Count }, template.RowVersion);
        transaction.Commit();
    }

    /// <summary>
    /// Entfernt die Vorlage aus der Auswahl. Bereits erzeugte PDFs bleiben unverändert,
    /// weil sie auf ihre Versionsnummer verweisen.
    /// </summary>
    public void Delete(Guid templateId)
    {
        _actor.Require(Permission.ManageTemplates);

        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        var template = _templates.GetById(connection, transaction, templateId)
                       ?? throw new ValidationException("Die Vorlage wurde nicht gefunden.");

        var rowVersion = _templates.SoftDelete(connection, transaction, templateId);
        _changes.Record(connection, transaction, "Template", templateId.ToString("D"), "Delete",
            new { template.Name, template.DocumentType, template.CurrentVersion }, rowVersion);
        transaction.Commit();
    }

    public void SetActive(Guid templateId, bool active)
    {
        _actor.Require(Permission.ManageTemplates);

        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        var template = _templates.GetById(connection, transaction, templateId)
                       ?? throw new ValidationException("Die Vorlage wurde nicht gefunden.");

        if (template.IsActive == active)
        {
            transaction.Commit();
            return;
        }

        template.IsActive = active;
        template.UpdatedUtc = DateTime.UtcNow;
        template.UpdatedBy = _actor.UserName;

        var rowVersion = _templates.UpdateMeta(connection, transaction, template);
        _changes.Record(connection, transaction, "Template", templateId.ToString("D"),
            active ? "Activate" : "Deactivate", new { template.Name }, rowVersion);
        transaction.Commit();
    }

    private void RequireRead()
    {
        if (!_actor.Can(Permission.ManageTemplates) && !_actor.Can(Permission.CreateDocuments))
        {
            throw new PermissionDeniedException(Permission.ManageTemplates);
        }
    }

    /// <summary>Stellt sicher, dass der Typ in der zentralen Liste steht.</summary>
    private string RequireKnownType(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction? transaction,
        string? documentType)
    {
        var name = string.IsNullOrWhiteSpace(documentType) ? "Dokument" : documentType.Trim();

        if (_types.GetByName(connection, transaction, name) is null)
        {
            throw new ValidationException(
                $"Der Dokumenttyp \"{name}\" ist nicht bekannt. Bitte unter Vorlagen → Typen verwalten anlegen.");
        }

        return name;
    }

    private static string RequireName(string? name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            throw new ValidationException("Bitte einen Namen für die Vorlage angeben.");
        }

        return trimmed;
    }
}
