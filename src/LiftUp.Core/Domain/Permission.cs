namespace LiftUp.Core.Domain;

/// <summary>
/// Einzelne Rechte. Werden als Bitmaske an Rollen gehalten, damit Rechte
/// ohne Schemaänderung erweitert werden können.
/// </summary>
[Flags]
public enum Permission
{
    None = 0,
    ViewProjects = 1 << 0,
    CreateProjects = 1 << 1,
    EditProjects = 1 << 2,
    DeleteProjects = 1 << 3,
    ManageUsers = 1 << 4,
    ManageRoles = 1 << 5,
    ArchiveProjects = 1 << 6,
    ViewArchivedProjects = 1 << 7,
    ManageTemplates = 1 << 8,
    ManageSnippets = 1 << 9,
    ManageImages = 1 << 10,
    CreateDocuments = 1 << 11,
    DeleteDocuments = 1 << 12,
    ChangeSettings = 1 << 13,
    RunSync = 1 << 14,
    ViewChangeLog = 1 << 15,

    All = ViewProjects | CreateProjects | EditProjects | DeleteProjects | ManageUsers | ManageRoles |
          ArchiveProjects | ViewArchivedProjects | ManageTemplates | ManageSnippets | ManageImages |
          CreateDocuments | DeleteDocuments | ChangeSettings | RunSync | ViewChangeLog,
}

public sealed record PermissionInfo(Permission Value, string Name, string Description)
{
    public string CheckboxText => $"{Name} – {Description}";
}

public static class PermissionCatalog
{
    public static IReadOnlyList<PermissionInfo> All { get; } =
    [
        new(Permission.ViewProjects, "Projekte ansehen", "Projektliste, Suche und Details öffnen"),
        new(Permission.CreateProjects, "Projekte anlegen", "Neue Projekte erfassen und Projektordner anlegen"),
        new(Permission.EditProjects, "Projekte bearbeiten", "Bestehende Projekte ändern"),
        new(Permission.DeleteProjects, "Projekte löschen", "Projekte entfernen, Ordner optional in den Papierkorb"),
        new(Permission.ArchiveProjects, "Projekte archivieren", "Projekte archivieren und wiederherstellen"),
        new(Permission.ViewArchivedProjects, "Archiv ansehen", "Archivierte Projekte einsehen"),
        new(Permission.CreateDocuments, "PDFs erstellen", "Dokumente aus Vorlagen erzeugen und ablegen"),
        new(Permission.DeleteDocuments, "PDFs löschen", "Erzeugte Dokumente entfernen"),
        new(Permission.ManageTemplates, "Vorlagen verwalten", "HTML-Vorlagen anlegen, ändern, versionieren"),
        new(Permission.ManageSnippets, "Textbausteine verwalten", "Textbausteine anlegen und ändern"),
        new(Permission.ManageImages, "Bilder verwalten", "Bildbibliothek und Projektbilder pflegen"),
        new(Permission.ManageUsers, "Benutzer verwalten", "Benutzer anlegen, ändern, Passwort zurücksetzen"),
        new(Permission.ManageRoles, "Rollen und Rechte", "Rollen anlegen und Rechte verteilen"),
        new(Permission.ChangeSettings, "Einstellungen ändern", "Systemweite Einstellungen bearbeiten"),
        new(Permission.RunSync, "Synchronisierung ausführen", "Abgleich mit der zentralen Datenbank starten"),
        new(Permission.ViewChangeLog, "Änderungsprotokoll ansehen", "Protokolleinträge einsehen"),
    ];

    /// <summary>Rechte als lesbare Liste, z. B. für die Übersicht in der Rollenverwaltung.</summary>
    public static string Describe(Permission permissions)
    {
        if (permissions == Permission.None)
        {
            return "keine Rechte";
        }

        var active = All.Where(info => permissions.HasFlag(info.Value)).Select(info => info.Name).ToArray();
        return active.Length == 0 ? "keine Rechte" : string.Join(", ", active);
    }
}
