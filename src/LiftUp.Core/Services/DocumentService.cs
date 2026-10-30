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
    private readonly ChangeLogWriter _changes;

    public DocumentService(Database database, Session actor, IHtmlToPdfRenderer renderer)
    {
        _database = database;
        _actor = actor;
        _renderer = renderer;
        _changes = new ChangeLogWriter(database);
    }

    /// <summary>HTML für die Vorschau, ohne etwas zu schreiben.</summary>
    public string RenderHtml(Guid projectId, Guid templateId)
    {
        _actor.Require(Permission.CreateDocuments);

        using var connection = _database.Open();
        var project = _projects.GetById(connection, null, projectId)
                      ?? throw new ValidationException("Das Projekt wurde nicht gefunden.");
        var version = _templates.GetLatestVersion(connection, null, templateId)
                      ?? throw new ValidationException("Für diese Vorlage gibt es noch keine Version.");
        var snippets = _snippets.GetActiveByName(connection, null);

        return PlaceholderRenderer.Render(version.Html, new PlaceholderContext
        {
            Project = project,
            UserName = _actor.DisplayName is { Length: > 0 } display ? display : _actor.UserName,
            SnippetResolver = name => snippets.TryGetValue(name, out var html) ? html : null,
        });
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
        var snippets = _snippets.GetActiveByName(connection, transaction);

        var html = PlaceholderRenderer.Render(version.Html, new PlaceholderContext
        {
            Project = project,
            UserName = _actor.DisplayName is { Length: > 0 } display ? display : _actor.UserName,
            SnippetResolver = name => snippets.TryGetValue(name, out var snippet) ? snippet : null,
        });

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
        };

        _documents.Insert(connection, transaction, document);
        _changes.Record(connection, transaction, "Document", document.Id.ToString("D"), "Create",
            new { document.FileName, document.TemplateName, Version = document.TemplateVersion }, 1);
        transaction.Commit();

        return document;
    }

    public IReadOnlyList<ProjectDocument> GetDocuments(Guid projectId)
    {
        _actor.Require(Permission.ViewProjects);
        using var connection = _database.Open();
        return _documents.GetByProject(connection, null, projectId);
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
