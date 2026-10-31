using System.Security.Cryptography;
using LiftUp.Core.Data;
using LiftUp.Core.Documents;
using LiftUp.Core.Domain;
using LiftUp.Core.Repositories;

namespace LiftUp.Core.Services;

/// <summary>
/// Bildbibliothek: globale Bilder und projektbezogene Bilder. Beim Import wird die
/// Datei in den Bibliotheksordner kopiert; das Original bleibt unangetastet.
/// </summary>
public sealed class ImageService
{
    private readonly Database _database;
    private readonly Session _actor;
    private readonly ImageRepository _images = new();
    private readonly ProjectRepository _projects = new();
    private readonly ChangeLogWriter _changes;
    private readonly string _libraryRoot;

    public ImageService(Database database, Session actor, string? libraryRoot = null)
    {
        _database = database;
        _actor = actor;
        _changes = new ChangeLogWriter(database, _actor.UserName);

        var baseDirectory = Path.GetDirectoryName(database.FilePath) ?? Path.GetTempPath();
        _libraryRoot = libraryRoot ?? Path.Combine(baseDirectory, "bilder");
    }

    public string LibraryRoot => _libraryRoot;

    /// <summary>
    /// Sucht Bilder nach Name, Kategorie, Schlagworten und Beschreibung. Jedes Suchwort
    /// muss passen, damit „logo rot“ auch bei getrennt gespeicherten Schlagworten trifft.
    /// </summary>
    public IReadOnlyList<ImageListItem> Search(
        Guid? projectId,
        string? search,
        bool includeArchived = false,
        bool allProjects = false)
    {
        _actor.Require(Permission.ViewProjects);
        using var connection = _database.Open();
        return _images.Search(connection, null, projectId, search, includeArchived, allProjects);
    }

    /// <summary>Liefert ein Bild aus der Bibliothek.</summary>
    public LibraryImage Get(Guid id)
    {
        _actor.Require(Permission.ViewProjects);

        using var connection = _database.Open();
        return _images.GetById(connection, null, id)
               ?? throw new ValidationException("Das Bild wurde nicht gefunden.");
    }

    public LibraryImage Import(string sourcePath, ImageInput input)
    {
        _actor.Require(Permission.ManageImages);
        ArgumentNullException.ThrowIfNull(input);

        if (!File.Exists(sourcePath))
        {
            throw new ValidationException("Die ausgewählte Datei wurde nicht gefunden.");
        }

        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        var contentType = ImageEmbedding.ContentTypeFor(sourcePath);
        if (contentType is null)
        {
            throw new ValidationException(
                $"Der Dateityp \"{extension}\" wird nicht unterstützt. Möglich sind PNG, JPG, GIF, WebP, BMP und SVG.");
        }

        var name = RequireName(string.IsNullOrWhiteSpace(input.Name)
            ? Path.GetFileNameWithoutExtension(sourcePath)
            : input.Name);

        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        if (_images.NameExists(connection, transaction, name, input.ProjectId, null))
        {
            throw new ValidationException($"Es gibt bereits ein Bild mit dem Namen \"{name}\".");
        }

        var targetFolder = input.ProjectId is null
            ? Path.Combine(_libraryRoot, "global")
            : Path.Combine(_libraryRoot, "projekte", input.ProjectId.Value.ToString("D"));
        Directory.CreateDirectory(targetFolder);

        var fileName = $"{Guid.NewGuid():N}{extension}";
        var targetPath = Path.Combine(targetFolder, fileName);

        try
        {
            File.Copy(sourcePath, targetPath, overwrite: false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ValidationException($"Das Bild konnte nicht kopiert werden: {exception.Message}");
        }

        var image = new LibraryImage
        {
            Name = name,
            Category = Clean(input.Category),
            Description = Clean(input.Description),
            Tags = NormalizeTags(input.Tags),
            ProjectId = input.ProjectId,
            FileName = fileName,
            FilePath = targetPath,
            ContentType = contentType,
            SizeBytes = new FileInfo(targetPath).Length,
            ContentHash = ComputeHash(targetPath),
            CreatedUtc = DateTime.UtcNow,
            CreatedBy = _actor.UserName,
        };

        _images.Insert(connection, transaction, image);
        _changes.Record(connection, transaction, "Image", image.Id.ToString("D"), "Create",
            new { image.Name, image.Category, image.Tags, image.ProjectId }, 1);
        transaction.Commit();

        return image;
    }

    /// <summary>
    /// Ändert Name, Kategorie, Schlagworte und Beschreibung eines bereits importierten
    /// Bildes. Die Datei bleibt, wo sie ist.
    /// </summary>
    public LibraryImage Update(Guid id, ImageInput input)
    {
        _actor.Require(Permission.ManageImages);
        ArgumentNullException.ThrowIfNull(input);

        var name = RequireName(input.Name);

        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        var image = _images.GetById(connection, transaction, id)
                    ?? throw new ValidationException("Das Bild wurde nicht gefunden.");

        if (_images.NameExists(connection, transaction, name, image.ProjectId, id))
        {
            throw new ValidationException($"Es gibt bereits ein Bild mit dem Namen \"{name}\".");
        }

        image.Name = name;
        image.Category = Clean(input.Category);
        image.Description = Clean(input.Description);
        image.Tags = NormalizeTags(input.Tags);

        var rowVersion = _images.Update(connection, transaction, image);
        _changes.Record(connection, transaction, "Image", id.ToString("D"), "Update",
            new { image.Name, image.Category, image.Tags }, rowVersion);
        transaction.Commit();

        image.RowVersion = rowVersion;
        return image;
    }

    public void SetArchived(Guid id, bool archived)
    {
        _actor.Require(Permission.ManageImages);

        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        var image = _images.GetById(connection, transaction, id)
                    ?? throw new ValidationException("Das Bild wurde nicht gefunden.");

        var rowVersion = _images.SetArchived(connection, transaction, id, archived);
        _changes.Record(connection, transaction, "Image", id.ToString("D"),
            archived ? "Archive" : "Restore", new { image.Name }, rowVersion);
        transaction.Commit();
    }

    /// <summary>
    /// Entfernt ein Bild samt Kopie im Bibliotheksordner. Liefert eine Warnung, wenn der
    /// Eintrag gelöscht ist, die Datei aber nicht entfernt werden konnte.
    /// </summary>
    public string? Delete(Guid id)
    {
        _actor.Require(Permission.ManageImages);

        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        var image = _images.GetById(connection, transaction, id)
                    ?? throw new ValidationException("Das Bild wurde nicht gefunden.");

        _images.Delete(connection, transaction, id);
        _changes.Record(connection, transaction, "Image", id.ToString("D"), "Delete",
            new { image.Name, image.Category, image.ProjectId }, image.RowVersion + 1);
        transaction.Commit();

        return FolderService.TryDeleteFile(image.FilePath, out var error) ? null : error;
    }

    /// <summary>
    /// Entfernt alle Einträge, deren Bilddatei nicht mehr vorhanden ist – etwa weil sie
    /// im Projektordner gelöscht oder verschoben wurde. Liefert die Anzahl der entfernten
    /// Einträge. Der Bereich entspricht der Bilderansicht (global und Projekt).
    /// </summary>
    public int RemoveMissing(Guid? projectId, bool allProjects = false)
    {
        _actor.Require(Permission.ManageImages);

        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        var candidates = _images.Search(connection, transaction, projectId, null, includeArchived: true, allProjects);
        var removed = 0;

        foreach (var image in candidates)
        {
            if (File.Exists(image.FilePath))
            {
                continue;
            }

            _images.Delete(connection, transaction, image.Id);
            _changes.Record(connection, transaction, "Image", image.Id.ToString("D"), "Delete",
                new { image.Name, image.Category, Missing = true }, 1);
            removed++;
        }

        if (removed > 0)
        {
            transaction.Commit();
        }

        return removed;
    }

    /// <summary>
    /// Setzt Schlagworte für mehrere Bilder gleichzeitig: <paramref name="addTags"/> kommen
    /// hinzu, <paramref name="removeTags"/> werden entfernt. Beides darf eine kommagetrennte
    /// Liste sein. Liefert die Anzahl der tatsächlich geänderten Bilder.
    /// </summary>
    public int SetTags(IReadOnlyCollection<Guid> ids, string? addTags, string? removeTags)
    {
        _actor.Require(Permission.ManageImages);
        ArgumentNullException.ThrowIfNull(ids);

        var add = ImageTags.Split(addTags);
        var remove = ImageTags.Split(removeTags);

        if (ids.Count == 0 || (add.Count == 0 && remove.Count == 0))
        {
            return 0;
        }

        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        var changed = 0;
        foreach (var id in ids.Distinct())
        {
            var image = _images.GetById(connection, transaction, id)
                        ?? throw new ValidationException("Ein ausgewähltes Bild wurde nicht gefunden.");

            var tags = ImageTags.Split(image.Tags).ToList();
            tags.RemoveAll(tag => remove.Any(value =>
                string.Equals(value, tag, StringComparison.CurrentCultureIgnoreCase)));

            foreach (var tag in add)
            {
                if (!tags.Contains(tag, StringComparer.CurrentCultureIgnoreCase))
                {
                    tags.Add(tag);
                }
            }

            var normalized = NormalizeTags(string.Join(", ", tags));
            if (string.Equals(normalized, image.Tags, StringComparison.Ordinal))
            {
                continue;
            }

            var rowVersion = _images.UpdateTags(connection, transaction, id, normalized);
            _changes.Record(connection, transaction, "Image", id.ToString("D"), "Update",
                new { image.Name, Tags = normalized, Batch = true }, rowVersion);
            changed++;
        }

        if (changed > 0)
        {
            transaction.Commit();
        }

        return changed;
    }

    /// <summary>
    /// Ersetzt die Datei eines fehlenden Bildes durch eine neu ausgewählte Datei. Gibt es
    /// noch keinen Eintrag mit diesem Namen, wird er angelegt; sonst wird der vorhandene
    /// Eintrag auf die Kopie im Bibliotheksordner umgestellt. Der Name bleibt, damit
    /// <c>{{bild:Name}}</c> in der Vorlage weiter passt.
    /// </summary>
    public LibraryImage ReplaceFile(Guid? projectId, string name, string sourcePath)
    {
        _actor.Require(Permission.ManageImages);
        var cleanName = RequireName(name);

        if (!File.Exists(sourcePath))
        {
            throw new ValidationException("Die ausgewählte Datei wurde nicht gefunden.");
        }

        var contentType = ImageEmbedding.ContentTypeFor(sourcePath);
        if (contentType is null)
        {
            throw new ValidationException(
                "Der Dateityp wird nicht unterstützt. Möglich sind PNG, JPG, GIF, WebP, BMP und SVG.");
        }

        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();

        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        var existing = _images.Search(connection, transaction, projectId, null, includeArchived: true)
            .LastOrDefault(image => string.Equals(image.Name, cleanName, StringComparison.OrdinalIgnoreCase));

        var owner = existing is null || existing.IsGlobal ? null : projectId;
        var folder = owner is null
            ? Path.Combine(_libraryRoot, "global")
            : Path.Combine(_libraryRoot, "projekte", owner.Value.ToString("D"));
        Directory.CreateDirectory(folder);

        var fileName = $"{Guid.NewGuid():N}{extension}";
        var targetPath = Path.Combine(folder, fileName);

        try
        {
            File.Copy(sourcePath, targetPath, overwrite: false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ValidationException($"Das Bild konnte nicht übernommen werden: {exception.Message}");
        }

        var hash = ComputeHash(targetPath);
        var size = new FileInfo(targetPath).Length;

        if (existing is not null)
        {
            var oldPath = existing.FilePath;
            var category = string.IsNullOrWhiteSpace(existing.Category) ? null : existing.Category;

            if (existing.IsArchived)
            {
                _images.SetArchived(connection, transaction, existing.Id, archived: false);
            }

            var rowVersion = _images.UpdateLocation(
                connection, transaction, existing.Id, targetPath, fileName, category, size, hash);

            _changes.Record(connection, transaction, "Image", existing.Id.ToString("D"), "Update",
                new { existing.Name, Replaced = true }, rowVersion);
            transaction.Commit();

            FolderService.TryDeleteFile(oldPath, out _);
            return _images.GetById(connection, null, existing.Id)!;
        }

        var image = new LibraryImage
        {
            Name = cleanName,
            ProjectId = projectId,
            FileName = fileName,
            FilePath = targetPath,
            ContentType = contentType,
            SizeBytes = size,
            ContentHash = hash,
            CreatedUtc = DateTime.UtcNow,
            CreatedBy = _actor.UserName,
        };

        _images.Insert(connection, transaction, image);
        _changes.Record(connection, transaction, "Image", image.Id.ToString("D"), "Create",
            new { image.Name, Replaced = true }, 1);
        transaction.Commit();

        return image;
    }

    /// <summary>Liefert den HTML-Baustein für ein Bild (Data-URI) oder null.</summary>
    public string? GetImageTag(Guid? projectId, string name)
    {
        using var connection = _database.Open();
        var images = _images.GetActive(connection, null, projectId);
        var match = images.LastOrDefault(image => string.Equals(image.Name, name, StringComparison.OrdinalIgnoreCase));
        return match is null ? null : ImageEmbedding.ToImageTag(match);
    }

    /// <summary>
    /// Liest Bilddateien aus dem Projektordner ein und ordnet sie dem Projekt zu. Die
    /// Dateien bleiben, wo sie liegen – es wird nichts kopiert. Bereits erfasste Pfade
    /// werden übersprungen; Unterordner werden zur Kategorie.
    /// Verschohene oder umbenannte Dateien werden wiedererkannt – über Name und Größe
    /// oder über den Inhalts-Hash – und ihr gespeicherter Pfad wird aktualisiert, statt
    /// sie als fehlend zu melden und doppelt aufzunehmen.
    /// </summary>
    public ProjectFolderSyncResult SyncProjectFolder(Guid projectId)
    {
        _actor.Require(Permission.ManageImages);

        using var connection = _database.Open();
        var project = _projects.GetById(connection, null, projectId)
                      ?? throw new ValidationException("Das Projekt wurde nicht gefunden.");

        if (string.IsNullOrWhiteSpace(project.FolderPath) || !Directory.Exists(project.FolderPath))
        {
            return new ProjectFolderSyncResult(0, 0);
        }

        var files = Directory.EnumerateFiles(project.FolderPath, "*", SearchOption.AllDirectories)
            .Select(Path.GetFullPath)
            .Where(path => ImageEmbedding.ContentTypeFor(path) is not null)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (files.Length == 0)
        {
            return new ProjectFolderSyncResult(0, 0);
        }

        using var transaction = connection.BeginTransaction();

        var existing = _images.GetForProject(connection, transaction, projectId);
        var knownPaths = existing
            .Select(image => Path.GetFullPath(image.FilePath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var knownNames = existing
            .Select(image => image.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Hash vorhandener Dateien einmalig nachtragen, damit Umbenennungen erkennbar sind.
        foreach (var image in existing)
        {
            if (string.IsNullOrEmpty(image.ContentHash) && File.Exists(image.FilePath))
            {
                var hash = ComputeHash(image.FilePath);
                if (hash is not null)
                {
                    _images.UpdateHash(connection, transaction, image.Id, hash);
                    image.ContentHash = hash;
                }
            }
        }

        // Nur Einträge ohne Datei kommen als „verschoben“ in Frage.
        var missing = existing
            .Where(image => !File.Exists(image.FilePath))
            .ToList();

        var added = 0;
        var moved = 0;

        foreach (var path in files)
        {
            if (!knownPaths.Add(path))
            {
                continue;
            }

            var info = new FileInfo(path);
            string? hash = null;

            // 1) Gleicher Name und gleiche Größe: wandert die Datei mit.
            var relocated = missing.FirstOrDefault(image => SameNameAndSize(image, info));
            if (relocated is not null && !string.IsNullOrEmpty(relocated.ContentHash))
            {
                // Gleicher Name und gleiche Größe heißt nicht gleicher Inhalt.
                hash = ComputeHash(path);
                if (!string.Equals(hash, relocated.ContentHash, StringComparison.OrdinalIgnoreCase))
                {
                    relocated = null;
                }
            }

            // 2) Anderer Name: über den Inhalts-Hash wiedererkennen.
            if (relocated is null && missing.Any(image => !string.IsNullOrEmpty(image.ContentHash)))
            {
                hash ??= ComputeHash(path);
                if (hash is not null)
                {
                    relocated = missing.FirstOrDefault(image =>
                        !string.IsNullOrEmpty(image.ContentHash) &&
                        string.Equals(image.ContentHash, hash, StringComparison.OrdinalIgnoreCase));
                }
            }

            if (relocated is not null)
            {
                missing.Remove(relocated);

                var category = CategoryFor(project.FolderPath, path);
                var renamed = !string.Equals(relocated.FileName, info.Name, StringComparison.OrdinalIgnoreCase);
                hash ??= ComputeHash(path);

                var rowVersion = _images.UpdateLocation(
                    connection, transaction, relocated.Id, path, info.Name, category, info.Length, hash);

                _changes.Record(connection, transaction, "Image", relocated.Id.ToString("D"), "Update",
                    new { relocated.Name, Category = category, Moved = true, Renamed = renamed }, rowVersion);
                moved++;
                continue;
            }

            var name = UniqueName(Path.GetFileNameWithoutExtension(path), knownNames);
            knownNames.Add(name);

            var image = new LibraryImage
            {
                Name = name,
                Category = CategoryFor(project.FolderPath, path),
                ProjectId = projectId,
                FileName = info.Name,
                FilePath = path,
                ContentType = ImageEmbedding.ContentTypeFor(path) ?? "application/octet-stream",
                SizeBytes = info.Length,
                ContentHash = hash ?? ComputeHash(path),
                CreatedUtc = DateTime.UtcNow,
                CreatedBy = _actor.UserName,
            };

            _images.Insert(connection, transaction, image);
            added++;
        }

        if (added + moved > 0)
        {
            _changes.Record(connection, transaction, "Project", projectId.ToString("D"), "ImportImages",
                new { project.Name, Added = added, Moved = moved }, project.RowVersion);
            transaction.Commit();
        }

        return new ProjectFolderSyncResult(added, moved);
    }

    private static bool SameNameAndSize(LibraryImage image, FileInfo file) =>
        string.Equals(image.FileName, file.Name, StringComparison.OrdinalIgnoreCase) &&
        image.SizeBytes == file.Length;

    /// <summary>SHA-256 des Dateiinhalts; <c>null</c>, wenn die Datei nicht lesbar ist.</summary>
    private static string? ComputeHash(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Vereinheitlicht Schlagworte: durch Komma getrennt, ohne Doppelte.</summary>
    public static string? NormalizeTags(string? tags)
    {
        if (string.IsNullOrWhiteSpace(tags))
        {
            return null;
        }

        var values = ImageTags.Split(tags)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        return values.Length == 0 ? null : string.Join(", ", values);
    }

    /// <summary>Der Unterordner im Projektordner wird zur Kategorie des Bildes.</summary>
    private static string? CategoryFor(string root, string filePath)
    {
        var relative = Path.GetRelativePath(root, filePath);
        var folder = Path.GetDirectoryName(relative);

        return string.IsNullOrWhiteSpace(folder) || folder == "."
            ? null
            : folder.Replace('\\', '/');
    }

    private static string UniqueName(string? preferred, IReadOnlySet<string> known)
    {
        var name = string.IsNullOrWhiteSpace(preferred) ? "Bild" : preferred.Trim();
        if (!known.Contains(name))
        {
            return name;
        }

        for (var suffix = 2; suffix < 1000; suffix++)
        {
            var candidate = $"{name} ({suffix})";
            if (!known.Contains(candidate))
            {
                return candidate;
            }
        }

        return $"{name} ({Guid.NewGuid():N})";
    }

    private static string RequireName(string? name)
    {
        var value = (name ?? string.Empty).Trim();
        return value.Length == 0
            ? throw new ValidationException("Bitte einen Namen für das Bild angeben.")
            : value;
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
