namespace LiftUp.Core.Domain;

/// <summary>Angemeldeter Benutzer inklusive seiner Rechte.</summary>
public sealed class Session
{
    public required Guid UserId { get; init; }
    public required string UserName { get; init; }
    public required string DisplayName { get; init; }

    /// <summary>Absenderdaten aus dem Benutzerprofil für Titelseiten.</summary>
    public string Company { get; init; } = string.Empty;
    public string CompanyStreet { get; init; } = string.Empty;
    public string CompanyZip { get; init; } = string.Empty;
    public string CompanyCity { get; init; } = string.Empty;
    public string CompanyLogo { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public string Phone { get; init; } = string.Empty;

    public required Guid RoleId { get; init; }
    public required string RoleName { get; init; }
    public required Permission Permissions { get; init; }
    public required bool MustChangePassword { get; init; }
    public DateTime LoginUtc { get; init; } = DateTime.UtcNow;

    /// <summary>Notfall-Sitzung nach erfolgreicher Prüfung eines MasterKey-Lizenzcodes.</summary>
    public bool IsEmergency { get; init; }

    /// <summary>Zeitpunkt, zu dem die Notfall-Sitzung ohne Bedienung endet.</summary>
    public DateTime? EmergencyExpiresUtc { get; init; }

    /// <summary>Name im Änderungsprotokoll; Notfallzugriffe sind dort auf einen Blick erkennbar.</summary>
    public string ActorName => IsEmergency ? $"{UserName} (Notfallzugang)" : UserName;

    public static Session Create(User user, Role role) => new()
    {
        UserId = user.Id,
        UserName = user.UserName,
        DisplayName = user.DisplayName,
        Company = user.Company ?? string.Empty,
        CompanyStreet = user.CompanyStreet ?? string.Empty,
        CompanyZip = user.CompanyZip ?? string.Empty,
        CompanyCity = user.CompanyCity ?? string.Empty,
        CompanyLogo = user.CompanyLogo ?? string.Empty,
        Email = user.Email ?? string.Empty,
        Phone = user.Phone ?? string.Empty,
        RoleId = role.Id,
        RoleName = role.Name,
        Permissions = role.Permissions,
        MustChangePassword = user.MustChangePassword,
    };

    /// <summary>
    /// Sitzung des Notfallzugangs: alle Rechte, aber ausschliesslich für die Wiederherstellung
    /// regulärer Benutzerzugänge gedacht. Grundlage ist das Konto mit den meisten Rechten.
    /// </summary>
    public static Session CreateEmergency(User user, DateTime expiresUtc) => new()
    {
        UserId = user.Id,
        UserName = user.UserName,
        DisplayName = user.DisplayName,
        Company = user.Company ?? string.Empty,
        CompanyStreet = user.CompanyStreet ?? string.Empty,
        CompanyZip = user.CompanyZip ?? string.Empty,
        CompanyCity = user.CompanyCity ?? string.Empty,
        CompanyLogo = user.CompanyLogo ?? string.Empty,
        Email = user.Email ?? string.Empty,
        Phone = user.Phone ?? string.Empty,
        RoleId = user.RoleId,
        RoleName = "Notfall-Administrator (MasterKey)",
        Permissions = Permission.All,
        MustChangePassword = false,
        IsEmergency = true,
        EmergencyExpiresUtc = expiresUtc,
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
