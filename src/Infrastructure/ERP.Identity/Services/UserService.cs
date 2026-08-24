using ERP.Application.Abstractions.Identity;
using ERP.Application.Abstractions.Identity.Contracts;
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

    public UserService(
        IUserRepository users,
        IRoleRepository roles,
        ITenantRepository tenants,
        IMasterUnitOfWork uow)
    {
        _users = users;
        _roles = roles;
        _tenants = tenants;
        _uow = uow;
    }

    public async Task<Result<UserDto>> GetByIdAsync(Guid id)
    {
        var user = await _users.GetByIdWithRolesAsync(id);

        if (user is null)
            return Result.Failure<UserDto>(Error.NotFound("User", id));

        return Result.Success(MapToDto(user));
    }

    public async Task<Result<IReadOnlyList<UserDto>>> GetAllAsync()
    {
        var users = await _users.GetAllWithRolesAsync();
        return Result.Success<IReadOnlyList<UserDto>>(users.Select(MapToDto).ToList());
    }

    public async Task<Result<UserDto>> CreateAsync(CreateUserRequest request)
    {
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
        await _uow.SaveChangesAsync();

        return Result.Success(MapToDto(user));
    }

    public async Task<Result<UserDto>> UpdateAsync(Guid id, UpdateUserRequest request)
    {
        var user = await _users.GetByIdWithRolesAsync(id);

        if (user is null)
            return Result.Failure<UserDto>(Error.NotFound("User", id));

        user.Email = request.Email;
        user.NormalizedEmail = request.Email.ToUpperInvariant();
        user.FirstName = request.FirstName;
        user.LastName = request.LastName;
        user.IsActive = request.IsActive;

        await _uow.SaveChangesAsync();

        return Result.Success(MapToDto(user));
    }

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
    public async Task<Result> AssignRolesAsync(Guid userId, AssignRolesRequest request)
    {
        // ── Validate ────────────────────────────────────────────────────────
        var user = await _users.GetByIdForRoleAssignmentAsync(userId);

        if (user is null)
            return Result.Failure(Error.NotFound("User", userId));

        // Caught explicitly: an omitted tenantId binds to Guid.Empty, which would
        // otherwise reach the database and surface as an opaque FK violation.
        if (request.TenantId == Guid.Empty)
            return Result.Failure(Error.Validation(
                "Role.TenantRequired", "A tenant must be specified for role assignment."));

        if (!await _tenants.ExistsByIdAsync(request.TenantId))
            return Result.Failure(Error.NotFound("Tenant", request.TenantId));

        // Membership is a precondition, never a side effect: assigning a role must
        // not quietly grant a user access to a tenant. Same check the login path
        // uses, so "assigned to a tenant" means one thing across the service.
        if (!await _users.IsAssignedToTenantAsync(userId, request.TenantId))
            return Result.Failure(Error.Validation(
                "User.NotInTenant", "User is not assigned to the specified tenant."));

        // Deduplicated before persistence so caller repetition cannot trip the
        // (UserId, RoleId, TenantId) unique index.
        var roleIds = request.RoleIds.Distinct().ToList();

        foreach (var roleId in roleIds)
        {
            if (!await _roles.ExistsByIdAsync(roleId))
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
    public async Task<Result<IReadOnlyList<UserRoleAssignmentDto>>> GetUserRoleAssignmentsAsync(Guid userId)
    {
        var user = await _users.GetByIdAsync(userId);
        if (user is null)
            return Result.Failure<IReadOnlyList<UserRoleAssignmentDto>>(Error.NotFound("User", userId));

        var assignments = await _users.GetUserRoleAssignmentsAsync(userId);

        // One DTO per row, never collapsed by role name: the same role in two
        // tenants is two distinct assignments and must stay distinguishable.
        var dtos = assignments.Select(ur => new UserRoleAssignmentDto(
            TenantId: ur.TenantId,
            RoleId: ur.RoleId,
            RoleName: ur.Role.Name
        )).ToList();

        return Result.Success<IReadOnlyList<UserRoleAssignmentDto>>(dtos);
    }

    public async Task<Result<IReadOnlyList<UserTenantDto>>> GetUserTenantsAsync(Guid userId)
    {
        var user = await _users.GetByIdAsync(userId);
        if (user is null)
            return Result.Failure<IReadOnlyList<UserTenantDto>>(Error.NotFound("User", userId));

        var userTenants = await _users.GetUserTenantsAsync(userId);

        var dtos = userTenants.Select(ut => new UserTenantDto(
            TenantId: ut.TenantId,
            TenantCode: ut.Tenant.Code,
            TenantName: ut.Tenant.Name,
            IsDefault: ut.IsDefault
        )).ToList();

        return Result.Success<IReadOnlyList<UserTenantDto>>(dtos);
    }

    public async Task<Result> AssignTenantsAsync(Guid userId, AssignTenantsRequest request)
    {
        var user = await _users.GetByIdAsync(userId);
        if (user is null)
            return Result.Failure(Error.NotFound("User", userId));

        // Validate all tenant IDs exist
        foreach (var tenantId in request.TenantIds)
        {
            if (!await _tenants.ExistsByIdAsync(tenantId))
                return Result.Failure(Error.NotFound("Tenant", tenantId));
        }

        // Remove existing tenant assignments
        var existingRows = await _users.GetUserTenantRowsAsync(userId);
        _users.RemoveUserTenants(existingRows);

        // Add new tenant assignments (first one is default)
        var isFirst = true;
        foreach (var tenantId in request.TenantIds)
        {
            await _users.AddUserTenantAsync(new UserTenant
            {
                UserId = userId,
                TenantId = tenantId,
                IsDefault = isFirst,
                IsActive = true,
                JoinedAt = DateTime.UtcNow
            });
            isFirst = false;
        }

        await _uow.SaveChangesAsync();
        return Result.Success();
    }

    private static UserDto MapToDto(AppUser user) => new(
        Id: user.Id,
        UserName: user.UserName,
        Email: user.Email,
        FirstName: user.FirstName,
        LastName: user.LastName,
        FullName: user.FullName,
        IsActive: user.IsActive,
        LastLoginAt: user.LastLoginAt,
        Roles: user.UserRoles.Select(ur => ur.Role.Name).ToList());
}
