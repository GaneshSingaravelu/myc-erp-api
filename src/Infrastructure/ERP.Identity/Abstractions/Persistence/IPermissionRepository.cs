using ERP.Identity.Entities;

namespace ERP.Identity.Abstractions.Persistence;

/// <summary>
/// Read-only repository for Permission catalog (Master DB).
/// Permissions are seeded at startup and rarely change at runtime — only queries are exposed.
/// </summary>
public interface IPermissionRepository
{
    /// <summary>List all permissions, ordered by Category then Code (for admin permission-picker UIs).</summary>
    Task<IReadOnlyList<Permission>> GetAllOrderedAsync(CancellationToken cancellationToken = default);

    /// <summary>Check whether a permission with the given id exists.</summary>
    Task<bool> ExistsByIdAsync(Guid id, CancellationToken cancellationToken = default);
}
