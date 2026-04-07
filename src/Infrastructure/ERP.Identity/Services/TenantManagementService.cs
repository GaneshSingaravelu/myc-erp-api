using ERP.Identity.DTOs;
using ERP.Identity.Entities;
using ERP.Persistence.Context;
using ERP.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ERP.Identity.Services;

/// <summary>
/// TENANT MANAGEMENT SERVICE — CRUD operations on tenant records in Master DB.
///
/// This service handles the DATA side of tenants:
///   - Create/read/update tenant records in MasterDbContext
///   - Validate uniqueness of tenant codes
///   - Track user counts
///
/// DATABASE PROVISIONING (DB creation + migration) is handled separately
/// by ITenantProvisioningService. The controller orchestrates both.
/// </summary>
public class TenantManagementService : ITenantManagementService
{
    private readonly MasterDbContext _context;
    private readonly ILogger<TenantManagementService> _logger;

    public TenantManagementService(MasterDbContext context, ILogger<TenantManagementService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<Result<TenantResponse>> CreateAsync(CreateTenantRequest request)
    {
        // Validate unique code
        if (await _context.Set<Tenant>().AnyAsync(t => t.Code == request.Code))
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

        await _context.Set<Tenant>().AddAsync(tenant);
        await _context.SaveChangesAsync();

        _logger.LogInformation("Tenant '{TenantCode}' created with ID {TenantId}", tenant.Code, tenant.Id);

        return Result.Success(MapToResponse(tenant, 0));
    }

    public async Task<Result<TenantResponse>> GetByIdAsync(Guid id)
    {
        var tenant = await _context.Set<Tenant>()
            .Include(t => t.UserTenants)
            .FirstOrDefaultAsync(t => t.Id == id);

        if (tenant is null)
            return Result.Failure<TenantResponse>(Error.NotFound("Tenant", id));

        return Result.Success(MapToResponse(tenant, tenant.UserTenants.Count));
    }

    public async Task<Result<IReadOnlyList<TenantListResponse>>> GetAllAsync()
    {
        var tenants = await _context.Set<Tenant>()
            .OrderBy(t => t.Name)
            .Select(t => new TenantListResponse(
                t.Id,
                t.Code,
                t.Name,
                t.DbProvider,
                t.IsActive,
                t.CreatedAt))
            .ToListAsync();

        return Result.Success<IReadOnlyList<TenantListResponse>>(tenants);
    }

    public async Task<Result<TenantResponse>> UpdateAsync(Guid id, UpdateTenantRequest request)
    {
        var tenant = await _context.Set<Tenant>()
            .Include(t => t.UserTenants)
            .FirstOrDefaultAsync(t => t.Id == id);

        if (tenant is null)
            return Result.Failure<TenantResponse>(Error.NotFound("Tenant", id));

        tenant.Name = request.Name;
        tenant.IsActive = request.IsActive;

        await _context.SaveChangesAsync();

        _logger.LogInformation("Tenant '{TenantCode}' updated", tenant.Code);

        return Result.Success(MapToResponse(tenant, tenant.UserTenants.Count));
    }

    public async Task<Result> DeactivateAsync(Guid id)
    {
        var tenant = await _context.Set<Tenant>().FindAsync(id);
        if (tenant is null)
            return Result.Failure(Error.NotFound("Tenant", id));

        tenant.IsActive = false;
        await _context.SaveChangesAsync();

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
