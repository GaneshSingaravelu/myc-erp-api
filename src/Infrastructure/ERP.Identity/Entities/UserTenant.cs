using ERP.SharedKernel.Entities;

namespace ERP.Identity.Entities;

/// <summary>
/// USER-TENANT JUNCTION TABLE — Maps users to tenants (many-to-many).
///
/// A CEO/admin can access multiple tenants with different roles.
/// HasGlobalPlantAccess is per-tenant (CEO of Tenant A may not be CEO of Tenant B).
///
/// Example:
///   John → Tenant ABC (IsDefault=true, HasGlobalPlantAccess=true)
///   John → Tenant XYZ (IsDefault=false, HasGlobalPlantAccess=false)
/// </summary>
public class UserTenant : BaseEntity
{
    public Guid UserId { get; set; }
    public AppUser User { get; set; } = null!;

    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    /// <summary>Default tenant shown on login (user can switch)</summary>
    public bool IsDefault { get; set; }

    /// <summary>Can access ALL plants in this tenant (CEO/admin flag)</summary>
    public bool HasGlobalPlantAccess { get; set; }

    /// <summary>Disable tenant access without removing the record</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>When user was granted access to this tenant</summary>
    public DateTime JoinedAt { get; set; } = DateTime.UtcNow;
}
