using LiftUp.Core.Data;
using LiftUp.Core.Documents;
using LiftUp.Core.Domain;
using LiftUp.Core.Repositories;

namespace LiftUp.Core.Services;

/// <summary>
/// Erzeugt PDFs aus Vorlagen, legt sie im Projektordner unter <c>03_PDF</c> ab und
/// protokolliert, welche Vorlagenversion verwendet wurde. Alte PDFs bleiben unberührt,
/// wenn die Vorlage später geändert wird.
/// </summary>
public sealed class DocumentService
{
    private readonly Database _database;
    private readonly Session _actor;
    private readonly IHtmlToPdfRenderer _renderer;
    private readonly ProjectRepository _projects = new();
    private readonly TemplateRepository _templates = new();
    private readonly DocumentRepository _documents = new();
    private readonly SnippetRepository _snippets = new();
    private readonly ImageRepository _images = new();
    private readonly ChangeLogWriter _changes;

    public DocumentService(Database database, Session actor, IHtmlToPdfRenderer renderer)
    {
        _database = database;
        _actor = actor;
        _renderer = renderer;
        _changes = new ChangeLogWriter(database, _actor.UserName);
    }

    /// <summary>HTML für die Vorschau, ohne etwas zu schreiben.</summary>
    public string RenderHtml(Guid projectId, Guid templateId) => RenderHtml(projectId, templateId, null);

    /// <summary>HTML für die Vorschau, optional mit ausgefüllten Abschnitten.</summary>
    public string RenderHtml(Guid projectId, Guid templateId, IReadOnlyList<DocumentSectionContent>? sections)
    {
        _actor.Require(Permission.CreateDocuments);

        using var connection = _database.Open();
        var project = _projects.GetById(connection, null, projectId)
                      ?? throw new ValidationException("Das Projekt wurde nicht gefunden.");
        var version = _templates.GetLatestVersion(connection, null, templateId)
                      ?? throw new ValidationException("Für diese Vorlage gibt es noch keine Version.");

        var html = RenderTitlePage(connection, null, project, version);
        return sections is { Count: > 0 } ? DocumentComposition.Build(html, sections) : html;
    }

    public async Task<ProjectDocument> CreatePdfAsync(
        Guid projectId,
        Guid templateId,
        PdfPageSettings? settings = null,
        CancellationToken cancellationToken = default)
    {
        _actor.Require(Permission.CreateDocuments);
        settings ??= new PdfPageSettings();

        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        var project = _projects.GetById(connection, transaction, projectId)
                      ?? throw new ValidationException("Das Projekt wurde nicht gefunden.");
        var template = _templates.GetById(connection, transaction, templateId)
                       ?? throw new ValidationException("Die Vorlage wurde nicht gefunden.");
        var version = _templates.GetLatestVersion(connection, transaction, templateId)
                      ?? throw new ValidationException("Für diese Vorlage gibt es noch keine Version.");

        var html = RenderTitlePage(connection, transaction, project, version);
        return await SavePdfAsync(connection, transaction, project, template, version, html, null, settings, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Erzeugt ein Dokument aus der Titelseite der Vorlage und den ausgefüllten
    /// Abschnitten (Untertitel samt Text und Bildern) und legt es als PDF ab.
    /// </summary>
    public async Task<ProjectDocument> CreatePdfFromSectionsAsync(
        Guid projectId,
        Guid templateId,
        IReadOnlyList<DocumentSectionContent> sections,
        PdfPageSettings? settings = null,
        CancellationToken cancellationToken = default)
    {
        _actor.Require(Permission.CreateDocuments);
        ArgumentNullException.ThrowIfNull(sections);
        settings ??= new PdfPageSettings();

        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        var project = _projects.GetById(connection, transaction, projectId)
                      ?? throw new ValidationException("Das Projekt wurde nicht gefunden.");
        var template = _templates.GetById(connection, transaction, templateId)
                       ?? throw new ValidationException("Die Vorlage wurde nicht gefunden.");
        var version = _templates.GetLatestVersion(connection, transaction, templateId)
                      ?? throw new ValidationException("Für diese Vorlage gibt es noch keine Version.");

        var titlePage = RenderTitlePage(connection, transaction, project, version);
        var html = DocumentComposition.Build(titlePage, sections);

        return await SavePdfAsync(
                connection,
                transaction,
                project,
                template,
                version,
                html,
                DocumentSections.Serialize(sections),
                settings,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Entfernt ein erzeugtes Dokument. Der Eintrag wird weich gelöscht (der Abgleich kann
    /// die Löschung nachvollziehen), die PDF-Datei landet unter Windows im Papierkorb.
    /// </summary>
    public string? DeleteDocument(Guid documentId)
    {
        _actor.Require(Permission.DeleteDocuments);

        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        var document = _documents.GetById(connection, transaction, documentId)
                       ?? throw new ValidationException("Das Dokument wurde nicht gefunden.");

        var rowVersion = _documents.SoftDelete(connection, transaction, documentId);
        _changes.Record(connection, transaction, "Document", documentId.ToString("D"), "Delete",
            new { document.FileName, document.TemplateName, document.ProjectId }, rowVersion);
        transaction.Commit();

        return FolderService.TryDeleteFile(document.FilePath, out var error) ? null : error;
    }

    public IReadOnlyList<ProjectDocument> GetDocuments(Guid projectId)
    {
        _actor.Require(Permission.ViewProjects);
        using var connection = _database.Open();
        return _documents.GetByProject(connection, null, projectId);
    }

    /// <summary>Titelseite der Vorlage mit Projekt- und Absenderdaten füllen.</summary>
    private string RenderTitlePage(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction? transaction,
        Project project,
        TemplateVersion version)
    {
        var snippets = _snippets.GetActiveByName(connection, transaction);
        var images = BuildImageLookup(connection, transaction, project.Id);
        // Die Absenderdaten stehen direkt am angemeldeten Benutzer.
        var company = new CompanyProfile
        {
            Name = _actor.Company,
            Contact = _actor.DisplayName is { Length: > 0 } display ? display : _actor.UserName,
            Email = _actor.Email,
            Phone = _actor.Phone,
        };

        return PlaceholderRenderer.Render(version.Html, new PlaceholderContext
        {
            Project = project,
            UserName = company.Contact,
            Company = company,
            SnippetResolver = name => snippets.TryGetValue(name, out var snippet) ? snippet : null,
            ImageResolver = name => images.TryGetValue(name, out var image) ? ImageEmbedding.ToImageTag(image) : null,
        });
    }

    private async Task<ProjectDocument> SavePdfAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction transaction,
        Project project,
        DocumentTemplate template,
        TemplateVersion version,
        string html,
        string? sectionsJson,
        PdfPageSettings settings,
        CancellationToken cancellationToken)
    {
        var bytes = await _renderer.RenderAsync(html, settings, cancellationToken).ConfigureAwait(false);

        var folder = Path.Combine(project.FolderPath, "03_PDF");
        Directory.CreateDirectory(folder);

        var fileName = BuildFileName(project.Number, template.Name, DateTime.Now);
        var filePath = Path.Combine(folder, fileName);
        await File.WriteAllBytesAsync(filePath, bytes, cancellationToken).ConfigureAwait(false);

        var document = new ProjectDocument
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            TemplateId = template.Id,
            TemplateName = template.Name,
            TemplateVersion = version.Version,
            FileName = fileName,
            FilePath = filePath,
            SizeBytes = bytes.LongLength,
            CreatedUtc = DateTime.UtcNow,
            CreatedBy = _actor.UserName,
            SectionsJson = sectionsJson,
        };

        _documents.Insert(connection, transaction, document);
        _changes.Record(connection, transaction, "Document", document.Id.ToString("D"), "Create",
            new { document.FileName, document.TemplateName, Version = document.TemplateVersion }, 1);
        transaction.Commit();

        return document;
    }

    /// <summary>
    /// Bilder nach Namen auflösen; projektbezogene Bilder überschreiben gleichnamige
    /// globale Bilder, weil die Abfrage globale zuerst liefert.
    /// </summary>
    private Dictionary<string, LibraryImage> BuildImageLookup(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction? transaction,
        Guid projectId)
    {
        var lookup = new Dictionary<string, LibraryImage>(StringComparer.OrdinalIgnoreCase);
        foreach (var image in _images.GetActive(connection, transaction, projectId))
        {
            lookup[image.Name] = image;
        }

        return lookup;
    }

    private static string BuildFileName(string? number, string templateName, DateTime now)
    {
        var prefix = string.IsNullOrWhiteSpace(number) ? "Projekt" : number!;
        var name = $"{prefix}-{templateName}-{now:yyyyMMdd-HHmmss}.pdf";

        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '-');
        }

        return name;
    }
}
