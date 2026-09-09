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

    /// <summary>
    /// List the roles visible inside <paramref name="tenantId"/>, with RolePermissions →
    /// Permission eager-loaded, ordered by Name.
    ///
    /// <para>Visible: a system role (TenantId null, available to every tenant) or a role
    /// owned by this tenant. A role owned by another tenant is never returned.</para>
    ///
    /// <para>Three predicates coexist in this interface and must not be confused.
    /// VISIBILITY (here) is TenantId null or current. ASSIGNABILITY — whether a role may be
    /// granted to a user, see <see cref="ExistsAndIsAssignableInTenantAsync"/> — happens to
    /// use the same rule today. MUTABILITY is strictly narrower: owned by the current tenant
    /// AND not a system role. Never reach for a visibility method to authorise a write.</para>
    /// </summary>
    Task<IReadOnlyList<AppRole>> GetAllVisibleInTenantWithPermissionsAsync(
        Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Find a role by id, but only if it is visible inside <paramref name="tenantId"/> —
    /// a system role or one owned by this tenant — with RolePermissions → Permission
    /// eager-loaded.
    ///
    /// <para>Returns null for a role owned by another tenant, exactly as it does for an id
    /// that does not exist, so the caller cannot tell the two apart and foreign role
    /// existence is not disclosed.</para>
    /// </summary>
    Task<AppRole?> GetByIdVisibleInTenantWithPermissionsAsync(
        Guid id, Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Find a role by id under the same VISIBILITY boundary as the read methods, but
    /// TRACKED and without the permission graph, so the caller can classify it before
    /// deciding whether a write is allowed.
    ///
    /// <para>The boundary is visibility, not mutability, on purpose: it collapses "belongs
    /// to another tenant" and "does not exist" into one null answer, so a caller can answer
    /// 404 for both without ever running a global existence probe. Deciding whether the
    /// returned role may actually be written is the caller's job — a system role and an
    /// unowned legacy role are both visible and neither is mutable.</para>
    ///
    /// <para>Deliberately loads no RolePermissions: a destructive replacement must not have
    /// the graph in hand before authorization has succeeded.</para>
    /// </summary>
    Task<AppRole?> GetTrackedByIdVisibleInTenantAsync(
        Guid id, Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Explicitly load RolePermissions → Permission for an already-tracked role. Used after
    /// a write has been authorised, so the mutable graph enters the change tracker only once
    /// the caller is entitled to change it.
    /// </summary>
    Task LoadRolePermissionsAsync(AppRole role, CancellationToken cancellationToken = default);

    /// <summary>Check whether a role with the given id exists.</summary>
    Task<bool> ExistsByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether the role exists AND may be granted inside <paramref name="tenantId"/>.
    ///
    /// <para>AppRole.TenantId is nullable by design: null means a system role available
    /// to every tenant, a GUID means a role owned by one tenant. A role owned by
    /// another tenant is therefore not assignable here, and is reported as simply not
    /// found so its existence is not disclosed.</para>
    /// </summary>
    Task<bool> ExistsAndIsAssignableInTenantAsync(
        Guid roleId, Guid tenantId, CancellationToken cancellationToken = default);

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
