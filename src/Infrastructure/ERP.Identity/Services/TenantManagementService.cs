using ERP.Application.Abstractions.Identity;
using ERP.Application.Abstractions.Identity.Contracts;
using ERP.Identity.Abstractions.Persistence;
using ERP.Identity.Entities;
using ERP.SharedKernel.Results;
using Microsoft.Extensions.Logging;

namespace ERP.Identity.Services;

/// <summary>
/// TENANT MANAGEMENT SERVICE — CRUD operations on tenant records in Master DB.
///
/// This service handles the DATA side of tenants:
///   - Create/read/update tenant records via <see cref="ITenantRepository"/>
///   - Validate uniqueness of tenant codes
///   - Track user counts
///
/// DATABASE PROVISIONING (DB creation + migration) is handled separately
/// by ITenantProvisioningService. The controller orchestrates both.
///
/// Persistence is committed via <see cref="IMasterUnitOfWork.SaveChangesAsync"/> —
/// the service holds no EF Core references (enforced by architecture test).
/// </summary>
public class TenantManagementService : ITenantManagementService
{
    private readonly ITenantRepository _tenants;
    private readonly IMasterUnitOfWork _uow;
    private readonly ILogger<TenantManagementService> _logger;

    public TenantManagementService(
        ITenantRepository tenants,
        IMasterUnitOfWork uow,
        ILogger<TenantManagementService> logger)
    {
        _tenants = tenants;
        _uow = uow;
        _logger = logger;
    }

    public async Task<Result<TenantResponse>> CreateAsync(CreateTenantRequest request)
    {
        // Validate unique code
        if (await _tenants.ExistsByCodeAsync(request.Code))
            return Result.Failure<TenantResponse>(
                Error.Conflict("Tenant.CodeExists", $"Tenant code '{request.Code}' already exists."));

        // Validate DB provider
        if (request.DbProvider is not ("SqlServer" or "PostgreSql"))
            return Result.Failure<TenantResponse>(
                Error.Validation("Tenant.InvalidProvider", "DbProvider must be 'SqlServer' or 'PostgreSql'."));

        var tenant = new Tenant
        {
            Code = request.Code,
            Name = request.Name,
            ConnectionString = request.ConnectionString,
            DbProvider = request.DbProvider,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        await _tenants.AddAsync(tenant);
        await _uow.SaveChangesAsync();

        _logger.LogInformation("Tenant '{TenantCode}' created with ID {TenantId}", tenant.Code, tenant.Id);

        return Result.Success(MapToResponse(tenant, 0));
    }

    public async Task<Result<TenantResponse>> GetByIdAsync(Guid id)
    {
        var tenant = await _tenants.GetByIdWithUserTenantsAsync(id);

        if (tenant is null)
            return Result.Failure<TenantResponse>(Error.NotFound("Tenant", id));

        return Result.Success(MapToResponse(tenant, tenant.UserTenants.Count));
    }

    public async Task<Result<IReadOnlyList<TenantListResponse>>> GetAllAsync()
    {
        var tenants = await _tenants.GetAllAsync();

        var dtos = tenants
            .Select(t => new TenantListResponse(
                t.Id,
                t.Code,
                t.Name,
                t.DbProvider,
                t.IsActive,
                t.CreatedAt))
            .ToList();

        return Result.Success<IReadOnlyList<TenantListResponse>>(dtos);
    }

    public async Task<Result<TenantResponse>> UpdateAsync(Guid id, UpdateTenantRequest request)
    {
        var tenant = await _tenants.GetByIdWithUserTenantsAsync(id);

        if (tenant is null)
            return Result.Failure<TenantResponse>(Error.NotFound("Tenant", id));

        tenant.Name = request.Name;
        tenant.IsActive = request.IsActive;

        await _uow.SaveChangesAsync();

        _logger.LogInformation("Tenant '{TenantCode}' updated", tenant.Code);

        return Result.Success(MapToResponse(tenant, tenant.UserTenants.Count));
    }

    public async Task<Result> DeactivateAsync(Guid id)
    {
        var tenant = await _tenants.GetByIdAsync(id);
        if (tenant is null)
            return Result.Failure(Error.NotFound("Tenant", id));

        tenant.IsActive = false;
        await _uow.SaveChangesAsync();

        _logger.LogInformation("Tenant '{TenantCode}' deactivated", tenant.Code);
        return Result.Success();
    }

    private static TenantResponse MapToResponse(Tenant tenant, int userCount) => new(
        Id: tenant.Id,
        Code: tenant.Code,
        Name: tenant.Name,
        DbProvider: tenant.DbProvider,
        IsActive: tenant.IsActive,
        CreatedAt: tenant.CreatedAt,
        UserCount: userCount);
}
