namespace ERP.Identity.Abstractions.Persistence;

/// <summary>
/// Read-only repository for feature-flag resolution (Master DB).
///
/// Feature flags have two-level resolution:
///   1. Global default (FeatureFlag.IsEnabled)
///   2. Per-tenant override (TenantFeatureFlag.IsEnabled)
///
/// MenuService merges the two dictionaries at runtime (tenant override wins).
/// Keeping the shape as Dictionary&lt;Code, IsEnabled&gt; isolates the service from
/// EF Core and from the underlying entity graph.
/// </summary>
public interface IFeatureFlagRepository
{
    /// <summary>
    /// Global defaults keyed by <c>FeatureFlag.Code</c>.
    /// Fallback when a tenant has no override for a flag.
    /// </summary>
    Task<IReadOnlyDictionary<string, bool>> GetGlobalFlagsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Per-tenant overrides keyed by <c>FeatureFlag.Code</c>. Empty when the tenant
    /// has no overrides (or when no tenant is in context).
    /// </summary>
    Task<IReadOnlyDictionary<string, bool>> GetTenantOverridesAsync(Guid tenantId, CancellationToken cancellationToken = default);
}
