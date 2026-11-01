using System.Text.Json;
using LiftUp.Core.Data;
using LiftUp.Core.Domain;
using Microsoft.Data.Sqlite;

namespace LiftUp.Core.Repositories;

/// <summary>
/// Fotos der Analyse je Projekt und Aufzug. Die Markierungen liegen als JSON an der
/// Zeile; die Bilddatei selbst bleibt im Projektordner.
/// </summary>
public sealed class ProjectAnalysisPhotoRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public List<ProjectAnalysisPhoto> Get(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid projectId)
    {
        using var command = Db.Command(connection, """
            SELECT Id, ProjectId, LiftKey, FilePath, Caption, Annotations, ImageWidth, ImageHeight,
                   SortOrder, RowVersion
            FROM ProjectAnalysisPhotos
            WHERE ProjectId = $projectId AND IsDeleted = 0
            ORDER BY LiftKey, SortOrder, Id;
            """, transaction);
        command.With("$projectId", projectId.ToString("D"));

        using var reader = command.ExecuteReader();
        var photos = new List<ProjectAnalysisPhoto>();
        while (reader.Read())
        {
            photos.Add(new ProjectAnalysisPhoto
            {
                Id = reader.Guid(0),
                ProjectId = reader.Guid(1),
                LiftKey = reader.Text(2),
                FilePath = reader.Text(3),
                Caption = reader.Text(4),
                Annotations = Deserialize(reader.Text(5)),
                ImageWidth = reader.GetDouble(6),
                ImageHeight = reader.GetDouble(7),
                SortOrder = reader.Int32(8),
                RowVersion = reader.Int64(9),
            });
        }

        return photos;
    }

    /// <summary>
    /// Schreibt die Fotos eines Aufzugs neu; Fotos der übrigen Aufzüge bleiben unberührt.
    /// Bisherige Zeilen werden weich gelöscht.
    /// </summary>
    public List<ProjectAnalysisPhoto> Replace(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid projectId,
        string liftKey,
        IReadOnlyList<ProjectAnalysisPhotoInput> photos,
        string changedBy)
    {
        using (var retire = Db.Command(connection, """
            UPDATE ProjectAnalysisPhotos
            SET IsDeleted = 1, RowVersion = RowVersion + 1
            WHERE ProjectId = $projectId AND LiftKey = $liftKey AND IsDeleted = 0;
            """, transaction))
        {
            retire.With("$projectId", projectId.ToString("D")).With("$liftKey", liftKey);
            retire.ExecuteNonQuery();
        }

        var now = DateTime.UtcNow;
        var saved = new List<ProjectAnalysisPhoto>(photos.Count);

        for (var index = 0; index < photos.Count; index++)
        {
            var input = photos[index];
            var photo = new ProjectAnalysisPhoto
            {
                ProjectId = projectId,
                LiftKey = liftKey,
                FilePath = input.FilePath,
                Caption = input.Caption,
                Annotations = [.. input.Annotations],
                ImageWidth = input.ImageWidth,
                ImageHeight = input.ImageHeight,
                SortOrder = index,
            };

            using var insert = Db.Command(connection, """
                INSERT INTO ProjectAnalysisPhotos
                    (Id, ProjectId, LiftKey, FilePath, Caption, Annotations, ImageWidth, ImageHeight,
                     SortOrder, CreatedUtc, CreatedBy, UpdatedUtc, UpdatedBy, RowVersion, IsDeleted)
                VALUES ($id, $projectId, $liftKey, $filePath, $caption, $annotations, $imageWidth,
                        $imageHeight, $sortOrder, $createdUtc, $createdBy, $updatedUtc, $updatedBy, 1, 0);
                """, transaction);
            insert.With("$id", photo.Id.ToString("D"))
                .With("$projectId", projectId.ToString("D"))
                .With("$liftKey", liftKey)
                .With("$filePath", photo.FilePath)
                .With("$caption", photo.Caption)
                .With("$annotations", JsonSerializer.Serialize(photo.Annotations, JsonOptions))
                .With("$imageWidth", photo.ImageWidth)
                .With("$imageHeight", photo.ImageHeight)
                .With("$sortOrder", photo.SortOrder)
                .With("$createdUtc", now.ToDb())
                .With("$createdBy", changedBy)
                .With("$updatedUtc", now.ToDb())
                .With("$updatedBy", changedBy);
            insert.ExecuteNonQuery();

            saved.Add(photo);
        }

        return saved;
    }

    private static List<PhotoAnnotation> Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<PhotoAnnotation>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
