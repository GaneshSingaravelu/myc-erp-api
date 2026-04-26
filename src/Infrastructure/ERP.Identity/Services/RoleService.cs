using ERP.Application.Abstractions.Identity;
using ERP.Application.Abstractions.Identity.Contracts;
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

    public RoleService(
        IRoleRepository roles,
        IPermissionRepository permissions,
        IMasterUnitOfWork uow)
    {
        _roles = roles;
        _permissions = permissions;
        _uow = uow;
    }

    public async Task<Result<RoleDto>> GetByIdAsync(Guid id)
    {
        var role = await _roles.GetByIdWithPermissionsAsync(id);

        if (role is null)
            return Result.Failure<RoleDto>(Error.NotFound("Role", id));

        return Result.Success(MapToDto(role));
    }

    public async Task<Result<IReadOnlyList<RoleDto>>> GetAllAsync()
    {
        var roles = await _roles.GetAllWithPermissionsAsync();
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

    public async Task<Result<RoleDto>> UpdateAsync(Guid id, UpdateRoleRequest request)
    {
        var role = await _roles.GetByIdWithPermissionsAsync(id);

        if (role is null)
            return Result.Failure<RoleDto>(Error.NotFound("Role", id));

        if (role.IsSystemRole)
            return Result.Failure<RoleDto>(Error.Validation("Role.SystemRole", "System roles cannot be modified."));

        role.Name = request.Name;
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

    public async Task<Result> AssignPermissionsAsync(Guid roleId, AssignPermissionsRequest request)
    {
        var role = await _roles.GetByIdForPermissionAssignmentAsync(roleId);

        if (role is null)
            return Result.Failure(Error.NotFound("Role", roleId));

        // Remove existing permissions
        _roles.RemoveRolePermissions(role.RolePermissions);

        // Add new permissions
        foreach (var permissionId in request.PermissionIds)
        {
            if (!await _permissions.ExistsByIdAsync(permissionId))
                return Result.Failure(Error.NotFound("Permission", permissionId));

            role.RolePermissions.Add(new AppRolePermission { RoleId = roleId, PermissionId = permissionId });
        }

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
