using ERP.Application.Abstractions.Identity;
using ERP.Application.Abstractions.Identity.Contracts;
using ERP.Application.Abstractions.MultiTenancy;
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
    private readonly ITenantProvider _tenantProvider;
    private readonly ILogger<TenantManagementService> _logger;

    public TenantManagementService(
        ITenantRepository tenants,
        IMasterUnitOfWork uow,
        ITenantProvider tenantProvider,
        ILogger<TenantManagementService> logger)
    {
        _tenants = tenants;
        _uow = uow;
        _tenantProvider = tenantProvider;
        _logger = logger;
    }

    /// <summary>
    /// The tenant this request is authorised for — the ONLY tenant identity this service
    /// may use for authorization decisions.
    ///
    /// <para>It comes from <see cref="ITenantProvider"/>, which reads the value
    /// <c>TenantMiddleware</c> placed in <c>HttpContext.Items["CurrentTenant"]</c> after it
    /// cross-checked the <c>X-Tenant-Id</c> header against the caller's <c>tenant_id</c>
    /// claim, so the two are already reconciled by the time this runs. The Tenants routes
    /// only reach that state from phase S-T2A onwards; before it they bypassed the
    /// middleware entirely and no validated tenant existed here at all.</para>
    ///
    /// <para><b>Never read <c>X-Tenant-Id</c> or the <c>tenant_id</c> claim directly in this
    /// service.</b> Either one alone is a client-supplied value; only the reconciled result
    /// is an authorization input. A tenant id arriving in a route or body is not an
    /// authorization input either — it is the thing being authorised.</para>
    ///
    /// <para>Fails closed: <see cref="ITenantProvider.GetCurrentTenant"/> throws when no
    /// validated tenant is present, so code that reaches this without tenant resolution
    /// raises an error instead of running an unscoped query.</para>
    ///
    /// <para>Consulted by <see cref="GetByIdAsync"/> (phase S-T2B). <see cref="GetAllAsync"/>
    /// and <see cref="UpdateAsync"/> still run unscoped and are addressed in later phases.</para>
    /// </summary>
    private Guid CurrentTenantId => _tenantProvider.GetCurrentTenant().Id;

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

    /// <summary>
    /// Read one tenant, confined to the tenant the caller is signed in to.
    ///
    /// <para>The scope check runs <b>before</b> the repository is touched. That ordering is
    /// the security property, not a micro-optimisation: a foreign id is never looked up, so
    /// this endpoint cannot report whether it names a real tenant. An id belonging to
    /// another tenant and an id belonging to nobody are answered from the same branch,
    /// without consulting stored data, so they are indistinguishable by result, by content
    /// and by the work performed.</para>
    ///
    /// <para>The second branch is the genuine data miss — the caller's own tenant is not in
    /// the store — and is deliberately reported identically.</para>
    /// </summary>
    public async Task<Result<TenantResponse>> GetByIdAsync(Guid id)
    {
        if (id != CurrentTenantId)
            return Result.Failure<TenantResponse>(Error.NotFound("Tenant", id));

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
