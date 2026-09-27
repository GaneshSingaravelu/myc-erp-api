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
    /// <para>Consulted by <see cref="GetByIdAsync"/> (phase S-T2B),
    /// <see cref="GetAllAsync"/> (phase S-T2C) and <see cref="UpdateAsync"/> (phase S-T3).
    /// <see cref="CreateAsync"/> and <see cref="DeactivateAsync"/> are unreachable over HTTP
    /// after phase S-T1 and are left unscoped as dead primitives.</para>
    /// </summary>
    private Guid CurrentTenantId => _tenantProvider.GetCurrentTenant().Id;

    /// <summary>
    /// Creates a tenant record.
    ///
    /// <para><b>Unreachable over HTTP</b> since phase S-T1 withdrew the create route; it
    /// survives as a dead primitive. Its behaviour is unchanged here apart from the tenant
    /// code being canonicalized, which matters precisely because a dead primitive must not be
    /// unsafe if it is ever re-exposed: an uppercase code persisted raw would be a second
    /// tenant on PostgreSQL and an unresolvable one on either engine.</para>
    /// </summary>
    public async Task<Result<TenantResponse>> CreateAsync(CreateTenantRequest request)
    {
        // The canonical form is both what the duplicate check compares and what gets stored,
        // so 'ACME' collides with a stored 'acme' instead of being accepted alongside it.
        // The rejection message still echoes the code as supplied — the caller sent that, and
        // reporting a silently rewritten value back to them would be confusing.
        var code = TenantCode.Normalize(request.Code);

        // Validate unique code
        if (await _tenants.ExistsByCodeAsync(code))
            return Result.Failure<TenantResponse>(
                Error.Conflict("Tenant.CodeExists", $"Tenant code '{request.Code}' already exists."));

        // Validate DB provider
        if (request.DbProvider is not ("SqlServer" or "PostgreSql"))
            return Result.Failure<TenantResponse>(
                Error.Validation("Tenant.InvalidProvider", "DbProvider must be 'SqlServer' or 'PostgreSql'."));

        var tenant = new Tenant
        {
            Code = code,
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

    /// <summary>
    /// List the tenants visible to the caller — which, under the authorization model this
    /// application actually has, is exactly the one tenant they are signed in to.
    ///
    /// <para>This previously returned every tenant on the platform, so any tenant's Admin
    /// could enumerate the entire customer list. The fix is not to filter that list: it is
    /// never requested. Only <c>CurrentTenantId</c> is queried, so no other tenant's row is
    /// read at all — there is nothing to leak through timing, counts or error shape.</para>
    ///
    /// <para>The endpoint stays a collection and keeps its contract; it simply yields zero or
    /// one item. Zero is a <b>success</b>, not an error. <c>TenantMiddleware</c> has already
    /// resolved and validated the tenant by the time this runs, so a missing row means the
    /// store and the middleware's 30-minute cache have diverged — a tenant deleted mid-window.
    /// Reporting "nothing visible" is honest and discloses nothing, whereas failing would turn
    /// a stale-cache anomaly into a 400. A missing tenant <i>context</i> is a different matter
    /// and still fails closed through <see cref="ITenantProvider"/>.</para>
    ///
    /// <para>Ordering is no longer meaningful here at zero or one item. The repository's
    /// <c>GetAllAsync</c> still orders by name for any future caller; this one no longer calls it.</para>
    /// </summary>
    public async Task<Result<IReadOnlyList<TenantListResponse>>> GetAllAsync()
    {
        var tenant = await _tenants.GetByIdAsync(CurrentTenantId);

        var dtos = new List<TenantListResponse>();

        if (tenant is not null)
        {
            dtos.Add(new TenantListResponse(
                tenant.Id,
                tenant.Code,
                tenant.Name,
                tenant.DbProvider,
                tenant.IsActive,
                tenant.CreatedAt));
        }

        return Result.Success<IReadOnlyList<TenantListResponse>>(dtos);
    }

    /// <summary>
    /// Update the caller's own tenant. Renaming is permitted; activating or deactivating is not.
    ///
    /// <para><b>Scope.</b> The target must be the tenant the caller is signed in to, and the
    /// check runs before the repository is touched — so a foreign id is never looked up and
    /// cannot be distinguished from one that names nothing.</para>
    ///
    /// <para><b>Activation is immutable here.</b> This action used to assign
    /// <c>tenant.IsActive</c> straight from the request, which made it a second, quieter
    /// deactivation path alongside the DELETE that phase S-T1 withdrew: setting a tenant
    /// inactive locks every one of its users out at the middleware. Disabling a tenant is a
    /// platform operation and this application has no platform-administration role, so the
    /// capability is refused rather than scoped.</para>
    ///
    /// <para>The refusal is explicit, never silent: a request asking for a different
    /// activation state fails with <c>Tenant.ActivationImmutable</c> so the caller cannot
    /// believe a deactivation succeeded. A request that omits the field, or repeats the value
    /// already stored, carries no activation intent and is allowed through — but even then
    /// <c>IsActive</c> is never assigned, so no code path here can change it.</para>
    ///
    /// <para><b>Order matters.</b> Validation completes before <c>Name</c> is touched, so a
    /// refused request leaves the tracked entity exactly as it was found and never reaches
    /// <c>SaveChangesAsync</c>. Assigning first and validating afterwards would leave a
    /// mutated entity in the unit of work for whatever saved next.</para>
    /// </summary>
    public async Task<Result<TenantResponse>> UpdateAsync(Guid id, UpdateTenantRequest request)
    {
        if (id != CurrentTenantId)
            return Result.Failure<TenantResponse>(Error.NotFound("Tenant", id));

        var tenant = await _tenants.GetByIdWithUserTenantsAsync(id);

        if (tenant is null)
            return Result.Failure<TenantResponse>(Error.NotFound("Tenant", id));

        if (request.IsActive.HasValue && request.IsActive.Value != tenant.IsActive)
            return Result.Failure<TenantResponse>(Error.Validation(
                "Tenant.ActivationImmutable",
                "A tenant's active state cannot be changed through this endpoint."));

        tenant.Name = request.Name;

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
