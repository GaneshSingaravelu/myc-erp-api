using ERP.Application.Abstractions.Identity;
using ERP.Application.Abstractions.Identity.Contracts;
using ERP.Application.Abstractions.MultiTenancy;
using ERP.Identity.Abstractions.Persistence;
using ERP.Identity.Entities;
using ERP.SharedKernel.Results;

namespace ERP.Identity.Services;

/// <summary>
/// Role management service (admin CRUD + permission assignment + permission catalog).
///
/// Persistence goes through <see cref="IRoleRepository"/> and <see cref="IPermissionRepository"/>;
/// commits happen via <see cref="IMasterUnitOfWork.SaveChangesAsync"/>. The service holds
/// no EF Core references — that's enforced by the Identity architecture test.
/// </summary>
public class RoleService : IRoleService
{
    private readonly IRoleRepository _roles;
    private readonly IPermissionRepository _permissions;
    private readonly IMasterUnitOfWork _uow;
    private readonly ITenantProvider _tenantProvider;

    public RoleService(
        IRoleRepository roles,
        IPermissionRepository permissions,
        IMasterUnitOfWork uow,
        ITenantProvider tenantProvider)
    {
        _roles = roles;
        _permissions = permissions;
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
    /// reconciled result is an authorization input. A tenant id arriving in a request
    /// body is not an authorization input either.</para>
    ///
    /// <para>Fails closed: <see cref="ITenantProvider.GetCurrentTenant"/> throws when
    /// no validated tenant is present, so code that reaches this without tenant
    /// resolution raises an error instead of running an unscoped query.</para>
    ///
    /// <para>Introduced by the Roles authorization remediation (phase S-A1). Consulted
    /// by the read paths below; the write paths still run unscoped and are addressed in
    /// a later phase.</para>
    /// </summary>
    private Guid CurrentTenantId => _tenantProvider.GetCurrentTenant().Id;

    /// <summary>
    /// Why a write against <paramref name="role"/> must be refused, or null when it may
    /// proceed. <paramref name="role"/> has already been narrowed to the VISIBILITY boundary,
    /// so anything non-null here is either a system role, an unowned legacy role, or a role
    /// this tenant owns.
    ///
    /// <para>Order matters. IsSystemRole is checked first because protection is independent
    /// of ownership: a system role is immutable whether its TenantId is null or this tenant's.
    /// Only then does ownership decide, and a null TenantId at that point is the legacy
    /// TenantId-null / IsSystemRole-false shape that no tenant owns — visible to everyone,
    /// writable by no one, until the role data reconciliation decision is taken. Assigning it
    /// an owner here would be inventing ownership.</para>
    ///
    /// <para>A null role yields NotFound, which is also the answer for a role belonging to
    /// another tenant — the visibility query already made the two indistinguishable.</para>
    /// </summary>
    private static Error? MutationRefusalFor(AppRole? role, Guid id)
    {
        if (role is null)
            return Error.NotFound("Role", id);

        if (role.IsSystemRole)
            return Error.Validation("Role.SystemRole", "System roles cannot be modified.");

        if (role.TenantId is null)
            return Error.Validation(
                "Role.UnownedRole",
                "This role is not owned by any tenant and cannot be modified.");

        return null;
    }

    /// <summary>
    /// Read one role that is visible in the current tenant — a system role, or one this
    /// tenant owns.
    ///
    /// <para>A role belonging to another tenant answers Role.NotFound, the same answer an
    /// unknown id gets. Returning Forbidden instead would confirm that the id names a real
    /// role, which is a disclosure in its own right.</para>
    /// </summary>
    public async Task<Result<RoleDto>> GetByIdAsync(Guid id)
    {
        var role = await _roles.GetByIdVisibleInTenantWithPermissionsAsync(id, CurrentTenantId);

        if (role is null)
            return Result.Failure<RoleDto>(Error.NotFound("Role", id));

        return Result.Success(MapToDto(role));
    }

    /// <summary>
    /// List the roles visible in the current tenant: every system role plus the roles this
    /// tenant owns. Another tenant's custom roles are never included.
    /// </summary>
    public async Task<Result<IReadOnlyList<RoleDto>>> GetAllAsync()
    {
        var roles = await _roles.GetAllVisibleInTenantWithPermissionsAsync(CurrentTenantId);
        return Result.Success<IReadOnlyList<RoleDto>>(roles.Select(MapToDto).ToList());
    }

    public async Task<Result<RoleDto>> CreateAsync(CreateRoleRequest request)
    {
        if (await _roles.ExistsByNameAsync(request.Name))
            return Result.Failure<RoleDto>(Error.Conflict("Role.NameExists", $"Role '{request.Name}' already exists."));

        var role = new AppRole
        {
            Name = request.Name,
            Description = request.Description,
            IsSystemRole = false
        };

        await _roles.AddAsync(role);
        await _uow.SaveChangesAsync();

        return Result.Success(MapToDto(role));
    }

    /// <summary>
    /// Update a role the current tenant owns. Name and description only — ownership and
    /// system-role protection are not part of the update contract and are never written here.
    /// </summary>
    public async Task<Result<RoleDto>> UpdateAsync(Guid id, UpdateRoleRequest request)
    {
        var role = await _roles.GetTrackedByIdVisibleInTenantAsync(id, CurrentTenantId);

        var refusal = MutationRefusalFor(role, id);
        if (refusal is not null)
            return Result.Failure<RoleDto>(refusal);

        // Authorised. The permission graph is only needed to shape the response, so it is
        // fetched now rather than being in hand while the decision was still open.
        await _roles.LoadRolePermissionsAsync(role!);

        role!.Name = request.Name;
        role.Description = request.Description;
        await _uow.SaveChangesAsync();

        return Result.Success(MapToDto(role));
    }

    public async Task<Result> DeleteAsync(Guid id)
    {
        var role = await _roles.GetByIdAsync(id);
        if (role is null)
            return Result.Failure(Error.NotFound("Role", id));

        if (role.IsSystemRole)
            return Result.Failure(Error.Validation("Role.SystemRole", "System roles cannot be deleted."));

        _roles.Remove(role);
        await _uow.SaveChangesAsync();

        return Result.Success();
    }

    /// <summary>
    /// Replace the permission set of a role the current tenant owns.
    ///
    /// <para>The step order is the security property, not a style choice. Replacement is
    /// destructive and unrecoverable from the caller's side, so nothing is removed until the
    /// caller is known to be entitled to the role AND every requested permission id has been
    /// checked. Previously the clear-then-validate order meant an unknown id could leave the
    /// staged set half-rewritten, and no ownership check ran at all.</para>
    /// </summary>
    public async Task<Result> AssignPermissionsAsync(Guid roleId, AssignPermissionsRequest request)
    {
        // 1-2. Current tenant, then the role narrowed to what this tenant may see.
        var role = await _roles.GetTrackedByIdVisibleInTenantAsync(roleId, CurrentTenantId);

        // 3. Missing, foreign, system and unowned all stop here — before any mutation.
        var refusal = MutationRefusalFor(role, roleId);
        if (refusal is not null)
            return Result.Failure(refusal);

        // 4. Every requested permission must exist before the old set is touched, so a bad
        //    id cannot leave a partially rewritten set behind. Permissions are global
        //    catalogue data and carry no tenant dimension.
        foreach (var permissionId in request.PermissionIds)
        {
            if (!await _permissions.ExistsByIdAsync(permissionId))
                return Result.Failure(Error.NotFound("Permission", permissionId));
        }

        // 5. Authorised and validated — only now does the destructive graph get loaded.
        await _roles.LoadRolePermissionsAsync(role!);

        // 6. Replace.
        _roles.RemoveRolePermissions(role!.RolePermissions);

        foreach (var permissionId in request.PermissionIds)
            role.RolePermissions.Add(new AppRolePermission { RoleId = roleId, PermissionId = permissionId });

        // 7. One commit.
        await _uow.SaveChangesAsync();
        return Result.Success();
    }

    public async Task<Result<IReadOnlyList<PermissionDto>>> GetAllPermissionsAsync()
    {
        var permissions = await _permissions.GetAllOrderedAsync();

        var dtos = permissions
            .Select(p => new PermissionDto(p.Id, p.Code, p.Name, p.Category))
            .ToList();

        return Result.Success<IReadOnlyList<PermissionDto>>(dtos);
    }

    private static RoleDto MapToDto(AppRole role) => new(
        Id: role.Id,
        Name: role.Name,
        Description: role.Description,
        IsSystemRole: role.IsSystemRole,
        Permissions: role.RolePermissions
            .Select(rp => new PermissionDto(rp.Permission.Id, rp.Permission.Code, rp.Permission.Name, rp.Permission.Category))
            .ToList());
}
