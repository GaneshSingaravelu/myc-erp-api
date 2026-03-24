using ERP.Identity.DTOs;
using ERP.SharedKernel.Results;

namespace ERP.Identity.Services;

/// <summary>
/// ROLE SERVICE — CRUD operations for role management and permission assignment.
/// </summary>
public interface IRoleService
{
    Task<Result<RoleDto>> GetByIdAsync(Guid id);
    Task<Result<IReadOnlyList<RoleDto>>> GetAllAsync();
    Task<Result<RoleDto>> CreateAsync(CreateRoleRequest request);
    Task<Result<RoleDto>> UpdateAsync(Guid id, UpdateRoleRequest request);
    Task<Result> DeleteAsync(Guid id);
    Task<Result> AssignPermissionsAsync(Guid roleId, AssignPermissionsRequest request);
    Task<Result<IReadOnlyList<PermissionDto>>> GetAllPermissionsAsync();
}
