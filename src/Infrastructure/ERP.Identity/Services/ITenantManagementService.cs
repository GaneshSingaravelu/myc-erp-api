using ERP.Identity.DTOs;
using ERP.SharedKernel.Results;

namespace ERP.Identity.Services;

/// <summary>
/// TENANT MANAGEMENT — CRUD operations on tenant records in the Master DB.
/// Provisioning (DB creation + migration) is handled separately by ITenantProvisioningService.
/// </summary>
public interface ITenantManagementService
{
    Task<Result<TenantResponse>> CreateAsync(CreateTenantRequest request);
    Task<Result<TenantResponse>> GetByIdAsync(Guid id);
    Task<Result<IReadOnlyList<TenantListResponse>>> GetAllAsync();
    Task<Result<TenantResponse>> UpdateAsync(Guid id, UpdateTenantRequest request);
    Task<Result> DeactivateAsync(Guid id);
}
