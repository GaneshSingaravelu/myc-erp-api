using ERP.Identity.Entities;

namespace ERP.Identity.Abstractions.Persistence;

/// <summary>
/// Repository for Tenant (Master DB).
///
/// Exposes both the read-only lookups used by AuthService and the CRUD surface used
/// by TenantManagementService. Keeping both in one repository preserves the single
/// MasterDbContext per-request pattern — repositories stage changes, the service
/// calls IMasterUnitOfWork.SaveChangesAsync() once to commit.
/// </summary>
public interface ITenantRepository
{
    /// <summary>Find by GUID; returns null if not found.</summary>
    Task<Tenant?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Find by short Code (e.g., "ABC"); returns null if not found.</summary>
    Task<Tenant?> GetByCodeAsync(string code, CancellationToken cancellationToken = default);

    /// <summary>True if a tenant with this id exists.</summary>
    Task<bool> ExistsByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>True if any tenant already uses this code (uniqueness check for create).</summary>
    Task<bool> ExistsByCodeAsync(string code, CancellationToken cancellationToken = default);

    /// <summary>
    /// Find by id with the UserTenants collection eager-loaded. Used by the admin
    /// read/update flow to project a user-count into TenantResponse.
    /// </summary>
    Task<Tenant?> GetByIdWithUserTenantsAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>List all tenants ordered by Name (no related entities loaded).</summary>
    Task<IReadOnlyList<Tenant>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Stage a new tenant for insertion. Persisted on next IMasterUnitOfWork.SaveChangesAsync().</summary>
    Task AddAsync(Tenant tenant, CancellationToken cancellationToken = default);
}
