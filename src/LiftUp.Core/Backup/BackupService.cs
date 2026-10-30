using System.Text.Json;
using LiftUp.Core.Data;
using LiftUp.Core.Domain;
using Microsoft.Data.Sqlite;

namespace LiftUp.Core.Backup;

public enum BackupTargetKind
{
    LocalFolder = 0,
    GoogleDrive = 1,
    OneDrive = 2,
}

/// <summary>Einstellungen für die Datensicherung.</summary>
public sealed class BackupOptions
{
    public BackupTargetKind Target { get; set; } = BackupTargetKind.LocalFolder;

    /// <summary>Zielordner für lokale Sicherungen; leer bedeutet Standardordner.</summary>
    public string LocalFolder { get; set; } = string.Empty;

    public int KeepDays { get; set; } = 30;

    /// <summary>Nach jeder Sicherung zusätzlich in die Cloud laden.</summary>
    public bool UploadToCloud { get; set; }
}

public sealed record BackupFile(string FilePath, DateTime CreatedUtc, long SizeBytes)
{
    public string FileName => Path.GetFileName(FilePath);

    public string CreatedText => CreatedUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm");

    public string SizeText => SizeBytes < 1024
        ? $"{SizeBytes} B"
        : $"{SizeBytes / 1024.0:0.0} KB";
}

/// <summary>
/// Datensicherung der lokalen Datenbank. Sicherungen entstehen mit <c>VACUUM INTO</c>
/// und sind damit auch im laufenden Betrieb konsistent – kein Kopieren einer offenen Datei.
/// </summary>
public sealed class BackupService
{
    public const string DefaultFolderName = "backups";
    private const string OptionsKey = "Backup.Options";
    private static readonly byte[] SqliteHeader = "SQLite format 3\u0000"u8.ToArray();

    private readonly Database _database;
    private readonly string _defaultFolder;

    public BackupService(Database database, string? folder = null)
    {
        _database = database;
        var baseDirectory = Path.GetDirectoryName(database.FilePath) ?? Path.GetTempPath();
        _defaultFolder = folder ?? Path.Combine(baseDirectory, DefaultFolderName);
    }

    public string DefaultFolder => _defaultFolder;

    public BackupOptions LoadOptions()
    {
        var json = _database.GetSetting(OptionsKey);
        if (string.IsNullOrWhiteSpace(json))
        {
            return new BackupOptions();
        }

        try
        {
            return JsonSerializer.Deserialize<BackupOptions>(json) ?? new BackupOptions();
        }
        catch (JsonException)
        {
            return new BackupOptions();
        }
    }

    public void SaveOptions(BackupOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _database.SetSetting(OptionsKey, JsonSerializer.Serialize(options));
    }

    public string ResolveFolder(BackupOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return string.IsNullOrWhiteSpace(options.LocalFolder)
            ? _defaultFolder
            : Path.GetFullPath(options.LocalFolder.Trim());
    }

    /// <summary>Erzeugt eine konsistente Sicherung und räumt alte auf.</summary>
    public BackupFile Create(string? reason = null)
    {
        var options = LoadOptions();
        var folder = ResolveFolder(options);

        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ValidationException($"Der Sicherungsordner konnte nicht angelegt werden: {exception.Message}");
        }

        var suffix = string.IsNullOrWhiteSpace(reason)
            ? string.Empty
            : "-" + Sanitize(reason);
        var target = Path.Combine(folder, $"liftup-{DateTime.Now:yyyyMMdd-HHmmss}{suffix}.db");

        try
        {
            using var connection = _database.Open();
            using var command = Db.Command(connection, "VACUUM INTO $target;");
            command.With("$target", target);
            command.ExecuteNonQuery();
        }
        catch (SqliteException exception)
        {
            throw new ValidationException($"Die Sicherung konnte nicht erstellt werden: {exception.Message}");
        }

        var info = new FileInfo(target);
        var backup = new BackupFile(target, info.LastWriteTimeUtc, info.Length);

        Prune(options.KeepDays);
        return backup;
    }

    public IReadOnlyList<BackupFile> List()
    {
        var folder = ResolveFolder(LoadOptions());
        if (!Directory.Exists(folder))
        {
            return [];
        }

        return Directory.EnumerateFiles(folder, "liftup-*.db")
            .Select(path => new FileInfo(path))
            .OrderByDescending(info => info.LastWriteTimeUtc)
            .Select(info => new BackupFile(info.FullName, info.LastWriteTimeUtc, info.Length))
            .ToArray();
    }

    /// <summary>Löscht Sicherungen, die älter als <paramref name="keepDays"/> Tage sind. Die jüngste bleibt immer.</summary>
    public int Prune(int keepDays)
    {
        if (keepDays <= 0)
        {
            return 0;
        }

        var files = List();
        if (files.Count <= 1)
        {
            return 0;
        }

        var cutoff = DateTime.UtcNow.AddDays(-keepDays);
        var removed = 0;

        // Die neueste Sicherung bleibt erhalten, auch wenn sie älter als die Frist wäre.
        foreach (var file in files.Skip(1))
        {
            if (file.CreatedUtc >= cutoff)
            {
                continue;
            }

            try
            {
                File.Delete(file.FilePath);
                removed++;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Gesperrte Dateien überspringen; beim nächsten Lauf erneut versuchen.
            }
        }

        return removed;
    }

    /// <summary>
    /// Stellt eine Sicherung wieder her. Vorher wird der aktuelle Stand gesichert,
    /// damit ein Fehlgriff rückholbar bleibt.
    /// </summary>
    public BackupFile Restore(string backupPath)
    {
        if (!File.Exists(backupPath))
        {
            throw new ValidationException("Die Sicherung wurde nicht gefunden.");
        }

        if (!HasSqliteHeader(backupPath))
        {
            throw new ValidationException("Die Datei ist keine gültige LiftUp-Sicherung.");
        }

        var safety = Create("vor-wiederherstellung");

        try
        {
            // Alle Verbindungen schließen, sonst hält Windows die Datei gesperrt.
            SqliteConnection.ClearAllPools();

            foreach (var suffix in new[] { "-wal", "-shm" })
            {
                var side = _database.FilePath + suffix;
                if (File.Exists(side))
                {
                    File.Delete(side);
                }
            }

            File.Copy(backupPath, _database.FilePath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ValidationException(
                $"Die Sicherung konnte nicht eingespielt werden: {exception.Message}. Der bisherige Stand liegt unter {safety.FileName}.");
        }

        if (_database.GetSchemaVersion() == 0)
        {
            throw new ValidationException(
                $"Die Sicherung enthält keine LiftUp-Datenbank. Der bisherige Stand liegt unter {safety.FileName}.");
        }

        return safety;
    }

    private static bool HasSqliteHeader(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var buffer = new byte[SqliteHeader.Length];
            var read = stream.Read(buffer, 0, buffer.Length);
            return read == buffer.Length && buffer.SequenceEqual(SqliteHeader);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string Sanitize(string value)
    {
        var cleaned = new string(value
            .Where(character => char.IsLetterOrDigit(character) || character is '-' or '_')
            .ToArray())
            .Trim('-', '_');

        return cleaned.Length == 0 ? "sicherung" : cleaned;
    }
}
