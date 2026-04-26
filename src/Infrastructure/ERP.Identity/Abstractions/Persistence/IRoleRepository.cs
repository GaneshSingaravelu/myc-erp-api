using ERP.Identity.Entities;

namespace ERP.Identity.Abstractions.Persistence;

/// <summary>
/// Repository for AppRole aggregate (Master DB).
///
/// Responsibilities:
///   - Load roles (with / without the Permissions graph) for admin CRUD.
///   - Stage Add / Remove / RolePermission-replacement operations.
///
/// Persistence is committed via IMasterUnitOfWork.SaveChangesAsync() — this repo does not save.
/// </summary>
public interface IRoleRepository
{
    /// <summary>Find a role by id (no related entities loaded).</summary>
    Task<AppRole?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Find a role by id with RolePermissions → Permission eager-loaded.
    /// Use for role DTO projections and read-modify-save of the role itself.
    /// </summary>
    Task<AppRole?> GetByIdWithPermissionsAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Find a role by id with only the RolePermissions join collection loaded (no Permission nav).
    /// Use for permission-assignment operations where the service clears and re-adds join rows.
    /// </summary>
    Task<AppRole?> GetByIdForPermissionAssignmentAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>List all roles with RolePermissions → Permission eager-loaded, ordered by Name.</summary>
    Task<IReadOnlyList<AppRole>> GetAllWithPermissionsAsync(CancellationToken cancellationToken = default);

    /// <summary>Check whether a role with the given id exists.</summary>
    Task<bool> ExistsByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Check whether a role with the given (case-sensitive) name exists.</summary>
    Task<bool> ExistsByNameAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Stage a new role for insertion.</summary>
    Task AddAsync(AppRole role, CancellationToken cancellationToken = default);

    /// <summary>Stage a role for removal (soft-delete via AuditableEntityInterceptor).</summary>
    void Remove(AppRole role);

    /// <summary>
    /// Stage a batch of RolePermission join rows for removal. Used by permission-assignment
    /// replacement flows where the service loads a role, clears permissions, and adds new ones.
    /// </summary>
    void RemoveRolePermissions(IEnumerable<AppRolePermission> rolePermissions);
}
