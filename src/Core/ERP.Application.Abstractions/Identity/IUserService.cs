using ERP.Application.Abstractions.Identity.Contracts;
using ERP.SharedKernel.Results;

namespace ERP.Application.Abstractions.Identity;

/// <summary>
/// USER SERVICE — CRUD operations for user management.
/// </summary>
public interface IUserService
{
    Task<Result<UserDto>> GetByIdAsync(Guid id);
    Task<Result<IReadOnlyList<UserDto>>> GetAllAsync();
    Task<Result<UserDto>> CreateAsync(CreateUserRequest request);
    Task<Result<UserDto>> UpdateAsync(Guid id, UpdateUserRequest request);
    Task<Result> DeleteAsync(Guid id);
    Task<Result> AssignRolesAsync(Guid userId, AssignRolesRequest request);
    Task<Result<IReadOnlyList<UserTenantDto>>> GetUserTenantsAsync(Guid userId);
    Task<Result> AssignTenantsAsync(Guid userId, AssignTenantsRequest request);
}
