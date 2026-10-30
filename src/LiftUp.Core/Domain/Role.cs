namespace LiftUp.Core.Domain;

public sealed class Role
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public Permission Permissions { get; set; } = Permission.None;

    /// <summary>Systemrollen (Administrator, Projektleiter, Betrachter) können nicht gelöscht werden.</summary>
    public bool IsSystem { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public long RowVersion { get; set; } = 1;

    public string PermissionSummary => PermissionCatalog.Describe(Permissions);

    public bool Has(Permission permission) => (Permissions & permission) == permission;
}
