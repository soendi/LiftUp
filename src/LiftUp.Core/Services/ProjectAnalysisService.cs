using LiftUp.Core.Data;
using LiftUp.Core.Documents;
using LiftUp.Core.Domain;
using LiftUp.Core.Repositories;

namespace LiftUp.Core.Services;

/// <summary>
/// Pflegt die Texte der Analyse je Projekt. Die Texte hängen am Projekt, nicht an einem
/// einzelnen Dokument: Sie werden einmal erfasst und fließen in jedes PDF desselben
/// Projekts (Platzhalter <c>{{analyse}}</c> bzw. Startinhalt des Reiters „2. Analyse“).
/// </summary>
public sealed class ProjectAnalysisService
{
    private readonly Database _database;
    private readonly Session _actor;
    private readonly ProjectRepository _projects = new();
    private readonly ProjectTableRepository _tables = new();
    private readonly ProjectAnalysisRepository _texts = new();
    private readonly ProjectAnalysisPhotoRepository _photos = new();
    private readonly ChangeLogWriter _changes;

    public ProjectAnalysisService(Database database, Session actor)
    {
        _database = database;
        _actor = actor;
        _changes = new ChangeLogWriter(database, _actor.UserName);
    }

    public IReadOnlyList<ProjectAnalysisText> Get(Guid projectId)
    {
        _actor.Require(Permission.ViewProjects);

        using var connection = _database.Open();
        return _texts.Get(connection, null, projectId);
    }

    /// <summary>
    /// Ersetzt die Texte des Projekts. Unbekannte Unterpunkte und leere Texte fallen weg;
    /// ein leerer Text löscht den bisherigen Inhalt.
    /// </summary>
    public IReadOnlyList<ProjectAnalysisText> Save(Guid projectId, IReadOnlyList<ProjectAnalysisTextInput> texts)
    {
        _actor.Require(Permission.EditProjects);
        ArgumentNullException.ThrowIfNull(texts);

        var prepared = new List<ProjectAnalysisTextInput>();
        foreach (var text in texts)
        {
            var section = ProjectAnalysisCatalog.FindSection(text.SectionKey)
                          ?? throw new ValidationException(
                              $"Unbekannter Analysepunkt \"{text.SectionKey}\".");

            // Leerzeilen und leere Absätze am Ende gehören nicht in den Bericht.
            var html = ProjectAnalysisCatalog.TrimTrailing(text.Html);
            if (html.Length == 0 || prepared.Any(item => item.SectionKey == section.Key))
            {
                continue;
            }

            prepared.Add(new ProjectAnalysisTextInput(section.Key, html));
        }

        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        var project = _projects.GetById(connection, transaction, projectId)
                      ?? throw new ValidationException("Das Projekt wurde nicht gefunden.");

        var saved = _texts.Replace(connection, transaction, projectId, prepared, _actor.UserName);

        _changes.Record(connection, transaction, "ProjectAnalysis", projectId.ToString("D"), "Update",
            new { project.Name, Abschnitte = saved.Count }, saved.Count == 0 ? 1 : saved[0].RowVersion);
        transaction.Commit();

        return saved;
    }

    /// <summary>
    /// Aufzüge aus der Zustandsanalyse – je Aufzug ein Reiter unter „4. Fotos“. Ein Reiter
    /// entsteht erst, wenn Anlagebezeichnung und Standort erfasst sind; gleiche Bezeichnungen
    /// ergeben einen Reiter.
    /// </summary>
    public IReadOnlyList<ProjectAnalysisLift> GetLifts(Guid projectId)
    {
        _actor.Require(Permission.ViewProjects);

        using var connection = _database.Open();
        var rows = _tables.GetRows(connection, null, projectId, ProjectTableCatalog.ZustandsanalyseKey);

        var lifts = new List<ProjectAnalysisLift>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            var name = row.Values.TryGetValue("AssetName", out var assetName) ? assetName.Trim() : string.Empty;
            var location = row.Values.TryGetValue("Location", out var place) ? place.Trim() : string.Empty;

            // Ohne Anlagebezeichnung gibt es keinen Schlüssel für die Fotos, ohne Standort
            // keine eindeutige Überschrift – solche Zeilen bekommen keinen Reiter.
            if (name.Length == 0 || location.Length == 0 || !seen.Add(name))
            {
                continue;
            }

            lifts.Add(new ProjectAnalysisLift(name, location));
        }

        return lifts;
    }

    /// <summary>Alle Fotos des Projekts, gruppiert nach Aufzug.</summary>
    public IReadOnlyList<ProjectAnalysisPhoto> GetPhotos(Guid projectId)
    {
        _actor.Require(Permission.ViewProjects);

        using var connection = _database.Open();
        return _photos.Get(connection, null, projectId);
    }

    /// <summary>Ersetzt die Fotos eines Aufzugs; Fotos anderer Aufzüge bleiben unberührt.</summary>
    public IReadOnlyList<ProjectAnalysisPhoto> SavePhotos(
        Guid projectId,
        string liftKey,
        IReadOnlyList<ProjectAnalysisPhotoInput> photos)
    {
        _actor.Require(Permission.EditProjects);
        ArgumentNullException.ThrowIfNull(photos);

        var key = (liftKey ?? string.Empty).Trim();
        var prepared = new List<ProjectAnalysisPhotoInput>(photos.Count);

        foreach (var photo in photos)
        {
            var path = (photo.FilePath ?? string.Empty).Trim();
            if (path.Length == 0)
            {
                continue;
            }

            prepared.Add(photo with
            {
                FilePath = path,
                Caption = (photo.Caption ?? string.Empty).Trim(),
                Annotations = photo.Annotations ?? [],
            });
        }

        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        var project = _projects.GetById(connection, transaction, projectId)
                      ?? throw new ValidationException("Das Projekt wurde nicht gefunden.");

        var saved = _photos.Replace(connection, transaction, projectId, key, prepared, _actor.UserName);

        _changes.Record(connection, transaction, "ProjectAnalysisPhoto", projectId.ToString("D"), "Update",
            new { project.Name, Aufzug = key, Fotos = saved.Count }, saved.Count == 0 ? 1 : saved[0].RowVersion);
        transaction.Commit();

        return saved;
    }

    /// <summary>
    /// Übernimmt eine Bilddatei in den Bilderordner des Projekts und liefert den Pfad, der
    /// am Foto gespeichert wird. Liegt die Datei schon dort, bleibt sie unverändert.
    /// </summary>
    public string ImportPhoto(Guid projectId, string sourcePath)
    {
        _actor.Require(Permission.EditProjects);

        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            throw new ValidationException("Bitte eine Bilddatei wählen.");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(sourcePath.Trim());
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ValidationException("Der Pfad zum Bild ist ungültig.");
        }

        if (!File.Exists(fullPath))
        {
            throw new ValidationException("Die gewählte Bilddatei wurde nicht gefunden.");
        }

        if (ImageEmbedding.ContentTypeFor(fullPath) is null)
        {
            throw new ValidationException(
                $"Der Dateityp \"{Path.GetExtension(fullPath)}\" wird für Fotos nicht unterstützt. " +
                "Möglich sind PNG, JPG, GIF, WebP, BMP und SVG.");
        }

        using var connection = _database.Open();
        var project = _projects.GetById(connection, null, projectId)
                      ?? throw new ValidationException("Das Projekt wurde nicht gefunden.");

        var folder = Path.Combine(project.FolderPath, ProjectFolders.Images);
        if (IsInsideFolder(fullPath, folder))
        {
            return fullPath;
        }

        Directory.CreateDirectory(folder);
        var target = UniquePath(folder, Path.GetFileName(fullPath));

        try
        {
            File.Copy(fullPath, target);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new LiftUpException("Das Foto konnte nicht in den Projektordner kopiert werden: " + exception.Message);
        }

        return target;
    }

    private static bool IsInsideFolder(string filePath, string folder)
    {
        var fullFolder = Path.GetFullPath(folder);
        if (!fullFolder.EndsWith(Path.DirectorySeparatorChar))
        {
            fullFolder += Path.DirectorySeparatorChar;
        }

        return filePath.StartsWith(fullFolder, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Freier Dateiname im Bilderordner; ein gleichnamiges Foto bleibt erhalten.</summary>
    private static string UniquePath(string folder, string fileName)
    {
        var target = Path.Combine(folder, fileName);
        if (!File.Exists(target))
        {
            return target;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var index = 2; index < 1000; index++)
        {
            target = Path.Combine(folder, $"{stem} ({index}){extension}");
            if (!File.Exists(target))
            {
                return target;
            }
        }

        return Path.Combine(folder, $"{stem}-{Guid.NewGuid():N}{extension}");
    }
}
