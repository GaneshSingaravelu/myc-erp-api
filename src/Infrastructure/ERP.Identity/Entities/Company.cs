using ERP.SharedKernel.Entities;

namespace ERP.Identity.Entities;

/// <summary>
/// COMPANY — The organisational grouping above Tenant (approved model: Company 1:N Tenant).
///
/// Company metadata lives in the Master DB alongside Tenant metadata. A Company owns no
/// business database of its own; each of its Tenants keeps its own database, and the Tenant
/// remains the security / data-isolation boundary.
///
/// Membership in a Company never implies access to its Tenants. Tenant access stays
/// explicitly assigned through UserTenants.
///
/// Code follows the same canonical contract as Tenant.Code — Trim() + ToLowerInvariant(),
/// enforced by the application, not by database collation. No Company write path exists yet;
/// the normalizer is introduced with the first one.
/// </summary>
public class Company : BaseEntity
{
    /// <summary>Canonical company code (trimmed, lower-case invariant). Unique. Max 50.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Display name. Max 200.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Soft-disable flag. Stored only — not yet enforced anywhere.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>When this company was created (UTC)</summary>
    public DateTime CreatedAt { get; set; }

    // ── Navigation Properties ──

    /// <summary>Tenants grouped under this company</summary>
    public ICollection<Tenant> Tenants { get; set; } = [];
}
