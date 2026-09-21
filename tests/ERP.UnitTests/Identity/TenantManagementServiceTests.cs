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
        var stored = await Context.Set<Tenant>().FirstAsync(t => t.Code == "ACME");
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

    [Fact]
    public async Task GetAll_OrdersByName()
    {
        await PersistAsync(CreateTestTenant("ZED"));
        await PersistAsync(CreateTestTenant("ACME"));
        await PersistAsync(CreateTestTenant("MID"));

        var result = await _sut.GetAllAsync();

        Assert.True(result.IsSuccess);
        // Tenant.Name is "{code} Manufacturing" → ordered ACME, MID, ZED
        Assert.Equal(new[] { "ACME", "MID", "ZED" }, result.Value.Select(t => t.Code));
    }

    // ── UpdateAsync ──

    [Fact]
    public async Task Update_WhenNotFound_ReturnsNotFound()
    {
        var result = await _sut.UpdateAsync(Guid.NewGuid(), new UpdateTenantRequest("X", true));

        Assert.True(result.IsFailure);
        Assert.Equal("Tenant.NotFound", result.Error.Code);
    }

    [Fact]
    public async Task Update_PersistsNameAndActiveFlag()
    {
        var tenant = await PersistAsync(CreateTestTenant("ACME"));

        var result = await _sut.UpdateAsync(tenant.Id, new UpdateTenantRequest("Acme Renamed", IsActive: false));

        Assert.True(result.IsSuccess);
        var stored = await Context.Set<Tenant>().FirstAsync(t => t.Id == tenant.Id);
        Assert.Equal("Acme Renamed", stored.Name);
        Assert.False(stored.IsActive);
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
