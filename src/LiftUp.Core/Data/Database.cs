using System.Globalization;
using LiftUp.Core.Domain;
using LiftUp.Core.Security;
using Microsoft.Data.Sqlite;

namespace LiftUp.Core.Data;

/// <summary>
/// Kapselt die lokale SQLite-Datenbank: Verbindungen, Schema und Erstbefüllung.
/// Alle Tabellen führen RowVersion, IsDeleted und UTC-Zeitstempel mit, damit die
/// Daten später mit einer zentralen PostgreSQL-Datenbank abgeglichen werden können.
/// </summary>
public sealed class Database
{
    public const int CurrentSchemaVersion = 4;

    private readonly string _connectionString;

    public Database(string? filePath = null)
    {
        FilePath = Path.GetFullPath(filePath ?? AppPaths.GetDatabasePath());
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = FilePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            Pooling = true,
        }.ToString();
    }

    public string FilePath { get; }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    /// <summary>Legt Schema, Systemrollen und ein Startkonto an, falls die Datei neu ist.</summary>
    public void EnsureCreated()
    {
        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = Schema;
        command.ExecuteNonQuery();

        // Bestehende Datenbanken um fehlende Spalten und Rollen ergänzen.
        Migrate(connection);

        using var transaction = connection.BeginTransaction();
        SeedRoles(connection, transaction);
        SeedAdministrator(connection, transaction);
        SeedDeviceId(connection, transaction);
        SeedTemplates(connection, transaction);
        transaction.Commit();
    }

    /// <summary>Version des Schemas; 0 bedeutet, dass die Datenbank noch nicht angelegt ist.</summary>
    public int GetSchemaVersion()
    {
        using var connection = Open();
        using var command = Db.Command(connection, "SELECT Version FROM SchemaInfo LIMIT 1;");
        var value = command.ExecuteScalar();
        return value is null or DBNull ? 0 : Convert.ToInt32(value);
    }

    /// <summary>Eindeutige Kennung dieses Arbeitsplatzes für das Änderungsprotokoll.</summary>
    public string GetDeviceId()
    {
        using var connection = Open();
        return GetDeviceId(connection, null);
    }

    public static string GetDeviceId(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = Db.Command(connection, "SELECT Value FROM Settings WHERE Key = 'DeviceId';", transaction);
        var value = command.ExecuteScalar() as string;
        return string.IsNullOrWhiteSpace(value) ? "unbekannt" : value;
    }

    private static int ReadSchemaVersion(SqliteConnection connection)
    {
        using var command = Db.Command(connection, "SELECT Version FROM SchemaInfo LIMIT 1;");
        var value = command.ExecuteScalar();
        return value is null or DBNull ? 0 : Convert.ToInt32(value);
    }

    /// <summary>Bringt eine bestehende Datenbank auf den aktuellen Schemastand.</summary>
    private static void Migrate(SqliteConnection connection)
    {
        var version = ReadSchemaVersion(connection);
        if (version >= CurrentSchemaVersion)
        {
            return;
        }

        if (version <= 1)
        {
            MigrateToVersion2(connection);
        }

        // Version 3 und 4 legen nur neue Tabellen an; das erledigt das Schema per IF NOT EXISTS.

        using var command = Db.Command(connection, "UPDATE SchemaInfo SET Version = $version;");
        command.With("$version", CurrentSchemaVersion);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Version 2: Ansprechpartner, Verantwortlicher, Archivstempel und eigene
    /// Nummernkreise je Jahr. Bestehende Projekte bleiben unverändert.
    /// </summary>
    private static void MigrateToVersion2(SqliteConnection connection)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var info = Db.Command(connection, "PRAGMA table_info(Projects);"))
        using (var reader = info.ExecuteReader())
        {
            while (reader.Read())
            {
                existing.Add(reader.GetString(1));
            }
        }

        (string Name, string Definition)[] additions =
        [
            ("Contact", "TEXT NULL"),
            ("ResponsibleUserId", "TEXT NULL"),
            ("ResponsibleName", "TEXT NULL"),
            ("ArchivedUtc", "TEXT NULL"),
            ("ArchivedBy", "TEXT NULL"),
        ];

        foreach (var (name, definition) in additions)
        {
            if (existing.Contains(name))
            {
                continue;
            }

            using var alter = Db.Command(connection, $"ALTER TABLE Projects ADD COLUMN {name} {definition};");
            alter.ExecuteNonQuery();
        }

        using var sequence = Db.Command(connection, """
            CREATE TABLE IF NOT EXISTS ProjectNumberSequences (
                Year       INTEGER PRIMARY KEY NOT NULL,
                LastNumber INTEGER NOT NULL DEFAULT 0
            );
            """);
        sequence.ExecuteNonQuery();

        // "Betrachter" heißt künftig "Leser" und darf zusätzlich das Archiv einsehen.
        using var rename = Db.Command(connection, """
            UPDATE Roles SET Name = 'Leser', Permissions = Permissions | $archive
            WHERE Name = 'Betrachter' AND IsSystem = 1;
            """);
        rename.With("$archive", (int)Permission.ViewArchivedProjects);
        rename.ExecuteNonQuery();
    }

    /// <summary>Legt eine brauchbare Startvorlage an, damit der PDF-Generator sofort nutzbar ist.</summary>
    private static void SeedTemplates(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var count = Db.Command(connection, "SELECT COUNT(1) FROM Templates;", transaction);
        if (Convert.ToInt64(count.ExecuteScalar(), CultureInfo.InvariantCulture) > 0)
        {
            return;
        }

        var templateId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        using var insert = Db.Command(connection, """
            INSERT INTO Templates
                (Id, Name, DocumentType, CurrentVersion, IsActive, CreatedUtc, CreatedBy,
                 UpdatedUtc, UpdatedBy, RowVersion, IsDeleted)
            VALUES ($id, 'Standard-Dokument', 'Dokument', 1, 1, $createdUtc, 'System', $createdUtc, 'System', 1, 0);

            INSERT INTO TemplateVersions (Id, TemplateId, Version, Html, CreatedUtc, CreatedBy)
            VALUES ($versionId, $id, 1, $html, $createdUtc, 'System');
            """, transaction);
        insert.With("$id", templateId.ToString("D"))
            .With("$versionId", Guid.NewGuid().ToString("D"))
            .With("$html", DefaultTemplateHtml)
            .With("$createdUtc", now.ToDb());
        insert.ExecuteNonQuery();
    }

    private const string DefaultTemplateHtml = """
        <!DOCTYPE html>
        <html lang="de">
        <head>
          <meta charset="utf-8" />
          <style>
            @page { size: A4; margin: 20mm 18mm; }
            body { font-family: 'Segoe UI', Arial, sans-serif; font-size: 11pt; color: #1f2933; }
            h1 { font-size: 18pt; margin: 0 0 4mm 0; }
            .meta { color: #52606d; font-size: 9.5pt; margin-bottom: 8mm; }
            table { width: 100%; border-collapse: collapse; }
            td { padding: 2mm 0; vertical-align: top; }
            .label { width: 40mm; color: #52606d; }
            .note { margin-top: 8mm; white-space: pre-wrap; }
          </style>
        </head>
        <body>
          <h1>{{project.name}}</h1>
          <div class="meta">Projekt {{project.number}} · {{date}}</div>
          <table>
            <tr><td class="label">Kunde</td><td>{{project.customer}}</td></tr>
            <tr><td class="label">Ansprechpartner</td><td>{{project.contact}}</td></tr>
            <tr><td class="label">Verantwortlich</td><td>{{project.responsible}}</td></tr>
            <tr><td class="label">Status</td><td>{{project.status}}</td></tr>
          </table>
          <div class="note">{{project.description}}</div>
        </body>
        </html>
        """;

    private static void SeedRoles(SqliteConnection connection, SqliteTransaction transaction)
    {
        var roles = new[]
        {
            new Role
            {
                Name = "Administrator",
                Description = "Voller Zugriff auf Projekte, Benutzer und Rechte.",
                Permissions = Permission.All,
                IsSystem = true,
            },
            new Role
            {
                Name = "Projektleiter",
                Description = "Darf Projekte anlegen, bearbeiten, löschen und archivieren.",
                Permissions = Permission.ViewProjects | Permission.CreateProjects | Permission.EditProjects |
                              Permission.DeleteProjects | Permission.ArchiveProjects | Permission.ViewArchivedProjects |
                              Permission.ManageTemplates | Permission.ManageSnippets | Permission.ManageImages |
                              Permission.CreateDocuments | Permission.DeleteDocuments | Permission.ViewChangeLog,
                IsSystem = true,
            },
            new Role
            {
                Name = "Mitarbeiter",
                Description = "Darf Projekte anlegen, bearbeiten und Dokumente erzeugen.",
                Permissions = Permission.ViewProjects | Permission.CreateProjects | Permission.EditProjects |
                              Permission.CreateDocuments | Permission.ManageSnippets | Permission.ManageImages,
                IsSystem = true,
            },
            new Role
            {
                Name = "Leser",
                Description = "Darf Projekte und das Archiv einsehen.",
                Permissions = Permission.ViewProjects | Permission.ViewArchivedProjects,
                IsSystem = true,
            },
        };

        foreach (var role in roles)
        {
            using var exists = Db.Command(connection, "SELECT COUNT(1) FROM Roles WHERE Name = $name;", transaction);
            exists.With("$name", role.Name);
            if (Convert.ToInt64(exists.ExecuteScalar(), CultureInfo.InvariantCulture) > 0)
            {
                continue;
            }

            using var insert = Db.Command(connection, """
                INSERT INTO Roles (Id, Name, Description, Permissions, IsSystem, CreatedUtc, RowVersion)
                VALUES ($id, $name, $description, $permissions, $isSystem, $createdUtc, 1);
                """, transaction);
            insert.With("$id", role.Id.ToString("D"))
                .With("$name", role.Name)
                .With("$description", role.Description)
                .With("$permissions", (int)role.Permissions)
                .With("$isSystem", role.IsSystem ? 1 : 0)
                .With("$createdUtc", role.CreatedUtc.ToDb());
            insert.ExecuteNonQuery();
        }
    }

    private static void SeedAdministrator(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var count = Db.Command(connection, "SELECT COUNT(1) FROM Users;", transaction);
        if (Convert.ToInt64(count.ExecuteScalar(), CultureInfo.InvariantCulture) > 0)
        {
            return;
        }

        using var roleCommand = Db.Command(connection, "SELECT Id FROM Roles WHERE Name = 'Administrator';", transaction);
        var roleId = roleCommand.ExecuteScalar() as string;
        if (string.IsNullOrWhiteSpace(roleId))
        {
            return;
        }

        // Startkonto: admin / admin – das Passwort muss bei der ersten Anmeldung geändert werden.
        var hash = PasswordHasher.Hash("admin");
        using var insert = Db.Command(connection, """
            INSERT INTO Users
                (Id, UserName, DisplayName, RoleId, IsActive, PasswordHash, PasswordSalt, PasswordIterations,
                 MustChangePassword, CreatedUtc, CreatedBy, LastLoginUtc, RowVersion, IsDeleted)
            VALUES
                ($id, 'admin', 'Administrator', $roleId, 1, $hash, $salt, $iterations, 1, $createdUtc, 'System', NULL, 1, 0);
            """, transaction);
        insert.With("$id", Guid.NewGuid().ToString("D"))
            .With("$roleId", roleId)
            .With("$hash", hash.HashBase64)
            .With("$salt", hash.SaltBase64)
            .With("$iterations", hash.Iterations)
            .With("$createdUtc", DateTime.UtcNow.ToDb());
        insert.ExecuteNonQuery();
    }

    private static void SeedDeviceId(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = Db.Command(connection, """
            INSERT INTO Settings (Key, Value)
            SELECT 'DeviceId', $deviceId
            WHERE NOT EXISTS (SELECT 1 FROM Settings WHERE Key = 'DeviceId');
            """, transaction);
        command.With("$deviceId", Guid.NewGuid().ToString("D"));
        command.ExecuteNonQuery();
    }

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS SchemaInfo (
            Version INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS Settings (
            Key   TEXT PRIMARY KEY NOT NULL,
            Value TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS Roles (
            Id          TEXT PRIMARY KEY NOT NULL,
            Name        TEXT NOT NULL COLLATE NOCASE UNIQUE,
            Description TEXT NOT NULL DEFAULT '',
            Permissions INTEGER NOT NULL DEFAULT 0,
            IsSystem    INTEGER NOT NULL DEFAULT 0,
            CreatedUtc  TEXT NOT NULL,
            RowVersion  INTEGER NOT NULL DEFAULT 1
        );

        CREATE TABLE IF NOT EXISTS Users (
            Id                 TEXT PRIMARY KEY NOT NULL,
            UserName           TEXT NOT NULL COLLATE NOCASE UNIQUE,
            DisplayName        TEXT NOT NULL DEFAULT '',
            RoleId             TEXT NOT NULL REFERENCES Roles(Id),
            IsActive           INTEGER NOT NULL DEFAULT 1,
            PasswordHash       TEXT NOT NULL,
            PasswordSalt       TEXT NOT NULL,
            PasswordIterations INTEGER NOT NULL,
            MustChangePassword INTEGER NOT NULL DEFAULT 0,
            CreatedUtc         TEXT NOT NULL,
            CreatedBy          TEXT NULL,
            LastLoginUtc       TEXT NULL,
            RowVersion         INTEGER NOT NULL DEFAULT 1,
            IsDeleted          INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE IF NOT EXISTS Projects (
            Id          TEXT PRIMARY KEY NOT NULL,
            Number      TEXT NULL COLLATE NOCASE,
            Name        TEXT NOT NULL,
            Customer    TEXT NULL,
            Contact     TEXT NULL,
            Description TEXT NULL,
            ResponsibleUserId TEXT NULL,
            ResponsibleName   TEXT NULL,
            FolderPath  TEXT NOT NULL COLLATE NOCASE,
            Status      INTEGER NOT NULL DEFAULT 0,
            ArchivedUtc TEXT NULL,
            ArchivedBy  TEXT NULL,
            CreatedUtc  TEXT NOT NULL,
            CreatedBy   TEXT NULL,
            UpdatedUtc  TEXT NOT NULL,
            UpdatedBy   TEXT NULL,
            RowVersion  INTEGER NOT NULL DEFAULT 1,
            IsDeleted   INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE IF NOT EXISTS ChangeLog (
            Id             INTEGER PRIMARY KEY AUTOINCREMENT,
            EntityType     TEXT NOT NULL,
            EntityId       TEXT NOT NULL,
            Operation      TEXT NOT NULL,
            Payload        TEXT NOT NULL,
            ChangedUtc     TEXT NOT NULL,
            OriginDeviceId TEXT NOT NULL,
            RowVersion     INTEGER NOT NULL,
            SyncedUtc      TEXT NULL
        );

        CREATE TABLE IF NOT EXISTS ProjectNumberSequences (
            Year       INTEGER PRIMARY KEY NOT NULL,
            LastNumber INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE IF NOT EXISTS Templates (
            Id             TEXT PRIMARY KEY NOT NULL,
            Name           TEXT NOT NULL COLLATE NOCASE UNIQUE,
            DocumentType   TEXT NOT NULL DEFAULT '',
            CurrentVersion INTEGER NOT NULL DEFAULT 0,
            IsActive       INTEGER NOT NULL DEFAULT 1,
            CreatedUtc     TEXT NOT NULL,
            CreatedBy      TEXT NULL,
            UpdatedUtc     TEXT NOT NULL,
            UpdatedBy      TEXT NULL,
            RowVersion     INTEGER NOT NULL DEFAULT 1,
            IsDeleted      INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE IF NOT EXISTS TemplateVersions (
            Id         TEXT PRIMARY KEY NOT NULL,
            TemplateId TEXT NOT NULL REFERENCES Templates(Id),
            Version    INTEGER NOT NULL,
            Html       TEXT NOT NULL,
            CreatedUtc TEXT NOT NULL,
            CreatedBy  TEXT NOT NULL,
            UNIQUE (TemplateId, Version)
        );

        CREATE TABLE IF NOT EXISTS ProjectDocuments (
            Id              TEXT PRIMARY KEY NOT NULL,
            ProjectId       TEXT NOT NULL REFERENCES Projects(Id),
            TemplateId      TEXT NOT NULL,
            TemplateName    TEXT NOT NULL,
            TemplateVersion INTEGER NOT NULL,
            FileName        TEXT NOT NULL,
            FilePath        TEXT NOT NULL,
            SizeBytes       INTEGER NOT NULL DEFAULT 0,
            CreatedUtc      TEXT NOT NULL,
            CreatedBy       TEXT NOT NULL,
            RowVersion      INTEGER NOT NULL DEFAULT 1,
            IsDeleted       INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE IF NOT EXISTS TextSnippets (
            Id         TEXT PRIMARY KEY NOT NULL,
            Name       TEXT NOT NULL COLLATE NOCASE UNIQUE,
            Category   TEXT NULL,
            Html       TEXT NOT NULL,
            IsArchived INTEGER NOT NULL DEFAULT 0,
            Version    INTEGER NOT NULL DEFAULT 1,
            CreatedUtc TEXT NOT NULL,
            CreatedBy  TEXT NULL,
            UpdatedUtc TEXT NOT NULL,
            UpdatedBy  TEXT NULL,
            RowVersion INTEGER NOT NULL DEFAULT 1
        );

        CREATE TABLE IF NOT EXISTS Images (
            Id          TEXT PRIMARY KEY NOT NULL,
            Name        TEXT NOT NULL COLLATE NOCASE,
            Category    TEXT NULL,
            Description TEXT NULL,
            ProjectId   TEXT NULL REFERENCES Projects(Id),
            FileName    TEXT NOT NULL,
            FilePath    TEXT NOT NULL,
            ContentType TEXT NOT NULL,
            SizeBytes   INTEGER NOT NULL DEFAULT 0,
            IsArchived  INTEGER NOT NULL DEFAULT 0,
            CreatedUtc  TEXT NOT NULL,
            CreatedBy   TEXT NULL,
            RowVersion  INTEGER NOT NULL DEFAULT 1
        );

        CREATE UNIQUE INDEX IF NOT EXISTS UX_Images_GlobalName
            ON Images (Name) WHERE ProjectId IS NULL;

        CREATE UNIQUE INDEX IF NOT EXISTS UX_Images_ProjectName
            ON Images (ProjectId, Name) WHERE ProjectId IS NOT NULL;

        CREATE UNIQUE INDEX IF NOT EXISTS UX_Projects_Number
            ON Projects (Number) WHERE Number IS NOT NULL AND IsDeleted = 0;

        CREATE UNIQUE INDEX IF NOT EXISTS UX_Projects_Folder
            ON Projects (FolderPath) WHERE IsDeleted = 0;

        CREATE INDEX IF NOT EXISTS IX_Users_RoleId ON Users (RoleId);
        CREATE INDEX IF NOT EXISTS IX_Projects_Status ON Projects (Status);
        CREATE INDEX IF NOT EXISTS IX_ChangeLog_Pending ON ChangeLog (SyncedUtc) WHERE SyncedUtc IS NULL;
        CREATE INDEX IF NOT EXISTS IX_ProjectDocuments_Project ON ProjectDocuments (ProjectId);

        INSERT INTO SchemaInfo (Version)
        SELECT 4 WHERE NOT EXISTS (SELECT 1 FROM SchemaInfo);
        """;
}
