using ERP.SharedKernel.Entities;

namespace ERP.Identity.Entities;

/// <summary>
/// FEATURE FLAG — Controls feature availability globally.
///
/// Feature flags allow rolling out features incrementally:
///   1. Deploy code with the feature behind a flag (IsEnabled = false)
///   2. Enable for specific tenants via TenantFeatureFlags
///   3. Once stable, enable globally (IsEnabled = true)
///
/// RESOLUTION ORDER:
/// ==================
///   1. Check TenantFeatureFlags for this tenant + flag → use if exists
///   2. Fall back to FeatureFlags.IsEnabled → global default
///
/// Used by Menus.FeatureFlag column to control menu visibility per tenant.
/// </summary>
public class FeatureFlag : BaseEntity
{
    /// <summary>Unique code (e.g., "PRODUCTION_V2", "QA_DASHBOARD"). Max 100.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Human-readable name. Max 200.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>What this flag controls. Max 500.</summary>
    public string? Description { get; set; }

    /// <summary>Global default (overridden per tenant via TenantFeatureFlags)</summary>
    public bool IsEnabled { get; set; }

    /// <summary>When this flag was created</summary>
    public DateTime CreatedAt { get; set; }

    // ── Navigation Properties ──

    /// <summary>Per-tenant overrides</summary>
    public ICollection<TenantFeatureFlag> TenantOverrides { get; set; } = [];
}
