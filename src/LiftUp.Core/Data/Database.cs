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
    public const int CurrentSchemaVersion = 16;

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
        // busy_timeout: Bei gleichzeitigen Schreibzugriffen (etwa durch die
        // Synchronisation) wartet SQLite kurz, statt sofort abzubrechen.
        pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL; PRAGMA busy_timeout = 5000;";
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
        SeedDocumentTypes(connection, transaction);
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

    public string? GetSetting(string key)
    {
        using var connection = Open();
        using var command = Db.Command(connection, "SELECT Value FROM Settings WHERE Key = $key;");
        command.With("$key", key);
        return command.ExecuteScalar() as string;
    }

    public void SetSetting(string key, string value)
    {
        using var connection = Open();
        using var command = Db.Command(connection, """
            INSERT INTO Settings (Key, Value) VALUES ($key, $value)
            ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;
            """);
        command.With("$key", key).With("$value", value);
        command.ExecuteNonQuery();
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

        // Version 3 bis 5 legen nur neue Tabellen an; das erledigt das Schema per IF NOT EXISTS.
        MigrateDocumentTypes(connection);

        // Version 6 ergänzt den Bearbeiter im Änderungsprotokoll.
        MigrateChangeLogActor(connection);

        // Version 7 ergänzt die Kundenanschrift und eigene Ansprechpersonen.
        MigrateCustomerContacts(connection);

        // Version 8 gibt dem Administrator Rechte, die erst nach dem Anlegen der Rolle dazukamen.
        MigrateAdministratorPermissions(connection);

        // Version 9 ergänzt die Abschnitte (Untertitel) je Vorlage.
        MigrateTemplateSections(connection);

        // Version 10 hält die ausgefüllten Abschnitte am erzeugten Dokument fest.
        MigrateDocumentSections(connection);

        // Version 11 legt Firma, E-Mail und Telefon direkt am Benutzer ab.
        MigrateUserProfile(connection);

        // Version 12 ergänzt Schlagworte an Bildern.
        MigrateImageTags(connection);

        // Version 13 merkt sich den Inhalts-Hash, um umbenannte Bilder wiederzuerkennen.
        MigrateImageContentHash(connection);

        // Version 14 ergänzt die zweite Adresszeile und die Anschrift des Auftraggebers.
        MigrateProjectAddresses(connection);

        // Version 15 legt die Tabellen für Zustandsanalyse und Investitionsplanung an.
        MigrateProjectTables(connection);

        // Version 16 gibt den Abschnitten einen Startinhalt und ersetzt die Startvorlage
        // durch den Zustandsbericht Aufzugsanlagen.
        MigrateTemplateSectionContent(connection);
        EnsureReportTemplate(connection);

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

    /// <summary>
    /// Version 6: hält im Änderungsprotokoll fest, wer eine Änderung ausgelöst hat.
    /// Bestehende Einträge bleiben ohne Bearbeiter.
    /// </summary>
    private static void MigrateChangeLogActor(SqliteConnection connection)
    {
        var hasColumn = false;
        using (var info = Db.Command(connection, "PRAGMA table_info(ChangeLog);"))
        using (var reader = info.ExecuteReader())
        {
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), "OriginUser", StringComparison.OrdinalIgnoreCase))
                {
                    hasColumn = true;
                }
            }
        }

        if (hasColumn)
        {
            return;
        }

        using var alter = Db.Command(connection, "ALTER TABLE ChangeLog ADD COLUMN OriginUser TEXT NULL;");
        alter.ExecuteNonQuery();
    }

    /// <summary>
    /// Version 7: Kundenanschrift am Projekt und beliebig viele Ansprechpersonen.
    /// Bestehende Projekte behalten ihren einzelnen Ansprechpartner im Feld <c>Contact</c>.
    /// </summary>
    private static void MigrateCustomerContacts(SqliteConnection connection)
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
            ("CustomerStreet", "TEXT NULL"),
            ("CustomerZip", "TEXT NULL"),
            ("CustomerCity", "TEXT NULL"),
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

        using var create = Db.Command(connection, """
            CREATE TABLE IF NOT EXISTS ProjectContacts (
                Id             TEXT PRIMARY KEY NOT NULL,
                ProjectId      TEXT NOT NULL REFERENCES Projects(Id),
                Name           TEXT NOT NULL,
                Email          TEXT NULL,
                Phone          TEXT NULL,
                Responsibility TEXT NULL,
                SortOrder      INTEGER NOT NULL DEFAULT 0,
                CreatedUtc     TEXT NOT NULL,
                CreatedBy      TEXT NULL,
                RowVersion     INTEGER NOT NULL DEFAULT 1,
                IsDeleted      INTEGER NOT NULL DEFAULT 0
            );

            CREATE INDEX IF NOT EXISTS IX_ProjectContacts_Project ON ProjectContacts (ProjectId);
            """);
        create.ExecuteNonQuery();
    }

    /// <summary>
    /// Version 8: Rollen speichern ihre Rechte als Maske, die beim Anlegen einmalig
    /// geschrieben wird. Später ergänzte Rechte – etwa <c>ManageSnippets</c> – fehlten
    /// dadurch in älteren Datenbanken selbst beim Administrator. Er gilt in der
    /// Rollenverwaltung ohnehin immer als voll berechtigt, deshalb bekommt er hier
    /// alle Rechte. Selbst angelegte Rollen bleiben unverändert.
    /// </summary>
    private static void MigrateAdministratorPermissions(SqliteConnection connection)
    {
        using var command = Db.Command(connection, """
            UPDATE Roles SET Permissions = $all, RowVersion = RowVersion + 1
            WHERE Name = 'Administrator' AND IsSystem = 1 AND Permissions <> $all;
            """);
        command.With("$all", (int)Permission.All);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Version 9: Vorlagen können Abschnitte (Untertitel) definieren, die beim
    /// Erstellen eines Dokuments als Tabs ausgefüllt werden.
    /// </summary>
    private static void MigrateTemplateSections(SqliteConnection connection)
    {
        using var command = Db.Command(connection, """
            CREATE TABLE IF NOT EXISTS TemplateSections (
                Id         TEXT PRIMARY KEY NOT NULL,
                TemplateId TEXT NOT NULL REFERENCES Templates(Id),
                Title      TEXT NOT NULL,
                SortOrder  INTEGER NOT NULL DEFAULT 0,
                CreatedUtc TEXT NOT NULL,
                CreatedBy  TEXT NULL,
                RowVersion INTEGER NOT NULL DEFAULT 1
            );

            CREATE INDEX IF NOT EXISTS IX_TemplateSections_Template ON TemplateSections (TemplateId);
            """);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Version 10: Erzeugte Dokumente merken sich ihre Abschnittsinhalte, damit sie
    /// später erneut geöffnet, geändert oder versendet werden können.
    /// </summary>
    private static void MigrateDocumentSections(SqliteConnection connection)
    {
        var hasColumn = false;
        using (var info = Db.Command(connection, "PRAGMA table_info(ProjectDocuments);"))
        using (var reader = info.ExecuteReader())
        {
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), "SectionsJson", StringComparison.OrdinalIgnoreCase))
                {
                    hasColumn = true;
                }
            }
        }

        if (hasColumn)
        {
            return;
        }

        using var alter = Db.Command(connection, "ALTER TABLE ProjectDocuments ADD COLUMN SectionsJson TEXT NULL;");
        alter.ExecuteNonQuery();
    }

    /// <summary>
    /// Version 11: Absenderdaten gehören zum Benutzer statt in globale Einstellungen.
    /// Bestehende Konten bleiben leer, bis sie ihre Angaben erfassen.
    /// </summary>
    private static void MigrateUserProfile(SqliteConnection connection)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var info = Db.Command(connection, "PRAGMA table_info(Users);"))
        using (var reader = info.ExecuteReader())
        {
            while (reader.Read())
            {
                existing.Add(reader.GetString(1));
            }
        }

        (string Name, string Definition)[] additions =
        [
            ("Company", "TEXT NULL"),
            ("Email", "TEXT NULL"),
            ("Phone", "TEXT NULL"),
        ];

        foreach (var (name, definition) in additions)
        {
            if (existing.Contains(name))
            {
                continue;
            }

            using var alter = Db.Command(connection, $"ALTER TABLE Users ADD COLUMN {name} {definition};");
            alter.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Version 12: Bilder lassen sich mit Schlagworten versehen. Sie ergänzen Name und
    /// Kategorie in der Bildsuche, ohne dass bestehende Bilder etwas verlieren.
    /// </summary>
    private static void MigrateImageTags(SqliteConnection connection)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var info = Db.Command(connection, "PRAGMA table_info(Images);"))
        using (var reader = info.ExecuteReader())
        {
            while (reader.Read())
            {
                existing.Add(reader.GetString(1));
            }
        }

        if (existing.Contains("Tags"))
        {
            return;
        }

        using var alter = Db.Command(connection, "ALTER TABLE Images ADD COLUMN Tags TEXT NULL;");
        alter.ExecuteNonQuery();
    }

    /// <summary>
    /// Version 13: Bilder merken sich einen Hash ihres Inhalts. Damit werden umbenannte
    /// oder verschobene Dateien beim Ordnerabgleich wiedererkannt. Bestehende Bilder
    /// bekommen den Hash beim nächsten Abgleich nachgetragen.
    /// </summary>
    private static void MigrateImageContentHash(SqliteConnection connection)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var info = Db.Command(connection, "PRAGMA table_info(Images);"))
        using (var reader = info.ExecuteReader())
        {
            while (reader.Read())
            {
                existing.Add(reader.GetString(1));
            }
        }

        if (existing.Contains("ContentHash"))
        {
            return;
        }

        using var alter = Db.Command(connection, "ALTER TABLE Images ADD COLUMN ContentHash TEXT NULL;");
        alter.ExecuteNonQuery();
    }

    /// <summary>
    /// Version 14: zweite Adresszeile beim Kunden und eine eigene Anschrift für den
    /// Auftraggeber. Bestehende Projekte behalten ihre bisherige Anschrift.
    /// </summary>
    private static void MigrateProjectAddresses(SqliteConnection connection)
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
            ("CustomerLine2", "TEXT NULL"),
            ("Client", "TEXT NULL"),
            ("ClientStreet", "TEXT NULL"),
            ("ClientLine2", "TEXT NULL"),
            ("ClientZip", "TEXT NULL"),
            ("ClientCity", "TEXT NULL"),
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
    }

    /// <summary>
    /// Version 15: Zeilen für Zustandsanalyse und Investitionsplanung. Die Werte einer Zeile
    /// liegen als JSON an den Spaltenschlüsseln; neue Spalten brauchen deshalb keine Migration.
    /// </summary>
    private static void MigrateProjectTables(SqliteConnection connection)
    {
        using var command = Db.Command(connection, """
            CREATE TABLE IF NOT EXISTS ProjectTableRows (
                Id         TEXT PRIMARY KEY NOT NULL,
                ProjectId  TEXT NOT NULL REFERENCES Projects(Id),
                TableKey   TEXT NOT NULL,
                Data       TEXT NOT NULL,
                SortOrder  INTEGER NOT NULL DEFAULT 0,
                CreatedUtc TEXT NOT NULL,
                CreatedBy  TEXT NULL,
                UpdatedUtc TEXT NOT NULL,
                UpdatedBy  TEXT NULL,
                RowVersion INTEGER NOT NULL DEFAULT 1,
                IsDeleted  INTEGER NOT NULL DEFAULT 0
            );

            CREATE INDEX IF NOT EXISTS IX_ProjectTableRows_Project ON ProjectTableRows (ProjectId, TableKey);
            """);
        command.ExecuteNonQuery();
    }

    /// <summary>Übernimmt vorhandene Typnamen bestehender Vorlagen in die Typenliste.</summary>
    private static void MigrateDocumentTypes(SqliteConnection connection)
    {
        using var command = Db.Command(connection, """
            INSERT INTO DocumentTypes (Id, Name, SortOrder, IsArchived, CreatedUtc)
            SELECT lower(hex(randomblob(16))), t.Name, 100, 0, $createdUtc
            FROM (SELECT DISTINCT DocumentType AS Name FROM Templates
                  WHERE DocumentType IS NOT NULL AND trim(DocumentType) <> '') t
            WHERE NOT EXISTS (SELECT 1 FROM DocumentTypes d WHERE d.Name = t.Name);
            """);
        command.With("$createdUtc", DateTime.UtcNow.ToDb());
        command.ExecuteNonQuery();
    }

    private static void SeedDocumentTypes(SqliteConnection connection, SqliteTransaction transaction)
    {
        string[] defaults = ["Angebot", "Rechnung", "Lieferschein", "Bericht", "Korrespondenz", "Dokument"];
        var sort = 1;

        foreach (var name in defaults)
        {
            using var insert = Db.Command(connection, """
                INSERT INTO DocumentTypes (Id, Name, SortOrder, IsArchived, CreatedUtc)
                SELECT lower(hex(randomblob(16))), $name, $sort, 0, $createdUtc
                WHERE NOT EXISTS (SELECT 1 FROM DocumentTypes WHERE Name = $name);
                """, transaction);
            insert.With("$name", name)
                .With("$sort", sort++)
                .With("$createdUtc", DateTime.UtcNow.ToDb());
            insert.ExecuteNonQuery();
        }
    }

    /// <summary>Legt den Zustandsbericht an, damit der PDF-Generator sofort nutzbar ist.</summary>
    private static void SeedTemplates(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var count = Db.Command(connection, "SELECT COUNT(1) FROM Templates;", transaction);
        if (Convert.ToInt64(count.ExecuteScalar(), CultureInfo.InvariantCulture) > 0)
        {
            return;
        }

        EnsureReportTemplate(connection);
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
          </style>
        </head>
        <body>
          <h1>Zustandsbericht Aufzugsanlagen</h1>
          <div class="meta">Projekt {{project.number}} · {{project.name}} · {{date}}</div>
          <table>
            <tr><td class="label">Kunde</td><td>{{project.customer}}</td></tr>
            <tr><td class="label">Anschrift</td><td>{{project.address}}</td></tr>
            <tr><td class="label">Auftraggeber</td><td>{{project.client}}</td></tr>
            <tr><td class="label">Verantwortlich</td><td>{{project.responsible}}</td></tr>
          </table>
        </body>
        </html>
        """;

    /// <summary>Name der mitgelieferten Vorlage für den Zustandsbericht.</summary>
    private const string ReportTemplateName = "Zustandsbericht Aufzugsanlagen";

    /// <summary>
    /// Tabelle der Anlagedaten – der Startinhalt des ersten Tabs. Die Werte werden beim
    /// Erstellen des Dokuments ergänzt.
    /// </summary>
    private const string AnlagedatenTableHtml = """
        <table style="width:100%;border-collapse:collapse;font-size:10.5pt;">
          <tr><td style="width:55mm;padding:1.8mm 2mm;border-bottom:1px solid #cbd2d9;color:#52606d;">Aufzugstyp:</td><td style="padding:1.8mm 2mm;border-bottom:1px solid #cbd2d9;">&nbsp;</td></tr>
          <tr><td style="padding:1.8mm 2mm;border-bottom:1px solid #cbd2d9;color:#52606d;">Jahrgang:</td><td style="padding:1.8mm 2mm;border-bottom:1px solid #cbd2d9;">&nbsp;</td></tr>
          <tr><td style="padding:1.8mm 2mm;border-bottom:1px solid #cbd2d9;color:#52606d;">Antrieb:</td><td style="padding:1.8mm 2mm;border-bottom:1px solid #cbd2d9;">&nbsp;</td></tr>
          <tr><td style="padding:1.8mm 2mm;border-bottom:1px solid #cbd2d9;color:#52606d;">Steuerung / Regler:</td><td style="padding:1.8mm 2mm;border-bottom:1px solid #cbd2d9;">&nbsp;</td></tr>
          <tr><td style="padding:1.8mm 2mm;border-bottom:1px solid #cbd2d9;color:#52606d;">Fabrikat:</td><td style="padding:1.8mm 2mm;border-bottom:1px solid #cbd2d9;">&nbsp;</td></tr>
          <tr><td style="padding:1.8mm 2mm;border-bottom:1px solid #cbd2d9;color:#52606d;">Nutzlast:</td><td style="padding:1.8mm 2mm;border-bottom:1px solid #cbd2d9;">&nbsp;</td></tr>
          <tr><td style="padding:1.8mm 2mm;border-bottom:1px solid #cbd2d9;color:#52606d;">Geschwindigkeit:</td><td style="padding:1.8mm 2mm;border-bottom:1px solid #cbd2d9;">&nbsp;</td></tr>
          <tr><td style="padding:1.8mm 2mm;border-bottom:1px solid #cbd2d9;color:#52606d;">Haltestellen:</td><td style="padding:1.8mm 2mm;border-bottom:1px solid #cbd2d9;">&nbsp;</td></tr>
          <tr><td style="padding:1.8mm 2mm;border-bottom:1px solid #cbd2d9;color:#52606d;">Zugänge:</td><td style="padding:1.8mm 2mm;border-bottom:1px solid #cbd2d9;">&nbsp;</td></tr>
          <tr><td style="padding:1.8mm 2mm;border-bottom:1px solid #cbd2d9;color:#52606d;">Förderhöhe:</td><td style="padding:1.8mm 2mm;border-bottom:1px solid #cbd2d9;">&nbsp;</td></tr>
          <tr><td style="padding:1.8mm 2mm;border-bottom:1px solid #cbd2d9;color:#52606d;">Maschinenraum:</td><td style="padding:1.8mm 2mm;border-bottom:1px solid #cbd2d9;">&nbsp;</td></tr>
          <tr><td style="padding:1.8mm 2mm;border-bottom:1px solid #cbd2d9;color:#52606d;">Kabinenabschluss:</td><td style="padding:1.8mm 2mm;border-bottom:1px solid #cbd2d9;">&nbsp;</td></tr>
          <tr><td style="padding:1.8mm 2mm;border-bottom:1px solid #cbd2d9;color:#52606d;">Schachttüren:</td><td style="padding:1.8mm 2mm;border-bottom:1px solid #cbd2d9;">&nbsp;</td></tr>
          <tr><td style="padding:1.8mm 2mm;border-bottom:1px solid #cbd2d9;color:#52606d;">Schachtausführung:</td><td style="padding:1.8mm 2mm;border-bottom:1px solid #cbd2d9;">&nbsp;</td></tr>
          <tr><td style="padding:1.8mm 2mm;border-bottom:1px solid #cbd2d9;color:#52606d;">Auffahrsockel:</td><td style="padding:1.8mm 2mm;border-bottom:1px solid #cbd2d9;">&nbsp;</td></tr>
        </table>
        """;

    /// <summary>Die Tabs des Zustandsberichts; nur der erste bringt Inhalt mit.</summary>
    private static readonly (string Title, string DefaultHtml)[] ReportSections =
    [
        ("1. Anlagedaten", AnlagedatenTableHtml),
        ("2. Analyse", string.Empty),
        ("3. Fazit / Empfehlung", string.Empty),
    ];

    /// <summary>
    /// Version 16: Abschnitte einer Vorlage dürfen einen Startinhalt mitbringen – so enthält
    /// der Tab „Anlagedaten“ des Zustandsberichts bereits die Tabelle.
    /// </summary>
    private static void MigrateTemplateSectionContent(SqliteConnection connection)
    {
        var hasColumn = false;
        using (var info = Db.Command(connection, "PRAGMA table_info(TemplateSections);"))
        using (var reader = info.ExecuteReader())
        {
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), "DefaultHtml", StringComparison.OrdinalIgnoreCase))
                {
                    hasColumn = true;
                }
            }
        }

        if (hasColumn)
        {
            return;
        }

        using var alter = Db.Command(connection,
            "ALTER TABLE TemplateSections ADD COLUMN DefaultHtml TEXT NOT NULL DEFAULT '';");
        alter.ExecuteNonQuery();
    }

    /// <summary>
    /// Sorgt dafür, dass der Zustandsbericht als Vorlage vorhanden ist. Eine noch unberührte
    /// Startvorlage übernimmt dabei Namen und Inhalt, damit keine zweite Vorlage entsteht.
    /// </summary>
    private static void EnsureReportTemplate(SqliteConnection connection)
    {
        var existing = FindTemplateId(connection, ReportTemplateName, onlyFirstVersion: false);
        if (existing is { } present)
        {
            if (!HasTemplateSections(connection, present))
            {
                AddTemplateSections(connection, present);
            }

            return;
        }

        var legacy = FindTemplateId(connection, "Standard-Dokument", onlyFirstVersion: true);
        if (legacy is { } legacyId)
        {
            using (var rename = Db.Command(connection, """
                UPDATE Templates
                SET Name = $name, DocumentType = 'Bericht', UpdatedUtc = $utc, UpdatedBy = 'System',
                    RowVersion = RowVersion + 1
                WHERE Id = $id;
                """))
            {
                rename.With("$id", legacyId.ToString("D"))
                    .With("$name", ReportTemplateName)
                    .With("$utc", DateTime.UtcNow.ToDb());
                rename.ExecuteNonQuery();
            }

            var version = NextTemplateVersion(connection, legacyId);
            InsertTemplateVersion(connection, legacyId, version, DefaultTemplateHtml);
            SetTemplateVersion(connection, legacyId, version);
            AddTemplateSections(connection, legacyId);
            return;
        }

        var templateId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        using (var insert = Db.Command(connection, """
            INSERT INTO Templates
                (Id, Name, DocumentType, CurrentVersion, IsActive, CreatedUtc, CreatedBy,
                 UpdatedUtc, UpdatedBy, RowVersion, IsDeleted)
            VALUES ($id, $name, 'Bericht', 1, 1, $createdUtc, 'System', $createdUtc, 'System', 1, 0);
            """))
        {
            insert.With("$id", templateId.ToString("D"))
                .With("$name", ReportTemplateName)
                .With("$createdUtc", now.ToDb());
            insert.ExecuteNonQuery();
        }

        InsertTemplateVersion(connection, templateId, 1, DefaultTemplateHtml);
        AddTemplateSections(connection, templateId);
    }

    private static Guid? FindTemplateId(SqliteConnection connection, string name, bool onlyFirstVersion)
    {
        using var command = Db.Command(connection, onlyFirstVersion
            ? "SELECT Id FROM Templates WHERE Name = $name AND CurrentVersion = 1 AND IsDeleted = 0;"
            : "SELECT Id FROM Templates WHERE Name = $name AND IsDeleted = 0;");
        command.With("$name", name);

        return command.ExecuteScalar() is string value && Guid.TryParse(value, out var id) ? id : null;
    }

    private static bool HasTemplateSections(SqliteConnection connection, Guid templateId)
    {
        using var command = Db.Command(connection, "SELECT COUNT(1) FROM TemplateSections WHERE TemplateId = $id;");
        command.With("$id", templateId.ToString("D"));
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    private static int NextTemplateVersion(SqliteConnection connection, Guid templateId)
    {
        using var command = Db.Command(connection, "SELECT MAX(Version) FROM TemplateVersions WHERE TemplateId = $id;");
        command.With("$id", templateId.ToString("D"));
        var value = command.ExecuteScalar();
        return value is null or DBNull ? 1 : Convert.ToInt32(value, CultureInfo.InvariantCulture) + 1;
    }

    private static void InsertTemplateVersion(SqliteConnection connection, Guid templateId, int version, string html)
    {
        using var insert = Db.Command(connection, """
            INSERT INTO TemplateVersions (Id, TemplateId, Version, Html, CreatedUtc, CreatedBy)
            VALUES ($id, $templateId, $version, $html, $createdUtc, 'System');
            """);
        insert.With("$id", Guid.NewGuid().ToString("D"))
            .With("$templateId", templateId.ToString("D"))
            .With("$version", version)
            .With("$html", html)
            .With("$createdUtc", DateTime.UtcNow.ToDb());
        insert.ExecuteNonQuery();
    }

    private static void SetTemplateVersion(SqliteConnection connection, Guid templateId, int version)
    {
        using var command = Db.Command(connection, """
            UPDATE Templates SET CurrentVersion = $version, UpdatedUtc = $utc, UpdatedBy = 'System',
                RowVersion = RowVersion + 1
            WHERE Id = $id;
            """);
        command.With("$id", templateId.ToString("D"))
            .With("$version", version)
            .With("$utc", DateTime.UtcNow.ToDb());
        command.ExecuteNonQuery();
    }

    /// <summary>Legt die Tabs des Zustandsberichts an; Tab 1 erhält die Anlagedaten-Tabelle.</summary>
    private static void AddTemplateSections(SqliteConnection connection, Guid templateId)
    {
        for (var index = 0; index < ReportSections.Length; index++)
        {
            using var insert = Db.Command(connection, """
                INSERT INTO TemplateSections
                    (Id, TemplateId, Title, DefaultHtml, SortOrder, CreatedUtc, CreatedBy, RowVersion)
                VALUES ($id, $templateId, $title, $defaultHtml, $sortOrder, $createdUtc, 'System', 1);
                """);
            insert.With("$id", Guid.NewGuid().ToString("D"))
                .With("$templateId", templateId.ToString("D"))
                .With("$title", ReportSections[index].Title)
                .With("$defaultHtml", ReportSections[index].DefaultHtml)
                .With("$sortOrder", index)
                .With("$createdUtc", DateTime.UtcNow.ToDb());
            insert.ExecuteNonQuery();
        }
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
            Company            TEXT NULL,
            Email              TEXT NULL,
            Phone              TEXT NULL,
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
            CustomerStreet TEXT NULL,
            CustomerLine2  TEXT NULL,
            CustomerZip    TEXT NULL,
            CustomerCity   TEXT NULL,
            Client         TEXT NULL,
            ClientStreet   TEXT NULL,
            ClientLine2    TEXT NULL,
            ClientZip      TEXT NULL,
            ClientCity     TEXT NULL,
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

        CREATE TABLE IF NOT EXISTS ProjectContacts (
            Id             TEXT PRIMARY KEY NOT NULL,
            ProjectId      TEXT NOT NULL REFERENCES Projects(Id),
            Name           TEXT NOT NULL,
            Email          TEXT NULL,
            Phone          TEXT NULL,
            Responsibility TEXT NULL,
            SortOrder      INTEGER NOT NULL DEFAULT 0,
            CreatedUtc     TEXT NOT NULL,
            CreatedBy      TEXT NULL,
            RowVersion     INTEGER NOT NULL DEFAULT 1,
            IsDeleted      INTEGER NOT NULL DEFAULT 0
        );

        -- Zeilen der projektbezogenen Tabellen; Data hält die Werte je Spaltenschlüssel.
        CREATE TABLE IF NOT EXISTS ProjectTableRows (
            Id         TEXT PRIMARY KEY NOT NULL,
            ProjectId  TEXT NOT NULL REFERENCES Projects(Id),
            TableKey   TEXT NOT NULL,
            Data       TEXT NOT NULL,
            SortOrder  INTEGER NOT NULL DEFAULT 0,
            CreatedUtc TEXT NOT NULL,
            CreatedBy  TEXT NULL,
            UpdatedUtc TEXT NOT NULL,
            UpdatedBy  TEXT NULL,
            RowVersion INTEGER NOT NULL DEFAULT 1,
            IsDeleted  INTEGER NOT NULL DEFAULT 0
        );

        CREATE INDEX IF NOT EXISTS IX_ProjectTableRows_Project ON ProjectTableRows (ProjectId, TableKey);

        CREATE TABLE IF NOT EXISTS ChangeLog (
            Id             INTEGER PRIMARY KEY AUTOINCREMENT,
            EntityType     TEXT NOT NULL,
            EntityId       TEXT NOT NULL,
            Operation      TEXT NOT NULL,
            Payload        TEXT NOT NULL,
            ChangedUtc     TEXT NOT NULL,
            OriginDeviceId TEXT NOT NULL,
            RowVersion     INTEGER NOT NULL,
            SyncedUtc      TEXT NULL,
            OriginUser     TEXT NULL
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

        CREATE TABLE IF NOT EXISTS TemplateSections (
            Id         TEXT PRIMARY KEY NOT NULL,
            TemplateId TEXT NOT NULL REFERENCES Templates(Id),
            Title       TEXT NOT NULL,
            DefaultHtml TEXT NOT NULL DEFAULT '',
            SortOrder   INTEGER NOT NULL DEFAULT 0,
            CreatedUtc  TEXT NOT NULL,
            CreatedBy   TEXT NULL,
            RowVersion  INTEGER NOT NULL DEFAULT 1
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
            SectionsJson    TEXT NULL,
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

        CREATE TABLE IF NOT EXISTS DocumentTypes (
            Id         TEXT PRIMARY KEY NOT NULL,
            Name       TEXT NOT NULL COLLATE NOCASE UNIQUE,
            SortOrder  INTEGER NOT NULL DEFAULT 0,
            IsArchived INTEGER NOT NULL DEFAULT 0,
            CreatedUtc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS Images (
            Id          TEXT PRIMARY KEY NOT NULL,
            Name        TEXT NOT NULL COLLATE NOCASE,
            Category    TEXT NULL,
            Description TEXT NULL,
            Tags        TEXT NULL,
            ProjectId   TEXT NULL REFERENCES Projects(Id),
            FileName    TEXT NOT NULL,
            FilePath    TEXT NOT NULL,
            ContentType TEXT NOT NULL,
            SizeBytes   INTEGER NOT NULL DEFAULT 0,
            IsArchived  INTEGER NOT NULL DEFAULT 0,
            CreatedUtc  TEXT NOT NULL,
            CreatedBy   TEXT NULL,
            ContentHash TEXT NULL,
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
        CREATE INDEX IF NOT EXISTS IX_TemplateSections_Template ON TemplateSections (TemplateId);
        CREATE INDEX IF NOT EXISTS IX_ProjectDocuments_Project ON ProjectDocuments (ProjectId);
        CREATE INDEX IF NOT EXISTS IX_ProjectContacts_Project ON ProjectContacts (ProjectId);

        INSERT INTO SchemaInfo (Version)
        SELECT 5 WHERE NOT EXISTS (SELECT 1 FROM SchemaInfo);
        """;
}
