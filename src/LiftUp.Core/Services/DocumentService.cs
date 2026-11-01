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
    private readonly ProjectTableRepository _tables = new();
    private readonly ProjectAnalysisRepository _analysis = new();
    private readonly ProjectAnalysisPhotoRepository _photos = new();
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

    /// <summary>
    /// Prüft die Bild-Platzhalter der Vorlage vor dem Erzeugen: Namen, für die es kein
    /// aktives Bild oder keine Datei mehr gibt. Eine leere Liste bedeutet „alles da“.
    /// </summary>
    public IReadOnlyList<string> FindMissingImages(Guid projectId, Guid templateId) =>
        FindMissingPlaceholders(projectId, templateId).Images;

    /// <summary>
    /// Prüft vor dem Erzeugen alle Angaben aus der Vorlage: fehlende Bilder (kein aktives
    /// Bild oder Datei weg) und fehlende Textbausteine (Name existiert nicht oder ist
    /// archiviert). Beides wird gemeinsam zurückgegeben.
    /// </summary>
    public MissingPlaceholders FindMissingPlaceholders(Guid projectId, Guid templateId)
    {
        _actor.Require(Permission.CreateDocuments);

        using var connection = _database.Open();
        var project = _projects.GetById(connection, null, projectId)
                      ?? throw new ValidationException("Das Projekt wurde nicht gefunden.");
        var version = _templates.GetLatestVersion(connection, null, templateId)
                      ?? throw new ValidationException("Für diese Vorlage gibt es noch keine Version.");

        return FindMissingPlaceholders(connection, null, project.Id, version.Html);
    }

    private MissingPlaceholders FindMissingPlaceholders(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction? transaction,
        Guid projectId,
        string html)
    {
        var imageNames = PlaceholderRenderer.FindImageNames(html);
        var images = imageNames.Count == 0
            ? []
            : BuildImageLookup(connection, transaction, projectId) is var lookup
                ? imageNames
                    .Where(name => !lookup.TryGetValue(name, out var image) || !File.Exists(image.FilePath))
                    .ToArray()
                : [];

        var snippetNames = PlaceholderRenderer.FindSnippetNames(html);
        var snippets = snippetNames.Count == 0
            ? []
            : _snippets.GetActiveByName(connection, transaction) is var known
                ? snippetNames.Where(name => !known.ContainsKey(name)).ToArray()
                : [];

        return images.Length == 0 && snippets.Length == 0
            ? MissingPlaceholders.Empty
            : new MissingPlaceholders(images, snippets);
    }

    /// <summary>Bricht ab, bevor eine Datei geschrieben wird, wenn Angaben fehlen.</summary>
    private static void EnsurePlaceholdersPresent(MissingPlaceholders missing)
    {
        if (!missing.Any)
        {
            return;
        }

        var parts = new List<string>();
        if (missing.Images.Count > 0)
        {
            parts.Add("Bild" + (missing.Images.Count == 1 ? " " : "er ")
                + string.Join(", ", missing.Images.Select(name => $"\"{name}\"")));
        }

        if (missing.Snippets.Count > 0)
        {
            parts.Add("Textbaustein" + (missing.Snippets.Count == 1 ? " " : "e ")
                + string.Join(", ", missing.Snippets.Select(name => $"\"{name}\"")));
        }

        throw new ValidationException(
            $"In der Vorlage fehlen: {string.Join("; ", parts)}. " +
            "Bitte in der Bilder- bzw. Textbausteinansicht ergänzen oder die Platzhalter aus der Vorlage entfernen.");
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

        EnsurePlaceholdersPresent(FindMissingPlaceholders(connection, transaction, project.Id, version.Html));

        // Die Tabellenblätter werden getrennt im Querformat gedruckt; die komplette Analyse folgt
        // nach ihnen und vor den Fotos.
        var html = RenderTitlePage(connection, transaction, project, version, includeSheets: false);
        var report = BuildReport(
            connection, transaction, project.Id,
            includeAnalysis: !HasAnalysisPlaceholder(version.Html));
        var bytes = await ComposePdfAsync(
                connection, transaction, project.Id, html, report, settings, cancellationToken)
            .ConfigureAwait(false);

        return await SavePdfAsync(connection, transaction, project, template, version, bytes, null, cancellationToken)
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

        EnsurePlaceholdersPresent(FindMissingPlaceholders(connection, transaction, project.Id, version.Html));

        var titlePage = RenderTitlePage(connection, transaction, project, version, includeSheets: false);
        var html = DocumentComposition.Build(titlePage, sections);
        var bytes = await ComposePdfAsync(
                connection, transaction, project.Id, html, string.Empty, settings, cancellationToken)
            .ConfigureAwait(false);

        return await SavePdfAsync(
                connection,
                transaction,
                project,
                template,
                version,
                bytes,
                DocumentSections.Serialize(sections),
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
        TemplateVersion version,
        bool includeSheets = true)
    {
        var snippets = _snippets.GetActiveByName(connection, transaction);
        var images = BuildImageLookup(connection, transaction, project.Id);
        // Die Absenderdaten stehen direkt am angemeldeten Benutzer.
        var company = new CompanyProfile
        {
            Name = _actor.Company,
            Street = _actor.CompanyStreet,
            Zip = _actor.CompanyZip,
            City = _actor.CompanyCity,
            LogoPath = _actor.CompanyLogo,
            Contact = _actor.DisplayName is { Length: > 0 } display ? display : _actor.UserName,
            Email = _actor.Email,
            Phone = _actor.Phone,
        };

        var html = PlaceholderRenderer.Render(version.Html, new PlaceholderContext
        {
            Project = project,
            UserName = company.Contact,
            Company = company,
            SnippetResolver = name => snippets.TryGetValue(name, out var snippet) ? snippet : null,
            ImageResolver = name => images.TryGetValue(name, out var image) ? ImageEmbedding.ToImageTag(image) : null,
            Analysis = _analysis.Get(connection, transaction, project.Id),
        });

        // In der Vorschau hängen Zustandsanalyse und Investitionsplanung als eigene Blätter an;
        // im PDF werden sie getrennt im Querformat gedruckt und danach zusammengeführt.
        return includeSheets
            ? AppendProjectSheets(
                connection, transaction, project.Id, html,
                BuildReport(
                    connection, transaction, project.Id,
                    includeAnalysis: !HasAnalysisPlaceholder(version.Html)))
            : html;
    }

    /// <summary>
    /// Hängt die projektbezogenen Tabellen als eigene Seiten an. Ohne Zeilen bleibt das
    /// Dokument unverändert.
    /// </summary>
    private string AppendProjectSheets(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction? transaction,
        Guid projectId,
        string html,
        string report)
    {
        var builder = new System.Text.StringBuilder(html);
        foreach (var (definition, rows) in CollectSheets(connection, transaction, projectId))
        {
            builder.Append(ProjectTableSheet.Render(definition, rows));
        }

        // „1. Anlagedaten“, „2. Analyse“ und „3. Fazit / Empfehlung“ folgen nach den Blättern.
        if (report.Length > 0)
        {
            builder.Append("<div style=\"page-break-before: always;\">")
                .Append(report)
                .Append("</div>");
        }

        // Die Fotos der Analyse folgen als eigene Seiten nach den Tabellen.
        builder.Append(ProjectPhotoSheet.Render(
            _photos.Get(connection, transaction, projectId),
            lifts: CollectLifts(connection, transaction, projectId)));

        return builder.ToString();
    }

    /// <summary>
    /// Aufzüge aus der Zustandsanalyse für die Fotoseiten („4.1 8609861 Messligfeldstrasse 1“).
    /// Nur sie bekommen eine Überschrift – Fotos zu einem Aufzug, den es nicht mehr gibt,
    /// bleiben im PDF weg.
    /// </summary>
    private List<ProjectAnalysisLift> CollectLifts(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction? transaction,
        Guid projectId)
    {
        var lifts = new List<ProjectAnalysisLift>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in _tables.GetRows(connection, transaction, projectId, ProjectTableCatalog.ZustandsanalyseKey))
        {
            var name = row.Values.TryGetValue("AssetName", out var assetName) ? assetName.Trim() : string.Empty;
            var location = row.Values.TryGetValue("Location", out var place) ? place.Trim() : string.Empty;

            // Wie im Reiter „4. Fotos“: ohne Anlagebezeichnung und Standort kein Reiter.
            if (name.Length == 0 || location.Length == 0 || !seen.Add(name))
            {
                continue;
            }

            lifts.Add(new ProjectAnalysisLift(name, location));
        }

        return lifts;
    }

    /// <summary>
    /// Die Berichtsseite mit „1. Anlagedaten“, „2. Analyse“ und „3. Fazit / Empfehlung“ – ohne
    /// Seitenumbruch, damit sie als eigenes Blatt gedruckt werden kann. Leer, wenn nichts erfasst ist.
    /// </summary>
    private string BuildReport(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction? transaction,
        Guid projectId,
        bool includeAnalysis)
    {
        var details = ProjectTableSheet.RenderDetails(
            ProjectTableCatalog.Anlagedaten,
            _tables.GetRows(connection, transaction, projectId, ProjectTableCatalog.AnlagedatenKey));
        // Steht die Analyse schon als Platzhalter in der Vorlage, bleibt sie dort – sonst käme
        // sie zweimal ins Dokument.
        var analysis = includeAnalysis
            ? ProjectAnalysisCatalog.Render(_analysis.Get(connection, transaction, projectId))
            : string.Empty;

        if (details.Length == 0 && analysis.Length == 0)
        {
            return string.Empty;
        }

        var body = new System.Text.StringBuilder();
        if (details.Length > 0)
        {
            body.Append("<h2 style=\"font-size:15pt;margin:0 0 4mm 0;\">")
                .Append(ProjectAnalysisCatalog.Anlagedaten.Heading)
                .Append("</h2>")
                .Append(details);
        }

        // „2. Analyse“ und „3. Fazit / Empfehlung“ kommen mit ihren Überschriften.
        body.Append(analysis);

        return body.ToString();
    }

    /// <summary>Die Berichtsseite als eigenes Dokument im Format des Berichts (hochkant).</summary>
    private static string ReportDocument(string body, PdfPageSettings settings)
    {
        var margin = (settings.MarginMm is { } value ? Math.Max(value, 0) : 15)
            .ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        var format = string.Equals(settings.Format, "A3", StringComparison.OrdinalIgnoreCase) ? "A3" : "A4";

        return "<!DOCTYPE html>\n<html lang=\"de\">\n<head>\n  <meta charset=\"utf-8\" />\n  <style>\n"
            + $"    @page {{ size: {format}; margin: {margin}mm; }}\n"
            + "    body { font-family: 'Segoe UI', Arial, sans-serif; font-size: 10pt; color: #1f2933; margin: 0; }\n"
            + "  </style>\n</head>\n<body>"
            + body
            + "</body>\n</html>";
    }

    /// <summary>Ob die Vorlage die Analyse selbst einsetzt („{{analyse}}“ oder „{{analyse:2.1}}“).</summary>
    private static bool HasAnalysisPlaceholder(string? templateHtml) =>
        templateHtml is not null && templateHtml.Contains("{{analyse", StringComparison.OrdinalIgnoreCase);

    /// <summary>Tabellen mit Zeilen, in der Reihenfolge der Blätter im PDF.</summary>
    private List<(ProjectTableDefinition Definition, IReadOnlyList<ProjectTableRow> Rows)> CollectSheets(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction? transaction,
        Guid projectId)
    {
        var sheets = new List<(ProjectTableDefinition, IReadOnlyList<ProjectTableRow>)>();
        foreach (var definition in ProjectTableCatalog.All)
        {
            var rows = _tables.GetRows(connection, transaction, projectId, definition.Key);
            if (rows.Count > 0)
            {
                sheets.Add((definition, rows));
            }
        }

        return sheets;
    }

    /// <summary>
    /// Druckt den Bericht, die Tabellenblätter und die komplette Analyse. Die Blätter bekommen
    /// eine liegende Seite und werden mit der Analyse vor die Fotos an das PDF gehängt.
    /// </summary>
    private async Task<byte[]> ComposePdfAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction? transaction,
        Guid projectId,
        string html,
        string report,
        PdfPageSettings settings,
        CancellationToken cancellationToken)
    {
        var sheets = CollectSheets(connection, transaction, projectId);
        var photos = _photos.Get(connection, transaction, projectId);
        var main = await _renderer.RenderAsync(html, settings, cancellationToken).ConfigureAwait(false);

        if (sheets.Count == 0 && photos.Count == 0 && report.Length == 0)
        {
            return main;
        }

        var landscape = settings with { Landscape = true };
        var parts = new List<byte[]> { main };
        foreach (var (definition, rows) in sheets)
        {
            var sheetHtml = ProjectTableSheet.RenderDocument(definition, rows, landscape);
            if (sheetHtml.Length == 0)
            {
                continue;
            }

            parts.Add(await _renderer.RenderAsync(sheetHtml, landscape, cancellationToken).ConfigureAwait(false));
        }

        // Nach den Tabellenblättern folgt die komplette Analyse als eigene Seite.
        if (report.Length > 0)
        {
            parts.Add(await _renderer
                .RenderAsync(ReportDocument(report, settings), settings, cancellationToken)
                .ConfigureAwait(false));
        }

        // Die Fotoseiten im Format des Berichts; sie beginnen auf einer neuen Seite.
        var photoHtml = ProjectPhotoSheet.RenderDocument(
            photos, settings, CollectLifts(connection, transaction, projectId));
        if (photoHtml.Length > 0)
        {
            parts.Add(await _renderer.RenderAsync(photoHtml, settings, cancellationToken).ConfigureAwait(false));
        }

        var merged = PdfPageMerger.TryMerge(parts);
        if (merged is not null)
        {
            return merged;
        }

        // Lassen sich die Teile nicht zusammenführen, bleibt alles in einem Dokument – in derselben
        // Reihenfolge: Bericht, Tabellenblätter, komplette Analyse, Fotos.
        var combined = AppendProjectSheets(connection, transaction, projectId, html, report);
        return await _renderer.RenderAsync(combined, settings, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ProjectDocument> SavePdfAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction transaction,
        Project project,
        DocumentTemplate template,
        TemplateVersion version,
        byte[] bytes,
        string? sectionsJson,
        CancellationToken cancellationToken)
    {
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
