using ERP.Identity.Abstractions.Persistence;
using ERP.Identity.Entities;
using ERP.Persistence.Context;
using Microsoft.EntityFrameworkCore;

namespace ERP.Identity.Persistence;

/// <summary>
/// EF Core implementation of <see cref="IFeatureFlagRepository"/> over MasterDbContext.
/// Returns flags pre-shaped as Code → IsEnabled dictionaries so consumers (MenuService)
/// never touch entity types or EF Core.
/// </summary>
public sealed class FeatureFlagRepository : IFeatureFlagRepository
{
    private readonly MasterDbContext _context;

    public FeatureFlagRepository(MasterDbContext context) => _context = context;

    public async Task<IReadOnlyDictionary<string, bool>> GetGlobalFlagsAsync(CancellationToken cancellationToken = default)
    {
        var flags = await _context.Set<FeatureFlag>()
            .ToDictionaryAsync(f => f.Code, f => f.IsEnabled, cancellationToken);

        return flags;
    }

    public async Task<IReadOnlyDictionary<string, bool>> GetTenantOverridesAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var overrides = await _context.Set<TenantFeatureFlag>()
            .Where(tf => tf.TenantId == tenantId)
            .ToDictionaryAsync(tf => tf.FeatureFlag.Code, tf => tf.IsEnabled, cancellationToken);

        return overrides;
    }
}
