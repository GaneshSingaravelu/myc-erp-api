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
    /// The user, but only when they are an ACTIVE member of <paramref name="tenantId"/>,
    /// with their role graph filtered to that same tenant.
    ///
    /// Returns null both when the user does not exist and when they exist outside the
    /// tenant — the caller cannot distinguish the two, which is what keeps the 404
    /// response from confirming that a foreign id is real.
    /// </summary>
    Task<AppUser?> GetByIdInTenantWithRolesAsync(
        Guid id, Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every ACTIVE member of <paramref name="tenantId"/>, roles filtered to that tenant.
    ///
    /// Membership is applied as an EXISTS subquery in the database, not as an in-memory
    /// filter over an unscoped read, and not as a join — so a user with memberships in
    /// several tenants appears exactly once.
    /// </summary>
    Task<IReadOnlyList<AppUser>> GetAllInTenantWithRolesAsync(
        Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>Active role assignments for one user WITHIN one tenant only.</summary>
    Task<IReadOnlyList<AppUserRole>> GetUserRoleAssignmentsInTenantAsync(
        Guid userId, Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>The user's ACTIVE membership row for one tenant, or null.</summary>
    Task<UserTenant?> GetUserTenantAsync(
        Guid userId, Guid tenantId, CancellationToken cancellationToken = default);

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

    /// <summary>Find user by normalized (uppercased) email (no related entities loaded). Used for password reset flows.</summary>
    Task<AppUser?> GetByNormalizedEmailAsync(string normalizedEmail, CancellationToken cancellationToken = default);

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

    /// <summary>
    /// Get all ACTIVE role assignments for a user, including the Role navigation
    /// (RoleName is needed for the DTO projection), each carrying its TenantId.
    /// Ordered by tenant then role name so the response is deterministic.
    /// </summary>
    Task<IReadOnlyList<AppUserRole>> GetUserRoleAssignmentsAsync(Guid userId, CancellationToken cancellationToken = default);

    // ── Tenant assignment ──────────────────────────────────────────────

    /// <summary>
    /// Get all active tenant assignments for a user, including the Tenant navigation
    /// (TenantCode, TenantName needed for the DTO projection).
    /// </summary>
    Task<IReadOnlyList<UserTenant>> GetUserTenantsAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get all UserTenant rows for a user (including inactive) — used by the
    /// replace-tenants flow to clear existing assignments before adding new ones.
    /// </summary>
    Task<IReadOnlyList<UserTenant>> GetUserTenantRowsAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Stage a new UserTenant row for insertion.</summary>
    Task AddUserTenantAsync(UserTenant userTenant, CancellationToken cancellationToken = default);

    /// <summary>Stage a batch of UserTenant join rows for removal.</summary>
    void RemoveUserTenants(IEnumerable<UserTenant> userTenants);
}
