using ERP.SharedKernel.Entities;

namespace ERP.Production.Domain.Entities;

/// <summary>
/// PLANT — Physical manufacturing plant/location belonging to a tenant.
///
/// Stored in the Tenant DB (each tenant has their own plants).
/// No TenantId column needed — the table is already in a tenant-specific database.
///
/// ROLE vs PLANT (Critical Architecture Rule):
/// =============================================
///   Role = WHAT a user can do (Supervisor, Operator, QA Inspector)
///   Plant = WHERE a user can do it (Chennai, Bangalore, Mumbai)
///
///   Same role, different plants:
///     Worker + Chennai → can work only in Chennai
///     Manager + Chennai + Bangalore → can see both plants
///     CEO + HasGlobalPlantAccess → can see all plants
/// </summary>
public class Plant : BaseEntity
{
    /// <summary>Plant code (e.g., "CHN", "BLR", "MUM"). Max 50. Unique per tenant DB.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Display name (e.g., "Chennai Plant"). Max 200.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Address or city. Max 200.</summary>
    public string? Location { get; set; }

    /// <summary>Active/inactive plant</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>When this plant was created</summary>
    public DateTime CreatedAt { get; set; }

    // ── Navigation Properties ──

    /// <summary>Users who have access to this plant</summary>
    public ICollection<UserPlantAccess> UserPlantAccess { get; set; } = [];
}
