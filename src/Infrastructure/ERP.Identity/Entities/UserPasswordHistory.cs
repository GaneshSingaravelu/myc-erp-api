using ERP.SharedKernel.Entities;

namespace ERP.Identity.Entities;

/// <summary>
/// PASSWORD HISTORY — Prevents users from reusing recent passwords.
///
/// On password change, the system checks the last 5 hashes.
/// If a match is found, the change is rejected with:
///   "Cannot reuse any of your last 5 passwords."
///
/// SECURITY COMPLIANCE:
/// =====================
/// Many security standards (SOC 2, ISO 27001) require password history enforcement.
/// </summary>
public class UserPasswordHistory : BaseEntity
{
    public Guid UserId { get; set; }
    public AppUser User { get; set; } = null!;

    /// <summary>BCrypt hash of the previous password</summary>
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>When this password was set</summary>
    public DateTime ChangedAt { get; set; } = DateTime.UtcNow;
}
