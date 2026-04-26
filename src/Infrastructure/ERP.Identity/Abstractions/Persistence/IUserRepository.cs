using ERP.Identity.Entities;

namespace ERP.Identity.Abstractions.Persistence;

/// <summary>
/// Repository for AppUser aggregate (Master DB).
///
/// Why a SPECIALIZED repository (not the generic IRepository&lt;T&gt;)?
/// ===============================================================
/// 1. AppUser lives in MasterDbContext, not AppDbContext (which IRepository&lt;T&gt; targets).
/// 2. Auth queries need eager-loading graphs (UserRoles → Role → Permissions) — encapsulated
///    here behind named methods rather than leaking EF Include() chains into the service.
/// 3. Lookup-by-NormalizedUserName/Email is a bounded vocabulary; making it a method
///    (instead of a free-form predicate) keeps the call sites readable and consistent.
///
/// Persistence is committed via IMasterUnitOfWork.SaveChangesAsync() — repositories do not save.
/// </summary>
public interface IUserRepository
{
    /// <summary>Find user by id (no related entities loaded).</summary>
    Task<AppUser?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Find user by id with role names eager-loaded (UserRoles → Role only, no permissions).
    /// Use for user DTO projections where only role names are needed.
    /// </summary>
    Task<AppUser?> GetByIdWithRolesAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Find user by id with full role + permission graph eager-loaded.</summary>
    Task<AppUser?> GetByIdWithRolesAndPermissionsAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Find user by id with UserRoles collection loaded (but not Role navigation).
    /// Use for role-assignment operations that modify the UserRoles join table.
    /// </summary>
    Task<AppUser?> GetByIdForRoleAssignmentAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Find user by normalized (uppercased) username with full role + permission graph eager-loaded.</summary>
    Task<AppUser?> GetByNormalizedUserNameWithRolesAndPermissionsAsync(
        string normalizedUserName,
        CancellationToken cancellationToken = default);

    /// <summary>List all users with role names eager-loaded (for admin user list pages).</summary>
    Task<IReadOnlyList<AppUser>> GetAllWithRolesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Return the distinct set of permission codes that the user currently has,
    /// computed from their active role assignments.
    /// </summary>
    Task<IReadOnlyList<string>> GetPermissionCodesForUserAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Check whether any user already uses this normalized username.</summary>
    Task<bool> ExistsByNormalizedUserNameAsync(string normalizedUserName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Verify that the user is an active member of the given tenant via the
    /// <c>UserTenants</c> junction table. This is the cross-tenant isolation
    /// gate enforced at login time — without it, valid credentials let a user
    /// authenticate into ANY existing tenant by varying the X-Tenant-Id header.
    ///
    /// Returns <c>true</c> only when a matching <c>UserTenant</c> row exists
    /// AND <c>IsActive == true</c>. Inactive memberships are treated the same
    /// as missing ones — the caller cannot distinguish "never assigned" from
    /// "access revoked" by design (avoids account-enumeration leaks).
    /// </summary>
    Task<bool> IsAssignedToTenantAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>Check whether any user already uses this normalized email.</summary>
    Task<bool> ExistsByNormalizedEmailAsync(string normalizedEmail, CancellationToken cancellationToken = default);

    /// <summary>Stage a new user for insertion. Persisted on next IMasterUnitOfWork.SaveChangesAsync().</summary>
    Task AddAsync(AppUser user, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stage a user for removal. Soft-delete is applied by the AuditableEntityInterceptor
    /// at SaveChanges time (IsDeleted = true, DeletedAt/DeletedBy populated).
    /// </summary>
    void Remove(AppUser user);

    /// <summary>
    /// Stage a batch of UserRole join rows for removal. Used by role-assignment replacement flows
    /// where the service loads an AppUser, clears their current roles, and adds new ones.
    /// </summary>
    void RemoveUserRoles(IEnumerable<AppUserRole> userRoles);
}
