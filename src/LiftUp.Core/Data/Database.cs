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
    public const int CurrentSchemaVersion = 1;

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

        using var transaction = connection.BeginTransaction();
        SeedRoles(connection, transaction);
        SeedAdministrator(connection, transaction);
        SeedDeviceId(connection, transaction);
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
                Description = "Darf Projekte anlegen, bearbeiten und einsehen.",
                Permissions = Permission.ViewProjects | Permission.CreateProjects | Permission.EditProjects,
                IsSystem = true,
            },
            new Role
            {
                Name = "Betrachter",
                Description = "Darf Projekte nur einsehen.",
                Permissions = Permission.ViewProjects,
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
            Description TEXT NULL,
            FolderPath  TEXT NOT NULL COLLATE NOCASE,
            Status      INTEGER NOT NULL DEFAULT 0,
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

        CREATE UNIQUE INDEX IF NOT EXISTS UX_Projects_Number
            ON Projects (Number) WHERE Number IS NOT NULL AND IsDeleted = 0;

        CREATE UNIQUE INDEX IF NOT EXISTS UX_Projects_Folder
            ON Projects (FolderPath) WHERE IsDeleted = 0;

        CREATE INDEX IF NOT EXISTS IX_Users_RoleId ON Users (RoleId);
        CREATE INDEX IF NOT EXISTS IX_Projects_Status ON Projects (Status);
        CREATE INDEX IF NOT EXISTS IX_ChangeLog_Pending ON ChangeLog (SyncedUtc) WHERE SyncedUtc IS NULL;

        INSERT INTO SchemaInfo (Version)
        SELECT 1 WHERE NOT EXISTS (SELECT 1 FROM SchemaInfo);
        """;
}
