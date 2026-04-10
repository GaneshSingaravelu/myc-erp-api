using ERP.SharedKernel.Entities;

namespace ERP.Identity.Entities;

/// <summary>
/// TENANT — Represents a client organization in the ERP system.
///
/// Each tenant gets their own business database (ERP_{Code}).
/// Tenant metadata is stored in the Master DB.
///
/// The Master DB → Tenant DB flow:
///   1. Request arrives with X-Tenant-Id header (e.g., "ABC")
///   2. TenantMiddleware looks up this table → gets ConnectionString
///   3. TenantDbContextFactory creates AppDbContext pointing to ERP_ABC
///   4. All business queries hit the tenant-specific database
/// </summary>
public class Tenant : BaseEntity
{
    /// <summary>Short code sent in X-Tenant-Id header (e.g., "ABC", "XYZ"). Max 50.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Display name (e.g., "ABC Manufacturing Ltd"). Max 200.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Connection string to this tenant's business database. Encrypted at rest in production.</summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>Database provider: "SqlServer" or "PostgreSql"</summary>
    public string DbProvider { get; set; } = "SqlServer";

    /// <summary>Disabled tenants cannot log in</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>When this tenant was provisioned (UTC)</summary>
    public DateTime CreatedAt { get; set; }

    // ── Navigation Properties ──

    /// <summary>Users who have access to this tenant</summary>
    public ICollection<UserTenant> UserTenants { get; set; } = [];

    /// <summary>Roles specific to this tenant</summary>
    public ICollection<AppRole> Roles { get; set; } = [];

    /// <summary>Role assignments within this tenant</summary>
    public ICollection<AppUserRole> UserRoles { get; set; } = [];

    /// <summary>Feature flag overrides for this tenant</summary>
    public ICollection<TenantFeatureFlag> FeatureFlags { get; set; } = [];

    /// <summary>Sessions created within this tenant context</summary>
    public ICollection<UserSession> Sessions { get; set; } = [];
}
