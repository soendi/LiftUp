-- LiftUp – Schema der zentralen PostgreSQL-Datenbank
--
-- Wird von installer/Install-ServerDatabase.ps1 eingespielt, lässt sich aber auch
-- von Hand ausführen:
--
--   psql -v ON_ERROR_STOP=1 -v schema=liftup -v owner=liftup -f installer/sql/liftup-schema.sql
--
-- Ausführen als Superuser (postgres) oder als Eigentümer der Datenbank. Das Skript
-- ist idempotent: mehrfaches Ausführen ändert nichts und legt nichts doppelt an.
--
-- Abbildung gegenüber der lokalen SQLite-Datenbank (src/LiftUp.Core/Data/Database.cs):
--
--   * Tabellen, Spalten und Indizes heißen genau wie lokal, deshalb sind alle
--     Bezeichner in Anführungszeichen gesetzt (PostgreSQL würde sie sonst klein
--     schreiben und der spätere Abgleich müsste zwei Namen kennen).
--   * GUIDs, Zeitstempel und Nummern bleiben TEXT. Der Abgleich kann die Werte
--     damit unverändert übernehmen: UTC-Zeitstempel im ISO-8601-Format "O",
--     GUIDs im Format "D".
--   * Zähler und Flags bleiben INTEGER (0/1), wie in SQLite gespeichert.
--   * Die Groß-/Kleinschreibung von SQLite (COLLATE NOCASE) entsteht über
--     eindeutige Indizes auf lower(...).
--
-- Schemastand: 13 – muss zu Database.CurrentSchemaVersion passen. Der Installer
-- liest die Version nach dem Einspielen aus dieser Tabelle und schreibt sie in
-- die Verbindungsdatei; die Anwendung vergleicht sie beim Einlesen.

\set ON_ERROR_STOP on

CREATE SCHEMA IF NOT EXISTS :"schema" AUTHORIZATION :"owner";

SET ROLE :"owner";
SET search_path TO :"schema";

-- --------------------------------------------------------------------------
-- Stammdaten
-- --------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS "SchemaInfo" (
    "Version" integer NOT NULL
);

CREATE TABLE IF NOT EXISTS "Settings" (
    "Key"   text PRIMARY KEY,
    "Value" text NOT NULL
);

CREATE TABLE IF NOT EXISTS "Roles" (
    "Id"          text PRIMARY KEY,
    "Name"        text NOT NULL,
    "Description" text NOT NULL DEFAULT '',
    "Permissions" integer NOT NULL DEFAULT 0,
    "IsSystem"    integer NOT NULL DEFAULT 0,
    "CreatedUtc"  text NOT NULL,
    "RowVersion"  integer NOT NULL DEFAULT 1
);

CREATE TABLE IF NOT EXISTS "Users" (
    "Id"                 text PRIMARY KEY,
    "UserName"           text NOT NULL,
    "DisplayName"        text NOT NULL DEFAULT '',
    "Company"            text,
    "Email"              text,
    "Phone"              text,
    "RoleId"             text NOT NULL REFERENCES "Roles" ("Id"),
    "IsActive"           integer NOT NULL DEFAULT 1,
    "PasswordHash"       text NOT NULL,
    "PasswordSalt"       text NOT NULL,
    "PasswordIterations" integer NOT NULL,
    "MustChangePassword" integer NOT NULL DEFAULT 0,
    "CreatedUtc"         text NOT NULL,
    "CreatedBy"          text,
    "LastLoginUtc"       text,
    "RowVersion"         integer NOT NULL DEFAULT 1,
    "IsDeleted"          integer NOT NULL DEFAULT 0
);

-- --------------------------------------------------------------------------
-- Projekte
-- --------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS "Projects" (
    "Id"                text PRIMARY KEY,
    "Number"            text,
    "Name"              text NOT NULL,
    "Customer"          text,
    "CustomerStreet"    text,
    "CustomerZip"       text,
    "CustomerCity"      text,
    "Contact"           text,
    "Description"       text,
    "ResponsibleUserId" text,
    "ResponsibleName"   text,
    "FolderPath"        text NOT NULL,
    "Status"            integer NOT NULL DEFAULT 0,
    "ArchivedUtc"       text,
    "ArchivedBy"        text,
    "CreatedUtc"        text NOT NULL,
    "CreatedBy"         text,
    "UpdatedUtc"        text NOT NULL,
    "UpdatedBy"         text,
    "RowVersion"        integer NOT NULL DEFAULT 1,
    "IsDeleted"         integer NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS "ProjectContacts" (
    "Id"             text PRIMARY KEY,
    "ProjectId"      text NOT NULL REFERENCES "Projects" ("Id"),
    "Name"           text NOT NULL,
    "Email"          text,
    "Phone"          text,
    "Responsibility" text,
    "SortOrder"      integer NOT NULL DEFAULT 0,
    "CreatedUtc"     text NOT NULL,
    "CreatedBy"      text,
    "RowVersion"     integer NOT NULL DEFAULT 1,
    "IsDeleted"      integer NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS "ProjectNumberSequences" (
    "Year"       integer PRIMARY KEY,
    "LastNumber" integer NOT NULL DEFAULT 0
);

-- --------------------------------------------------------------------------
-- Dokumente, Vorlagen, Bausteine
-- --------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS "Templates" (
    "Id"             text PRIMARY KEY,
    "Name"           text NOT NULL,
    "DocumentType"   text NOT NULL DEFAULT '',
    "CurrentVersion" integer NOT NULL DEFAULT 0,
    "IsActive"       integer NOT NULL DEFAULT 1,
    "CreatedUtc"     text NOT NULL,
    "CreatedBy"      text,
    "UpdatedUtc"     text NOT NULL,
    "UpdatedBy"      text,
    "RowVersion"     integer NOT NULL DEFAULT 1,
    "IsDeleted"      integer NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS "TemplateVersions" (
    "Id"         text PRIMARY KEY,
    "TemplateId" text NOT NULL REFERENCES "Templates" ("Id"),
    "Version"    integer NOT NULL,
    "Html"       text NOT NULL,
    "CreatedUtc" text NOT NULL,
    "CreatedBy"  text NOT NULL,
    UNIQUE ("TemplateId", "Version")
);

CREATE TABLE IF NOT EXISTS "TemplateSections" (
    "Id"         text PRIMARY KEY,
    "TemplateId" text NOT NULL REFERENCES "Templates" ("Id"),
    "Title"      text NOT NULL,
    "SortOrder"  integer NOT NULL DEFAULT 0,
    "CreatedUtc" text NOT NULL,
    "CreatedBy"  text,
    "RowVersion" integer NOT NULL DEFAULT 1
);

CREATE TABLE IF NOT EXISTS "ProjectDocuments" (
    "Id"              text PRIMARY KEY,
    "ProjectId"       text NOT NULL REFERENCES "Projects" ("Id"),
    "TemplateId"      text NOT NULL,
    "TemplateName"    text NOT NULL,
    "TemplateVersion" integer NOT NULL,
    "FileName"        text NOT NULL,
    "FilePath"        text NOT NULL,
    "SizeBytes"       bigint NOT NULL DEFAULT 0,
    "CreatedUtc"      text NOT NULL,
    "CreatedBy"       text NOT NULL,
    "SectionsJson"    text,
    "RowVersion"      integer NOT NULL DEFAULT 1,
    "IsDeleted"       integer NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS "TextSnippets" (
    "Id"         text PRIMARY KEY,
    "Name"       text NOT NULL,
    "Category"   text,
    "Html"       text NOT NULL,
    "IsArchived" integer NOT NULL DEFAULT 0,
    "Version"    integer NOT NULL DEFAULT 1,
    "CreatedUtc" text NOT NULL,
    "CreatedBy"  text,
    "UpdatedUtc" text NOT NULL,
    "UpdatedBy"  text,
    "RowVersion" integer NOT NULL DEFAULT 1
);

CREATE TABLE IF NOT EXISTS "DocumentTypes" (
    "Id"         text PRIMARY KEY,
    "Name"       text NOT NULL,
    "SortOrder"  integer NOT NULL DEFAULT 0,
    "IsArchived" integer NOT NULL DEFAULT 0,
    "CreatedUtc" text NOT NULL
);

CREATE TABLE IF NOT EXISTS "Images" (
    "Id"          text PRIMARY KEY,
    "Name"        text NOT NULL,
    "Category"    text,
    "Description" text,
    "Tags"        text,
    "ProjectId"   text REFERENCES "Projects" ("Id"),
    "FileName"    text NOT NULL,
    "FilePath"    text NOT NULL,
    "ContentType" text NOT NULL,
    "SizeBytes"   bigint NOT NULL DEFAULT 0,
    "IsArchived"  integer NOT NULL DEFAULT 0,
    "CreatedUtc"  text NOT NULL,
    "CreatedBy"   text,
    "ContentHash" text,
    "RowVersion"  integer NOT NULL DEFAULT 1
);

-- --------------------------------------------------------------------------
-- Änderungsprotokoll (dient gleichzeitig als Outbox für den Abgleich)
-- --------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS "ChangeLog" (
    "Id"             bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
    "EntityType"     text NOT NULL,
    "EntityId"       text NOT NULL,
    "Operation"      text NOT NULL,
    "Payload"        text NOT NULL,
    "ChangedUtc"     text NOT NULL,
    "OriginDeviceId" text NOT NULL,
    "RowVersion"     integer NOT NULL,
    "SyncedUtc"      text,
    "OriginUser"     text,
    -- Nummer des Eintrags in der lokalen Outbox des sendenden Arbeitsplatzes.
    -- Zusammen mit "OriginDeviceId" verhindert sie Doppelübertragungen.
    "OriginEntryId"  bigint,
    "ReceivedUtc"    text
);

-- Für Installationen, die vor den Abgleichsspalten entstanden sind.
ALTER TABLE "ChangeLog" ADD COLUMN IF NOT EXISTS "OriginEntryId" bigint;
ALTER TABLE "ChangeLog" ADD COLUMN IF NOT EXISTS "ReceivedUtc" text;

-- --------------------------------------------------------------------------
-- Eindeutigkeit (COLLATE NOCASE aus SQLite wird zu lower(...))
-- --------------------------------------------------------------------------

CREATE UNIQUE INDEX IF NOT EXISTS "UX_Roles_Name" ON "Roles" (lower("Name"));
CREATE UNIQUE INDEX IF NOT EXISTS "UX_Users_UserName" ON "Users" (lower("UserName"));
CREATE UNIQUE INDEX IF NOT EXISTS "UX_Templates_Name" ON "Templates" (lower("Name"));
CREATE UNIQUE INDEX IF NOT EXISTS "UX_TextSnippets_Name" ON "TextSnippets" (lower("Name"));
CREATE UNIQUE INDEX IF NOT EXISTS "UX_DocumentTypes_Name" ON "DocumentTypes" (lower("Name"));

CREATE UNIQUE INDEX IF NOT EXISTS "UX_Images_GlobalName"
    ON "Images" (lower("Name")) WHERE "ProjectId" IS NULL;

CREATE UNIQUE INDEX IF NOT EXISTS "UX_Images_ProjectName"
    ON "Images" ("ProjectId", lower("Name")) WHERE "ProjectId" IS NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS "UX_Projects_Number"
    ON "Projects" (lower("Number")) WHERE "Number" IS NOT NULL AND "IsDeleted" = 0;

CREATE UNIQUE INDEX IF NOT EXISTS "UX_Projects_Folder"
    ON "Projects" (lower("FolderPath")) WHERE "IsDeleted" = 0;

CREATE INDEX IF NOT EXISTS "IX_Users_RoleId" ON "Users" ("RoleId");
CREATE INDEX IF NOT EXISTS "IX_Projects_Status" ON "Projects" ("Status");
CREATE INDEX IF NOT EXISTS "IX_ChangeLog_Pending" ON "ChangeLog" ("SyncedUtc") WHERE "SyncedUtc" IS NULL;
CREATE UNIQUE INDEX IF NOT EXISTS "UX_ChangeLog_Origin"
    ON "ChangeLog" ("OriginDeviceId", "OriginEntryId");
CREATE INDEX IF NOT EXISTS "IX_TemplateSections_Template" ON "TemplateSections" ("TemplateId");
CREATE INDEX IF NOT EXISTS "IX_ProjectDocuments_Project" ON "ProjectDocuments" ("ProjectId");
CREATE INDEX IF NOT EXISTS "IX_ProjectContacts_Project" ON "ProjectContacts" ("ProjectId");

-- --------------------------------------------------------------------------
-- Schemastand (13 = Database.CurrentSchemaVersion in LiftUp.Core)
-- --------------------------------------------------------------------------

INSERT INTO "SchemaInfo" ("Version")
SELECT 13
WHERE NOT EXISTS (SELECT 1 FROM "SchemaInfo");

RESET ROLE;
