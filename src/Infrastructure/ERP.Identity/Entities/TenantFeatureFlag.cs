using ERP.SharedKernel.Entities;

namespace ERP.Identity.Entities;

/// <summary>
/// TENANT FEATURE FLAG — Per-tenant override for feature flags.
///
/// Example: "PRODUCTION_V2" is globally disabled, but enabled for Tenant ABC
/// because they are beta-testing the new production module.
/// </summary>
public class TenantFeatureFlag : BaseEntity
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    public Guid FeatureFlagId { get; set; }
    public FeatureFlag FeatureFlag { get; set; } = null!;

    /// <summary>Tenant-level override (overrides FeatureFlags.IsEnabled)</summary>
    public bool IsEnabled { get; set; }

    /// <summary>When this override was set</summary>
    public DateTime EnabledAt { get; set; } = DateTime.UtcNow;

    /// <summary>Who enabled/disabled this flag for the tenant</summary>
    public Guid? EnabledBy { get; set; }
}
