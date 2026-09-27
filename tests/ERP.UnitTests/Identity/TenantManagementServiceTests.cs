using ERP.Application.Abstractions.Identity.Contracts;
using ERP.Identity.Entities;
using ERP.Identity.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP.UnitTests.Identity;

/// <summary>
/// Unit tests for TenantManagementService — the data-side of tenant CRUD.
/// (DB provisioning is a separate concern owned by ITenantProvisioningService.)
/// </summary>
public class TenantManagementServiceTests : IdentityServiceTestBase
{
    private readonly TenantManagementService _sut;

    public TenantManagementServiceTests()
    {
        // S-T2B added ITenantProvider so GetByIdAsync can scope to the caller's tenant.
        // TenantProvider comes from the base and reports Guid.Empty until a test calls
        // SetCurrentTenant, so any test exercising GetByIdAsync must align the context.
        _sut = new TenantManagementService(
            Tenants,
            UnitOfWork,
            TenantProvider,
            NullLogger<TenantManagementService>.Instance);
    }

    // ── CreateAsync ──

    [Fact]
    public async Task Create_WhenCodeExists_ReturnsConflict()
    {
        await PersistAsync(CreateTestTenant("ACME"));

        var request = new CreateTenantRequest("ACME", "Acme Industries", "Server=.;Database=ERP_ACME", "SqlServer");
        var result = await _sut.CreateAsync(request);

        Assert.True(result.IsFailure);
        Assert.Equal("Tenant.CodeExists", result.Error.Code);
    }

    [Fact]
    public async Task Create_WhenInvalidProvider_ReturnsValidationFailure()
    {
        var request = new CreateTenantRequest("NEW", "New Tenant", "conn", "MongoDB");

        var result = await _sut.CreateAsync(request);

        Assert.True(result.IsFailure);
        Assert.Equal("Tenant.InvalidProvider", result.Error.Code);
    }

    [Theory]
    [InlineData("SqlServer")]
    [InlineData("PostgreSql")]
    public async Task Create_AcceptsKnownProviders(string provider)
    {
        var request = new CreateTenantRequest($"NEW_{provider}", "Name", "conn", provider);

        var result = await _sut.CreateAsync(request);

        Assert.True(result.IsSuccess);
        Assert.Equal(provider, result.Value.DbProvider);
        Assert.True(result.Value.IsActive);
        Assert.Equal(0, result.Value.UserCount);
    }

    [Fact]
    public async Task Create_PersistsTenantWithProvidedFields()
    {
        var request = new CreateTenantRequest("ACME", "Acme Industries", "Server=.;Database=ERP_ACME", "SqlServer");

        var result = await _sut.CreateAsync(request);

        Assert.True(result.IsSuccess);
        // The request said "ACME"; the stored code is canonical. This is a write-side
        // behaviour assertion, not a cosmetic one: a raw "ACME" row would be unresolvable
        // through the (normalizing) lookup path on a case-sensitive database.
        var stored = await Context.Set<Tenant>().FirstAsync(t => t.Code == "acme");
        Assert.Equal("Acme Industries", stored.Name);
        Assert.Equal("Server=.;Database=ERP_ACME", stored.ConnectionString);
    }

    // ── GetByIdAsync ──

    [Fact]
    public async Task GetById_WhenNotFound_ReturnsNotFound()
    {
        // Original intent: the repository has no row for this id. S-T2B added a scope guard
        // that runs first, so without aligning the tenant context this would start passing
        // because the id looked foreign — silently replacing the data-miss branch with the
        // authorization branch. Pointing the current tenant AT the requested id keeps the
        // guard satisfied and preserves the genuine repository-miss coverage.
        var missingId = Guid.NewGuid();
        SetCurrentTenant(missingId);

        var result = await _sut.GetByIdAsync(missingId);

        Assert.True(result.IsFailure);
        Assert.Equal("Tenant.NotFound", result.Error.Code);
    }

    [Fact]
    public async Task GetById_ReturnsUserCount()
    {
        var tenant = await PersistAsync(CreateTestTenant("ACME"));
        // S-T2B: reading a tenant is now confined to the caller's own tenant, so the test
        // context must be that tenant. The assertion below is unchanged.
        SetCurrentTenant(tenant.Id);
        var user1 = await PersistAsync(CreateTestUser("u1"));
        var user2 = await PersistAsync(CreateTestUser("u2"));
        await PersistAsync(new UserTenant { UserId = user1.Id, TenantId = tenant.Id, IsActive = true });
        await PersistAsync(new UserTenant { UserId = user2.Id, TenantId = tenant.Id, IsActive = true });

        var result = await _sut.GetByIdAsync(tenant.Id);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.UserCount);
    }

    // ── GetAllAsync ──
    //
    // `GetAll_OrdersByName` was retired in phase S-T2C. It persisted three tenants and
    // asserted they came back ordered ACME, MID, ZED — a contract that only existed while
    // the service returned every tenant on the platform. GetAllAsync is now confined to the
    // caller's own tenant and yields zero or one item, so ordering several tenants is no
    // longer part of this service's behaviour and the assertion had nothing left to protect.
    //
    // It was NOT rewritten to expect one item: that would have kept the name and the shape
    // while silently swapping what the test proves. The real contract now lives in
    // `TenantManagementService_TenantScopedReadTests`, which proves the list is scoped AND
    // that the platform-wide repository read is never issued.
    //
    // TenantRepository.GetAllAsync still orders by name but has no production caller left;
    // it is reported as a dead primitive for separate disposition and deliberately not
    // covered here, since coverage of dead behaviour is not worth having.

    // ── UpdateAsync ──

    [Fact]
    public async Task Update_WhenNotFound_ReturnsNotFound()
    {
        // As for GetById, the current tenant is pointed AT the requested id so the S-T3 scope
        // guard is satisfied and this still exercises the repository miss, not the guard.
        var missingId = Guid.NewGuid();
        SetCurrentTenant(missingId);

        var result = await _sut.UpdateAsync(missingId, new UpdateTenantRequest("X", true));

        Assert.True(result.IsFailure);
        Assert.Equal("Tenant.NotFound", result.Error.Code);
    }

    // `Update_PersistsNameAndActiveFlag` was retired in phase S-T3. It renamed a tenant AND
    // flipped IsActive to false, asserting both were persisted — a contract S-T3 deliberately
    // forbids, since deactivation locks every user of the tenant out and is refused here. It
    // was split rather than edited: dropping the IsActive assertion would have left a test
    // whose name still promised coverage it no longer had. The two halves follow.

    [Fact]
    public async Task Update_PersistsName_WhenNoActivationChangeIsRequested()
    {
        var tenant = await PersistAsync(CreateTestTenant("ACME"));
        SetCurrentTenant(tenant.Id);

        var result = await _sut.UpdateAsync(tenant.Id, new UpdateTenantRequest("Acme Renamed", IsActive: null));

        Assert.True(result.IsSuccess);
        var stored = await Context.Set<Tenant>().FirstAsync(t => t.Id == tenant.Id);
        Assert.Equal("Acme Renamed", stored.Name);
        Assert.True(stored.IsActive);
    }

    [Fact]
    public async Task Update_RefusesAnActivationChange_AndPersistsNothing()
    {
        var tenant = await PersistAsync(CreateTestTenant("ACME"));
        SetCurrentTenant(tenant.Id);

        var result = await _sut.UpdateAsync(
            tenant.Id,
            new UpdateTenantRequest("Should Not Persist", IsActive: false));

        Assert.True(result.IsFailure);
        Assert.Equal("Tenant.ActivationImmutable", result.Error.Code);

        // Read back from the store: neither field moved, so nothing was saved.
        var stored = await Context.Set<Tenant>().AsNoTracking().FirstAsync(t => t.Id == tenant.Id);
        Assert.Equal("ACME Manufacturing", stored.Name);
        Assert.True(stored.IsActive);
    }

    // ── DeactivateAsync ──

    [Fact]
    public async Task Deactivate_WhenNotFound_ReturnsNotFound()
    {
        var result = await _sut.DeactivateAsync(Guid.NewGuid());

        Assert.True(result.IsFailure);
        Assert.Equal("Tenant.NotFound", result.Error.Code);
    }

    [Fact]
    public async Task Deactivate_FlipsIsActiveWithoutSoftDelete()
    {
        var tenant = await PersistAsync(CreateTestTenant("ACME"));

        var result = await _sut.DeactivateAsync(tenant.Id);

        Assert.True(result.IsSuccess);
        // Deactivation is just a flag flip — the row stays in place (Tenant inherits
        // BaseEntity, not AuditableEntity, so there's no IsDeleted to assert against).
        var stored = await Context.Set<Tenant>().FirstAsync(t => t.Id == tenant.Id);
        Assert.False(stored.IsActive);
    }
}
