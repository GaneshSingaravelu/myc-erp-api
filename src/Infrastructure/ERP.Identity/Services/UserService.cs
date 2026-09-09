using ERP.Application.Abstractions.Identity;
using ERP.Application.Abstractions.Identity.Contracts;
using ERP.Application.Abstractions.MultiTenancy;
using ERP.Identity.Abstractions.Persistence;
using ERP.Identity.Entities;
using ERP.SharedKernel.Results;

namespace ERP.Identity.Services;

/// <summary>
/// User management service (admin CRUD + role assignment).
///
/// Persistence goes through <see cref="IUserRepository"/> and <see cref="IRoleRepository"/>;
/// commits happen via <see cref="IMasterUnitOfWork.SaveChangesAsync"/>. The service holds
/// no EF Core references — that's enforced by the Identity architecture test.
/// </summary>
public class UserService : IUserService
{
    private readonly IUserRepository _users;
    private readonly IRoleRepository _roles;
    private readonly ITenantRepository _tenants;
    private readonly IMasterUnitOfWork _uow;
    private readonly ITenantProvider _tenantProvider;

    public UserService(
        IUserRepository users,
        IRoleRepository roles,
        ITenantRepository tenants,
        IMasterUnitOfWork uow,
        ITenantProvider tenantProvider)
    {
        _users = users;
        _roles = roles;
        _tenants = tenants;
        _uow = uow;
        _tenantProvider = tenantProvider;
    }

    /// <summary>
    /// The tenant this request is authorised for — the ONLY tenant identity this
    /// service may use for authorization decisions.
    ///
    /// It comes from <see cref="ITenantProvider"/>, which reads the value
    /// <c>TenantMiddleware</c> placed in <c>HttpContext.Items["CurrentTenant"]</c>
    /// *after* it cross-checked the <c>X-Tenant-Id</c> header against the caller's
    /// <c>tenant_id</c> claim, so the two are already reconciled by the time this runs.
    ///
    /// <para><b>Never read <c>X-Tenant-Id</c> or the <c>tenant_id</c> claim directly
    /// in this service.</b> Either one alone is a client-supplied value; only the
    /// reconciled result is an authorization input.</para>
    ///
    /// <para>Fails closed: <see cref="ITenantProvider.GetCurrentTenant"/> throws when
    /// no validated tenant is present, so code that reaches this without tenant
    /// resolution raises an error instead of running an unscoped query.</para>
    ///
    /// <para>Introduced by the CT-4 authorization remediation (phase P1). Not yet
    /// consulted by any endpoint — behaviour is unchanged until phase P2.</para>
    /// </summary>
    private Guid CurrentTenantId => _tenantProvider.GetCurrentTenant().Id;

    /// <summary>
    /// Read one user, scoped to the authenticated tenant.
    ///
    /// A user outside the current tenant is reported as NotFound rather than
    /// Forbidden: a 403 would confirm the id exists and turn this endpoint into an
    /// enumeration oracle for other tenants' user ids. Unknown id and out-of-tenant
    /// id are therefore indistinguishable to the caller — deliberately.
    /// </summary>
    public async Task<Result<UserDto>> GetByIdAsync(Guid id)
    {
        var tenantId = CurrentTenantId;
        var user = await _users.GetByIdInTenantWithRolesAsync(id, tenantId);

        if (user is null)
            return Result.Failure<UserDto>(Error.NotFound("User", id));

        return Result.Success(MapToDto(user, tenantId));
    }

    /// <summary>
    /// List the users this tenant administers: active members of the authenticated
    /// tenant only. Users with no membership anywhere, and users whose membership
    /// here is inactive, are not part of any tenant's administrative scope.
    /// Each returned user carries only the roles held in this tenant.
    /// </summary>
    public async Task<Result<IReadOnlyList<UserDto>>> GetAllAsync()
    {
        var tenantId = CurrentTenantId;
        var users = await _users.GetAllInTenantWithRolesAsync(tenantId);
        return Result.Success<IReadOnlyList<UserDto>>(
            users.Select(u => MapToDto(u, tenantId)).ToList());
    }

    /// <summary>
    /// Create a user inside the authenticated tenant.
    ///
    /// <para><b>Tenant placement is derived, never supplied.</b> CreateUserRequest
    /// carries no tenant field and deliberately gains none: the tenant comes from the
    /// middleware-validated context, so a caller cannot place a user anywhere they are
    /// not already authorised to act.</para>
    ///
    /// <para>The membership is created with the identity rather than left to a later
    /// call. Since the reads became tenant-scoped, a user with no membership is
    /// invisible to every tenant administrator — so creating one without a membership
    /// would produce a user the creator could not then read, edit, or assign roles to.
    /// This also matches how identities are provisioned elsewhere: IdentitySeeder adds
    /// the AppUser, its role and its UserTenant together, and docs/TENANT_PROVISIONING.md
    /// documents create/link/assign as one procedure.</para>
    ///
    /// <para><b>Atomic.</b> Both entities are tracked and committed by the single
    /// existing SaveChanges. Ids are client-generated (BaseEntity, ValueGeneratedNever),
    /// so the membership can reference the user before either row is written. There is
    /// no window in which a saved identity exists without its membership.</para>
    ///
    /// <para>A username or email collision is still a Conflict. An existing identity is
    /// never attached to this tenant, never mutated, and never returned — attaching one
    /// would let a caller pull a user in from another tenant by guessing an address.</para>
    /// </summary>
    public async Task<Result<UserDto>> CreateAsync(CreateUserRequest request)
    {
        var tenantId = CurrentTenantId;

        var normalizedUserName = request.UserName.ToUpperInvariant();
        var normalizedEmail = request.Email.ToUpperInvariant();

        if (await _users.ExistsByNormalizedUserNameAsync(normalizedUserName))
            return Result.Failure<UserDto>(Error.Conflict("User.UserNameExists", $"Username '{request.UserName}' already exists."));

        if (await _users.ExistsByNormalizedEmailAsync(normalizedEmail))
            return Result.Failure<UserDto>(Error.Conflict("User.EmailExists", $"Email '{request.Email}' already exists."));

        var user = new AppUser
        {
            UserName = request.UserName,
            NormalizedUserName = normalizedUserName,
            Email = request.Email,
            NormalizedEmail = normalizedEmail,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            FirstName = request.FirstName,
            LastName = request.LastName,
            IsActive = true
        };

        await _users.AddAsync(user);

        // IsDefault: true for a sole membership, following the established convention —
        // IdentitySeeder sets it on the only membership it creates, and
        // AssignTenantsAsync sets IsDefault on the first tenant in the list.
        await _users.AddUserTenantAsync(new UserTenant
        {
            UserId = user.Id,
            TenantId = tenantId,
            IsDefault = true,
            IsActive = true,
            JoinedAt = DateTime.UtcNow
        });

        await _uow.SaveChangesAsync();

        return Result.Success(MapToDto(user, tenantId));
    }

    /// <summary>
    /// Update a user, scoped to the authenticated tenant.
    ///
    /// <para><b>Authorization runs before anything mutable is loaded.</b> The
    /// membership check is a standalone query, so a target outside this tenant is
    /// rejected without ever materialising a tracked entity — there is no window in
    /// which foreign state is attached to the change tracker and could reach
    /// SaveChanges. "Load globally, mutate, then check" is the shape this
    /// deliberately avoids.</para>
    ///
    /// <para>Out-of-tenant is reported as NotFound, matching
    /// <see cref="GetByIdAsync"/>: a distinct response would confirm the id exists.</para>
    ///
    /// <para>The response is projected with the tenant-aware overload so a successful
    /// update cannot echo back role names the caller may not see.</para>
    /// </summary>
    public async Task<Result<UserDto>> UpdateAsync(Guid id, UpdateUserRequest request)
    {
        var tenantId = CurrentTenantId;

        if (!await _users.IsAssignedToTenantAsync(id, tenantId))
            return Result.Failure<UserDto>(Error.NotFound("User", id));

        var user = await _users.GetByIdWithRolesAsync(id);

        if (user is null)
            return Result.Failure<UserDto>(Error.NotFound("User", id));

        user.Email = request.Email;
        user.NormalizedEmail = request.Email.ToUpperInvariant();
        user.FirstName = request.FirstName;
        user.LastName = request.LastName;
        user.IsActive = request.IsActive;

        await _uow.SaveChangesAsync();

        return Result.Success(MapToDto(user, tenantId));
    }

    /// <summary>
    /// <b>NOT YET TENANT-SCOPED — deliberately unchanged (CT-4 phase P3, blocked).</b>
    ///
    /// <para>Adding a membership check here would be unsafe on its own. This deletes
    /// the GLOBAL identity: <c>Remove</c> marks the AppUser row deleted, the auditable
    /// interceptor converts that to <c>IsDeleted = true</c>, and a global query filter
    /// then hides the user from every query in every tenant. UserTenant additionally
    /// cascades from User.</para>
    ///
    /// <para>So for a user who belongs to Tenant A and Tenant B, a Tenant A
    /// administrator passing a membership check would still erase that user from
    /// Tenant B. A simple membership gate would make the action look authorised while
    /// leaving the cross-tenant destruction intact.</para>
    ///
    /// <para>Whether DELETE means "remove the identity" or "remove from this tenant"
    /// is a product decision, not one to settle in code. Awaiting owner direction.</para>
    /// </summary>
    public async Task<Result> DeleteAsync(Guid id)
    {
        var user = await _users.GetByIdAsync(id);
        if (user is null)
            return Result.Failure(Error.NotFound("User", id));

        _users.Remove(user); // Soft delete via interceptor
        await _uow.SaveChangesAsync();

        return Result.Success();
    }

    /// <summary>
    /// Replace a user's roles WITHIN ONE TENANT.
    ///
    /// <para>UserRoles is keyed on (UserId, RoleId, TenantId), so every row needs a
    /// tenant. The tenant comes from the request and is never inferred — not from
    /// the user's default tenant, not from the caller's current tenant.</para>
    ///
    /// <para><b>Scope:</b> only the requested tenant's rows are replaced. Roles the
    /// user holds in other tenants are left alone, which is the whole point of a
    /// tenant-scoped assignment.</para>
    ///
    /// <para><b>Order:</b> everything is validated before anything is removed. The
    /// previous version deleted the user's rows first and validated role ids
    /// afterwards, relying on "SaveChanges was never reached" to stay safe — a
    /// guarantee that evaporates the moment anything else calls SaveChanges.</para>
    /// </summary>
    /// <summary>
    /// Replace a user's roles within one tenant.
    ///
    /// <para><b>Two independent gates, in this order.</b> ACTOR authority — is the
    /// caller allowed to act in the tenant named in the body? — is checked before
    /// anything about the TARGET. That ordering is the whole point: during CT-4 a
    /// cross-tenant attempt was refused by the target-membership check, which made the
    /// result useless as evidence about actor authority. The two failures now carry
    /// different error codes so a test can prove which gate fired.</para>
    ///
    /// <para>The actor gate also runs before the user lookup, so a caller with no
    /// authority over the requested tenant cannot use this endpoint to discover
    /// whether a user id or a tenant id exists.</para>
    ///
    /// <para>Replacement stays scoped to the requested tenant — roles the user holds
    /// in other tenants are untouched.</para>
    /// </summary>
    public async Task<Result> AssignRolesAsync(Guid userId, AssignRolesRequest request)
    {
        var currentTenantId = CurrentTenantId;

        // ── Request shape ───────────────────────────────────────────────────
        // Caught explicitly: an omitted tenantId binds to Guid.Empty, which would
        // otherwise reach the database and surface as an opaque FK violation.
        if (request.TenantId == Guid.Empty)
            return Result.Failure(Error.Validation(
                "Role.TenantRequired", "A tenant must be specified for role assignment."));

        // ── Gate 1: ACTOR AUTHORITY ─────────────────────────────────────────
        // A bare Admin role grants nothing outside the tenant this request was
        // authenticated for. The body may name a tenant; it may not choose one.
        if (request.TenantId != currentTenantId)
            return Result.Failure(Error.Validation(
                "Role.TenantNotAuthorized",
                "You are not authorized to assign roles in the specified tenant."));

        // Defence in depth. After the actor gate this equals the middleware-validated
        // tenant, which was already resolved from the tenant store — so it should never
        // fail. It stays because a misconfigured tenant provider must not silently
        // become an authorization bypass.
        if (!await _tenants.ExistsByIdAsync(request.TenantId))
            return Result.Failure(Error.NotFound("Tenant", request.TenantId));

        // ── Gate 2: TARGET ──────────────────────────────────────────────────
        var user = await _users.GetByIdForRoleAssignmentAsync(userId);

        if (user is null)
            return Result.Failure(Error.NotFound("User", userId));

        // Membership is a precondition, never a side effect: assigning a role must
        // not quietly grant a user access to a tenant. Same check the login path
        // uses, so "assigned to a tenant" means one thing across the service.
        if (!await _users.IsAssignedToTenantAsync(userId, request.TenantId))
            return Result.Failure(Error.Validation(
                "User.NotInTenant", "User is not assigned to the specified tenant."));

        // Deduplicated before persistence so caller repetition cannot trip the
        // (UserId, RoleId, TenantId) unique index.
        var roleIds = request.RoleIds.Distinct().ToList();

        // A role is grantable here if it is a system role (TenantId null) or owned by
        // this tenant. Another tenant's custom role reports as NotFound rather than as
        // a distinct error, so this cannot be used to enumerate their roles.
        foreach (var roleId in roleIds)
        {
            if (!await _roles.ExistsAndIsAssignableInTenantAsync(roleId, request.TenantId))
                return Result.Failure(Error.NotFound("Role", roleId));
        }

        // ── Mutate ──────────────────────────────────────────────────────────
        var rolesInTenant = user.UserRoles
            .Where(ur => ur.TenantId == request.TenantId)
            .ToList();

        _users.RemoveUserRoles(rolesInTenant);

        foreach (var roleId in roleIds)
        {
            user.UserRoles.Add(new AppUserRole
            {
                UserId = userId,
                RoleId = roleId,
                TenantId = request.TenantId
            });
        }

        await _uow.SaveChangesAsync();
        return Result.Success();
    }

    /// <summary>
    /// The user's tenant-scoped role assignments.
    ///
    /// <para>Exists because <c>UserDto.Roles</c> is a flat list of role names
    /// collapsed across every tenant: it carries neither the tenant nor the role id,
    /// so a caller editing one tenant's roles cannot tell which entries apply. With
    /// assignment keyed on <c>(UserId, RoleId, TenantId)</c>, acting on that flat
    /// list would write roles into the wrong tenant.</para>
    ///
    /// <para>A user with no assignments is not an error — an empty list is the
    /// correct answer, and the common one for a newly created user.</para>
    /// </summary>
    /// <summary>
    /// Role assignments for a user, scoped to the authenticated tenant.
    ///
    /// The target must be an active member here first — otherwise NotFound, matching
    /// <see cref="GetByIdAsync"/>. Assignments held in other tenants are never
    /// returned: a Tenant A administrator must not learn what the user is elsewhere.
    /// </summary>
    public async Task<Result<IReadOnlyList<UserRoleAssignmentDto>>> GetUserRoleAssignmentsAsync(Guid userId)
    {
        var tenantId = CurrentTenantId;

        if (!await _users.IsAssignedToTenantAsync(userId, tenantId))
            return Result.Failure<IReadOnlyList<UserRoleAssignmentDto>>(Error.NotFound("User", userId));

        var assignments = await _users.GetUserRoleAssignmentsInTenantAsync(userId, tenantId);

        // One DTO per row, never collapsed by role name: the same role in two
        // tenants is two distinct assignments and must stay distinguishable.
        var dtos = assignments.Select(ur => new UserRoleAssignmentDto(
            TenantId: ur.TenantId,
            RoleId: ur.RoleId,
            RoleName: ur.Role.Name
        )).ToList();

        return Result.Success<IReadOnlyList<UserRoleAssignmentDto>>(dtos);
    }

    /// <summary>
    /// The target's membership of the AUTHENTICATED tenant — and nothing else.
    ///
    /// This endpoint used to return every tenant the user belonged to. Returning that
    /// set to a single tenant's administrator discloses the existence, code and name
    /// of tenants they have no authority over, so the response is now the current
    /// tenant's membership alone (at most one row). Cross-tenant membership
    /// visibility, if ever needed, requires a separately authorised design.
    /// </summary>
    public async Task<Result<IReadOnlyList<UserTenantDto>>> GetUserTenantsAsync(Guid userId)
    {
        var membership = await _users.GetUserTenantAsync(userId, CurrentTenantId);

        if (membership is null)
            return Result.Failure<IReadOnlyList<UserTenantDto>>(Error.NotFound("User", userId));

        var dtos = new List<UserTenantDto>
        {
            new(
                TenantId: membership.TenantId,
                TenantCode: membership.Tenant.Code,
                TenantName: membership.Tenant.Name,
                IsDefault: membership.IsDefault)
        };

        return Result.Success<IReadOnlyList<UserTenantDto>>(dtos);
    }

    /// <summary>
    /// Membership management, confined to the caller's own tenant.
    ///
    /// <para><b>This used to be a global replace-all</b>: it loaded every UserTenant row
    /// for the user, deleted them, and recreated the submitted list. Combined with the
    /// tenant-scoped read — which shows a Tenant A administrator only the Tenant A
    /// membership — that was a live data-loss path. Opening the Tenants tab on a user
    /// belonging to A and B and pressing Save submitted [A], and B's membership was
    /// destroyed. That behaviour is retired.</para>
    ///
    /// <para><b>The caller may speak only about their own tenant.</b> Any other tenant
    /// id in the body is refused outright rather than filtered out, so a request whose
    /// meaning exceeds the caller's authority never partially succeeds.</para>
    ///
    /// <para><b>The target must already be an active member here.</b> Attaching a
    /// tenant-less or foreign identity is deliberately not supported: it would let an
    /// administrator pull any user they can name into their tenant and then read that
    /// user through the endpoints this remediation just secured. Out-of-scope targets
    /// report as NotFound so this cannot be used to discover that a user exists
    /// elsewhere.</para>
    ///
    /// <para><b>Consequence — this endpoint no longer mutates anything.</b> The request
    /// carries tenant ids and no per-membership fields; attachment and removal are both
    /// out of scope; and the only request that survives validation ([A] for a user
    /// already active in A) describes the state that already exists. So a valid call is
    /// a verified no-op. It is kept because the frontend round-trip depends on it
    /// succeeding, and because silently accepting-and-ignoring a foreign tenant id
    /// would be worse than refusing it.</para>
    ///
    /// <para>Membership ejection — removing a user from a tenant — is a separate
    /// product decision covering orphaned role rows, default-tenant promotion,
    /// tenant-less identity policy and re-attachment. Not designed here.</para>
    /// </summary>
    public async Task<Result> AssignTenantsAsync(Guid userId, AssignTenantsRequest request)
    {
        var currentTenantId = CurrentTenantId;

        // ── Request shape ───────────────────────────────────────────────────
        // An empty list previously cleared every membership the user had. Ejection is
        // out of scope, so an empty selection is now a rejected request rather than a
        // silent way to strand an identity in no tenant at all.
        if (request.TenantIds is null || request.TenantIds.Count == 0)
            return Result.Failure(Error.Validation(
                "Tenant.SelectionRequired",
                "At least one tenant must be specified. Removing a user from a tenant is not supported here."));

        // ── Gate 1: BODY TENANT AUTHORITY ───────────────────────────────────
        // Refused, never filtered: quietly dropping B from [A, B] would report success
        // for a request the caller was not entitled to make.
        if (request.TenantIds.Distinct().Any(tenantId => tenantId != currentTenantId))
            return Result.Failure(Error.Validation(
                "Tenant.NotAuthorized",
                "You may only manage membership of the tenant you are signed in to."));

        // ── Gate 2: TARGET ──────────────────────────────────────────────────
        // Active membership, matching every other tenant-scoped endpoint. An inactive
        // membership is not a membership, so it cannot be revived through this path.
        if (!await _users.IsAssignedToTenantAsync(userId, currentTenantId))
            return Result.Failure(Error.NotFound("User", userId));

        // ── No mutation ─────────────────────────────────────────────────────
        // The request asks for exactly the membership that already exists, and there is
        // no other field it could carry. Nothing is deleted, recreated, or re-dated —
        // in particular the user's other tenants, their IsDefault flags and their audit
        // values are never read for a decision and never written.
        return Result.Success();
    }

    /// <summary>
    /// Project a user, exposing ONLY the role names held in <paramref name="tenantId"/>.
    ///
    /// The repository already filters roles in the query, so this looks redundant — it
    /// is not. EF Core's identity resolution can repopulate a filtered navigation from
    /// entities the same DbContext already tracks, which silently restores the other
    /// tenant's roles. Two P2 tests caught exactly that. The reads therefore run
    /// AsNoTracking AND the tenant filter is re-applied here, so the projection is
    /// correct regardless of what the context happens to be tracking.
    /// </summary>
    private static UserDto MapToDto(AppUser user, Guid tenantId) => new(
        Id: user.Id,
        UserName: user.UserName,
        Email: user.Email,
        FirstName: user.FirstName,
        LastName: user.LastName,
        FullName: user.FullName,
        IsActive: user.IsActive,
        LastLoginAt: user.LastLoginAt,
        Roles: user.UserRoles
            .Where(ur => ur.TenantId == tenantId)
            .Select(ur => ur.Role.Name)
            .ToList());

}
