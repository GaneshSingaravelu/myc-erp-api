using ERP.Identity.DTOs;
using ERP.Identity.Entities;
using ERP.Persistence.Context;
using ERP.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace ERP.Identity.Services;

public class RoleService : IRoleService
{
    private readonly MasterDbContext _context;

    public RoleService(MasterDbContext context)
    {
        _context = context;
    }

    public async Task<Result<RoleDto>> GetByIdAsync(Guid id)
    {
        var role = await _context.Set<AppRole>()
            .Include(r => r.RolePermissions).ThenInclude(rp => rp.Permission)
            .FirstOrDefaultAsync(r => r.Id == id);

        if (role is null)
            return Result.Failure<RoleDto>(Error.NotFound("Role", id));

        return Result.Success(MapToDto(role));
    }

    public async Task<Result<IReadOnlyList<RoleDto>>> GetAllAsync()
    {
        var roles = await _context.Set<AppRole>()
            .Include(r => r.RolePermissions).ThenInclude(rp => rp.Permission)
            .OrderBy(r => r.Name)
            .ToListAsync();

        return Result.Success<IReadOnlyList<RoleDto>>(roles.Select(MapToDto).ToList());
    }

    public async Task<Result<RoleDto>> CreateAsync(CreateRoleRequest request)
    {
        if (await _context.Set<AppRole>().AnyAsync(r => r.Name == request.Name))
            return Result.Failure<RoleDto>(Error.Conflict("Role.NameExists", $"Role '{request.Name}' already exists."));

        var role = new AppRole
        {
            Name = request.Name,
            Description = request.Description,
            IsSystemRole = false
        };

        await _context.Set<AppRole>().AddAsync(role);
        await _context.SaveChangesAsync();

        return Result.Success(MapToDto(role));
    }

    public async Task<Result<RoleDto>> UpdateAsync(Guid id, UpdateRoleRequest request)
    {
        var role = await _context.Set<AppRole>()
            .Include(r => r.RolePermissions).ThenInclude(rp => rp.Permission)
            .FirstOrDefaultAsync(r => r.Id == id);

        if (role is null)
            return Result.Failure<RoleDto>(Error.NotFound("Role", id));

        if (role.IsSystemRole)
            return Result.Failure<RoleDto>(Error.Validation("Role.SystemRole", "System roles cannot be modified."));

        role.Name = request.Name;
        role.Description = request.Description;
        await _context.SaveChangesAsync();

        return Result.Success(MapToDto(role));
    }

    public async Task<Result> DeleteAsync(Guid id)
    {
        var role = await _context.Set<AppRole>().FindAsync(id);
        if (role is null)
            return Result.Failure(Error.NotFound("Role", id));

        if (role.IsSystemRole)
            return Result.Failure(Error.Validation("Role.SystemRole", "System roles cannot be deleted."));

        _context.Set<AppRole>().Remove(role);
        await _context.SaveChangesAsync();

        return Result.Success();
    }

    public async Task<Result> AssignPermissionsAsync(Guid roleId, AssignPermissionsRequest request)
    {
        var role = await _context.Set<AppRole>()
            .Include(r => r.RolePermissions)
            .FirstOrDefaultAsync(r => r.Id == roleId);

        if (role is null)
            return Result.Failure(Error.NotFound("Role", roleId));

        // Remove existing permissions
        _context.Set<AppRolePermission>().RemoveRange(role.RolePermissions);

        // Add new permissions
        foreach (var permissionId in request.PermissionIds)
        {
            var exists = await _context.Set<Permission>().AnyAsync(p => p.Id == permissionId);
            if (!exists)
                return Result.Failure(Error.NotFound("Permission", permissionId));

            role.RolePermissions.Add(new AppRolePermission { RoleId = roleId, PermissionId = permissionId });
        }

        await _context.SaveChangesAsync();
        return Result.Success();
    }

    public async Task<Result<IReadOnlyList<PermissionDto>>> GetAllPermissionsAsync()
    {
        var permissions = await _context.Set<Permission>()
            .OrderBy(p => p.Category).ThenBy(p => p.Code)
            .Select(p => new PermissionDto(p.Id, p.Code, p.Name, p.Category))
            .ToListAsync();

        return Result.Success<IReadOnlyList<PermissionDto>>(permissions);
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
