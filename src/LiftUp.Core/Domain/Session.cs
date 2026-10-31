namespace LiftUp.Core.Domain;

/// <summary>Angemeldeter Benutzer inklusive seiner Rechte.</summary>
public sealed class Session
{
    public required Guid UserId { get; init; }
    public required string UserName { get; init; }
    public required string DisplayName { get; init; }
    public required Guid RoleId { get; init; }
    public required string RoleName { get; init; }
    public required Permission Permissions { get; init; }
    public required bool MustChangePassword { get; init; }
    public DateTime LoginUtc { get; init; } = DateTime.UtcNow;

    public static Session Create(User user, Role role) => new()
    {
        UserId = user.Id,
        UserName = user.UserName,
        DisplayName = user.DisplayName,
        RoleId = role.Id,
        RoleName = role.Name,
        Permissions = role.Permissions,
        MustChangePassword = user.MustChangePassword,
    };

    public bool Can(Permission permission) => (Permissions & permission) == permission;

    /// <summary>Wirft <see cref="PermissionDeniedException"/>, wenn ein Recht fehlt.</summary>
    public void Require(Permission permission)
    {
        if (!Can(permission))
        {
            throw new PermissionDeniedException(permission, RoleName);
        }
    }
}

public class LiftUpException(string message) : Exception(message);

public sealed class ValidationException(string message) : LiftUpException(message);

public sealed class PermissionDeniedException : LiftUpException
{
    public PermissionDeniedException(Permission missing, string? roleName = null)
        : base(string.IsNullOrWhiteSpace(roleName)
            ? $"Ihre Rolle erlaubt diese Aktion nicht. Fehlendes Recht: {PermissionCatalog.Describe(missing)}."
            : $"Ihre Rolle ({roleName}) erlaubt diese Aktion nicht. Fehlendes Recht: {PermissionCatalog.Describe(missing)}.")
    {
        MissingPermission = missing;
    }

    public Permission MissingPermission { get; }
}
