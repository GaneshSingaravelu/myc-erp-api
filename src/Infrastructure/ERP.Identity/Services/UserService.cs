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

    public async Task<Result> AssignRolesAsync(Guid userId, AssignRolesRequest request)
    {
        var user = await _users.GetByIdForRoleAssignmentAsync(userId);

        if (user is null)
            return Result.Failure(Error.NotFound("User", userId));

        // Remove existing role assignments
        _users.RemoveUserRoles(user.UserRoles);

        // Add new role assignments
        foreach (var roleId in request.RoleIds)
        {
            if (!await _roles.ExistsByIdAsync(roleId))
                return Result.Failure(Error.NotFound("Role", roleId));

            user.UserRoles.Add(new AppUserRole { UserId = userId, RoleId = roleId });
        }

        await _uow.SaveChangesAsync();
        return Result.Success();
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
