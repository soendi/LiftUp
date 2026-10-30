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
    private readonly ChangeLogWriter _changes;
    private readonly string _libraryRoot;

    public ImageService(Database database, Session actor, string? libraryRoot = null)
    {
        _database = database;
        _actor = actor;
        _changes = new ChangeLogWriter(database);

        var baseDirectory = Path.GetDirectoryName(database.FilePath) ?? Path.GetTempPath();
        _libraryRoot = libraryRoot ?? Path.Combine(baseDirectory, "bilder");
    }

    public string LibraryRoot => _libraryRoot;

    public IReadOnlyList<ImageListItem> Search(Guid? projectId, string? search, bool includeArchived = false)
    {
        _actor.Require(Permission.ViewProjects);
        using var connection = _database.Open();
        return _images.Search(connection, null, projectId, search, includeArchived);
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

        var name = string.IsNullOrWhiteSpace(input.Name)
            ? Path.GetFileNameWithoutExtension(sourcePath)
            : input.Name.Trim();

        if (name.Length == 0)
        {
            throw new ValidationException("Bitte einen Namen für das Bild angeben.");
        }

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
            Category = string.IsNullOrWhiteSpace(input.Category) ? null : input.Category.Trim(),
            Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim(),
            ProjectId = input.ProjectId,
            FileName = fileName,
            FilePath = targetPath,
            ContentType = contentType,
            SizeBytes = new FileInfo(targetPath).Length,
            CreatedUtc = DateTime.UtcNow,
            CreatedBy = _actor.UserName,
        };

        _images.Insert(connection, transaction, image);
        _changes.Record(connection, transaction, "Image", image.Id.ToString("D"), "Create",
            new { image.Name, image.Category, image.ProjectId }, 1);
        transaction.Commit();

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

    /// <summary>Liefert den HTML-Baustein für ein Bild (Data-URI) oder null.</summary>
    public string? GetImageTag(Guid? projectId, string name)
    {
        using var connection = _database.Open();
        var images = _images.GetActive(connection, null, projectId);
        var match = images.LastOrDefault(image => string.Equals(image.Name, name, StringComparison.OrdinalIgnoreCase));
        return match is null ? null : ImageEmbedding.ToImageTag(match);
    }
}
