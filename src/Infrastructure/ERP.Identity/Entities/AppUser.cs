using ERP.SharedKernel.Entities;

namespace ERP.Identity.Entities;

/// <summary>
/// APPLICATION USER — Represents a user in the ERP system.
///
/// WHY NOT USE ASP.NET Core Identity's IdentityUser?
/// ===================================================
/// ASP.NET Core Identity is a full user management framework with its own tables,
/// password handling, and token management. It's great for general web apps.
///
/// For our ERP, we build our own because:
/// 1. We need multi-tenant users (each tenant has their own user table)
/// 2. We need ERP-specific fields (PlantId, EmployeeId, Department)
/// 3. We want full control over the JWT claims
/// 4. We use our own audit trail (AuditableEntity)
/// 5. Simpler to understand and teach to freshers
///
/// PASSWORD SECURITY:
/// ===================
/// We NEVER store passwords as plain text. We store a "hash":
///   Password: "MyP@ssw0rd"
///   Hash:     "$2a$11$rGJh2..." (BCrypt hash — cannot be reversed!)
///
/// When user logs in:
///   1. User sends password: "MyP@ssw0rd"
///   2. We hash it and compare with stored hash
///   3. Match → generate JWT token
///   4. No match → return 401 Unauthorized
///
/// BCrypt is used because it's intentionally SLOW (prevents brute force attacks).
/// </summary>
public class AppUser : AuditableEntity
{
    /// <summary>Unique username for login</summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>User's email address</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>BCrypt-hashed password (NEVER store plain text!)</summary>
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>User's first name</summary>
    public string FirstName { get; set; } = string.Empty;

    /// <summary>User's last name</summary>
    public string LastName { get; set; } = string.Empty;

    /// <summary>Full name for display</summary>
    public string FullName => $"{FirstName} {LastName}".Trim();

    /// <summary>Is this user account active? Inactive users cannot log in.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Last successful login time</summary>
    public DateTime? LastLoginAt { get; set; }

    /// <summary>The stored refresh token (for JWT refresh flow)</summary>
    public string? RefreshToken { get; set; }

    /// <summary>When the refresh token expires</summary>
    public DateTime? RefreshTokenExpiresAt { get; set; }

    /// <summary>Navigation property: roles assigned to this user (many-to-many)</summary>
    public ICollection<AppUserRole> UserRoles { get; set; } = [];
}
