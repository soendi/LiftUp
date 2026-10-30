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
    All = ViewProjects | CreateProjects | EditProjects | DeleteProjects | ManageUsers | ManageRoles,
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
        new(Permission.ManageUsers, "Benutzer verwalten", "Benutzer anlegen, ändern, Passwort zurücksetzen"),
        new(Permission.ManageRoles, "Rollen und Rechte", "Rollen anlegen und Rechte verteilen"),
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
