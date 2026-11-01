namespace LiftUp.Core.Domain;

public sealed class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Eigene Firma für Titelseiten.</summary>
    public string? Company { get; set; }

    /// <summary>Straße und Hausnummer der eigenen Firma.</summary>
    public string? CompanyStreet { get; set; }

    /// <summary>Postleitzahl der eigenen Firma.</summary>
    public string? CompanyZip { get; set; }

    /// <summary>Ort der eigenen Firma.</summary>
    public string? CompanyCity { get; set; }

    /// <summary>Firmenlogo für Titelseiten; liegt im Logoordner der Anwendung.</summary>
    public string? CompanyLogo { get; set; }

    /// <summary>Eigene E-Mail-Adresse für Titelseiten.</summary>
    public string? Email { get; set; }

    /// <summary>Eigene Telefonnummer für Titelseiten.</summary>
    public string? Phone { get; set; }

    public Guid RoleId { get; set; }
    public bool IsActive { get; set; } = true;

    public string PasswordHash { get; set; } = string.Empty;
    public string PasswordSalt { get; set; } = string.Empty;
    public int PasswordIterations { get; set; }
    public bool MustChangePassword { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string? CreatedBy { get; set; }
    public DateTime? LastLoginUtc { get; set; }

    public long RowVersion { get; set; } = 1;
    public bool IsDeleted { get; set; }
}

/// <summary>Anzeigeobjekt für die Benutzerliste.</summary>
public sealed class UserListItem
{
    public Guid Id { get; init; }
    public string UserName { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Company { get; init; } = string.Empty;
    public string CompanyStreet { get; init; } = string.Empty;
    public string CompanyZip { get; init; } = string.Empty;
    public string CompanyCity { get; init; } = string.Empty;
    public string CompanyLogo { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public string Phone { get; init; } = string.Empty;
    public string RoleName { get; init; } = string.Empty;
    public bool IsActive { get; init; }
    public string StatusText => IsActive ? "Aktiv" : "Deaktiviert";
    public string LastLoginText { get; init; } = "–";
    public string PermissionSummary { get; init; } = string.Empty;
}
