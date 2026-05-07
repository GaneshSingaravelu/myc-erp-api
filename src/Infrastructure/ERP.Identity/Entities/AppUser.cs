using ERP.SharedKernel.Entities;

namespace ERP.Identity.Entities;

/// <summary>
/// APPLICATION USER — Global identity record stored in the Master DB.
///
/// A user is a PERSON, not a tenant-scoped record. The same user can access
/// multiple tenants via the UserTenants junction table with different roles per tenant.
///
/// KEY DESIGN DECISIONS:
/// =====================
/// - NormalizedUserName/Email: Uppercase copies for case-insensitive lookups without collation issues
/// - SecurityStamp: Random GUID that changes on password/security changes; invalidates all sessions
/// - ConcurrencyStamp: Optimistic concurrency; prevents simultaneous edits overwriting each other
/// - Account lockout: 5 failed attempts → 15 min lock (IsLocked, LockoutEnd, AccessFailedCount)
/// - No TenantId on Users: tenant relationship is many-to-many via UserTenants
///
/// PASSWORD SECURITY:
/// ===================
/// BCrypt hashing (intentionally slow to prevent brute force).
/// Password history tracked in UserPasswordHistory table (last 5 hashes).
/// </summary>
public class AppUser : AuditableEntity
{
    /// <summary>Unique username for login (max 100)</summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>Uppercase copy for case-insensitive lookup (auto-computed)</summary>
    public string NormalizedUserName { get; set; } = string.Empty;

    /// <summary>User's email address (max 200)</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>Uppercase copy for case-insensitive lookup (auto-computed)</summary>
    public string NormalizedEmail { get; set; } = string.Empty;

    /// <summary>BCrypt-hashed password (NEVER store plain text!)</summary>
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>
    /// Random GUID that changes on password/security changes.
    /// Used to invalidate all existing tokens when security state changes.
    /// </summary>
    public string SecurityStamp { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Optimistic concurrency stamp. EF Core checks this on update to prevent
    /// simultaneous edits from overwriting each other.
    /// </summary>
    public string ConcurrencyStamp { get; set; } = Guid.NewGuid().ToString();

    /// <summary>User's first name (max 100)</summary>
    public string FirstName { get; set; } = string.Empty;

    /// <summary>User's last name (max 100)</summary>
    public string LastName { get; set; } = string.Empty;

    /// <summary>Optional display name (falls back to FirstName + LastName if null)</summary>
    public string? DisplayName { get; set; }

    /// <summary>Full name for display (computed, not stored)</summary>
    public string FullName => DisplayName ?? $"{FirstName} {LastName}".Trim();

    /// <summary>Phone number (max 20, optional)</summary>
    public string? PhoneNumber { get; set; }

    /// <summary>Is the account locked due to failed login attempts?</summary>
    public bool IsLocked { get; set; }

    /// <summary>When the lockout expires (NULL = not locked). Auto-unlocks after this time.</summary>
    public DateTime? LockoutEnd { get; set; }

    /// <summary>Consecutive failed login attempts. Reset to 0 on successful login.</summary>
    public int AccessFailedCount { get; set; }

    /// <summary>Last successful login time (UTC)</summary>
    public DateTime? LastLoginAt { get; set; }

    /// <summary>When the password was last changed (UTC)</summary>
    public DateTime? LastPasswordChangedAt { get; set; }

    /// <summary>Force password change on next login (e.g., after admin reset)</summary>
    public bool MustChangePassword { get; set; }

    /// <summary>SHA-256 hash of the password reset token (NULL when no active reset).</summary>
    public string? PasswordResetTokenHash { get; set; }

    /// <summary>When the password reset token expires (UTC). NULL when no active reset.</summary>
    public DateTime? PasswordResetTokenExpiry { get; set; }

    // ── Navigation Properties ──

    /// <summary>Tenant memberships for this user (many-to-many via UserTenants)</summary>
    public ICollection<UserTenant> UserTenants { get; set; } = [];

    /// <summary>Role assignments per tenant (many-to-many via UserRoles)</summary>
    public ICollection<AppUserRole> UserRoles { get; set; } = [];

    /// <summary>Active login sessions across devices and tenants</summary>
    public ICollection<UserSession> Sessions { get; set; } = [];

    /// <summary>Password history for preventing reuse</summary>
    public ICollection<UserPasswordHistory> PasswordHistory { get; set; } = [];
}
