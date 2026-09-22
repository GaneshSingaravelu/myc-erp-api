using System.Text.Json;
using ERP.Application.Abstractions.Identity.Contracts;
using ERP.Application.Abstractions.MultiTenancy;
using ERP.Identity.Abstractions.Persistence;
using ERP.Identity.Entities;
using ERP.Identity.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ERP.UnitTests.Identity;

/// <summary>
/// S-T3 — PUT /tenants/{id} is confined to the caller's own tenant, and activation state is
/// not editable through it.
///
/// Two separate defects are covered. The first is the same scope hole the read paths had: the
/// action updated whatever id the route carried. The second is subtler and was the more
/// dangerous of the pair — the action assigned <c>tenant.IsActive</c> straight from the
/// request body, making PUT a second deactivation path alongside the DELETE that phase S-T1
/// withdrew. Setting a tenant inactive locks every one of its users out at TenantMiddleware,
/// so closing DELETE while leaving PUT open would have closed nothing.
///
/// As in the read tests, the repository and unit of work are <b>strict</b> mocks: a foreign
/// lookup or an unexpected save throws rather than returning a convenient answer. The
/// ordering property — validation before mutation — is asserted on the tracked entity itself,
/// because "nothing was saved" and "nothing was changed" are different claims and the second
/// is the one that matters when something else shares the unit of work.
/// </summary>
public class TenantManagementService_TenantScopedWriteTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid TenantC = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    private const string OriginalName = "ACME Manufacturing";

    private readonly Mock<ITenantRepository> _tenants = new(MockBehavior.Strict);
    private readonly Mock<IMasterUnitOfWork> _uow = new(MockBehavior.Strict);
    private readonly Mock<ITenantProvider> _tenantProvider = new(MockBehavior.Strict);

    private TenantManagementService Sut() => new(
        _tenants.Object,
        _uow.Object,
        _tenantProvider.Object,
        NullLogger<TenantManagementService>.Instance);

    private void SetCurrentTenant(Guid tenantId) =>
        _tenantProvider
            .Setup(p => p.GetCurrentTenant())
            .Returns(new TenantInfo { Id = tenantId, Identifier = "tenant-" + tenantId, IsActive = true });

    private static Tenant TenantRow(Guid id, bool isActive = true) => new()
    {
        Id = id,
        Code = "ACME",
        Name = OriginalName,
        ConnectionString = "Server=(local);Database=ERP_ACME",
        DbProvider = "SqlServer",
        IsActive = isActive,
        CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    /// <summary>Stage the current tenant in the store and allow exactly one commit.</summary>
    private Tenant GivenCurrentTenant(bool isActive = true)
    {
        var tenant = TenantRow(TenantA, isActive);
        SetCurrentTenant(TenantA);
        _tenants
            .Setup(r => r.GetByIdWithUserTenantsAsync(TenantA, It.IsAny<CancellationToken>()))
            .ReturnsAsync(tenant);
        return tenant;
    }

    private void AllowSave() => _uow.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

    private void AssertNoStoreAccess()
    {
        _tenants.VerifyNoOtherCalls();
        _uow.VerifyNoOtherCalls();
    }

    // ── CASE 1: no activation request ────────────────────────────────────────

    [Fact]
    public async Task Update_WithNullIsActive_RenamesAndLeavesActivationAlone()
    {
        var tenant = GivenCurrentTenant(isActive: true);
        AllowSave();

        var result = await Sut().UpdateAsync(TenantA, new UpdateTenantRequest("Renamed", IsActive: null));

        Assert.True(result.IsSuccess);
        Assert.Equal("Renamed", tenant.Name);
        Assert.True(tenant.IsActive);
        _uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── CASE 2: activation value repeated, not changed ───────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Update_WithMatchingIsActive_RenamesAndLeavesActivationAlone(bool stored)
    {
        // Symmetric on purpose: echoing the stored value carries no activation intent whether
        // the tenant is active or not, and must not block a rename either way.
        var tenant = GivenCurrentTenant(isActive: stored);
        AllowSave();

        var result = await Sut().UpdateAsync(TenantA, new UpdateTenantRequest("Renamed", IsActive: stored));

        Assert.True(result.IsSuccess);
        Assert.Equal("Renamed", tenant.Name);
        Assert.Equal(stored, tenant.IsActive);
        _uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── CASE 3: activation change refused, before anything is touched ────────

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Update_WithDifferentIsActive_IsRefusedBeforeAnyMutation(bool stored, bool requested)
    {
        // Both directions: deactivating an active tenant, and reactivating a disabled one.
        var tenant = GivenCurrentTenant(isActive: stored);
        // No AllowSave(): the unit of work is strict, so any commit throws.

        var result = await Sut().UpdateAsync(
            TenantA,
            new UpdateTenantRequest("Should Not Persist", IsActive: requested));

        Assert.True(result.IsFailure);
        Assert.Equal("Tenant.ActivationImmutable", result.Error.Code);

        // The ordering property: the tracked entity is untouched, not merely unsaved.
        Assert.Equal(OriginalName, tenant.Name);
        Assert.Equal(stored, tenant.IsActive);
        _uow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        _uow.VerifyNoOtherCalls();
    }

    // ── CASE 4 / 5: foreign targets ──────────────────────────────────────────

    [Fact]
    public async Task Update_ForeignTenantThatExists_IsNotFound_AndIsNeverLookedUp()
    {
        SetCurrentTenant(TenantA);

        var result = await Sut().UpdateAsync(TenantB, new UpdateTenantRequest("Renamed", IsActive: null));

        Assert.True(result.IsFailure);
        Assert.Equal("Tenant.NotFound", result.Error.Code);
        AssertNoStoreAccess();
    }

    [Fact]
    public async Task Update_ForeignTenantThatDoesNotExist_IsNotFound_AndIsNeverLookedUp()
    {
        SetCurrentTenant(TenantA);

        var result = await Sut().UpdateAsync(TenantC, new UpdateTenantRequest("Renamed", IsActive: null));

        Assert.True(result.IsFailure);
        Assert.Equal("Tenant.NotFound", result.Error.Code);
        AssertNoStoreAccess();
    }

    [Fact]
    public async Task Update_ForeignTargets_AreAnsweredIdentically()
    {
        // Same classification, same code, same message template. The ids differ because the
        // CALLER supplied them, which discloses nothing.
        SetCurrentTenant(TenantA);
        var request = new UpdateTenantRequest("Renamed", IsActive: null);

        var existing = await Sut().UpdateAsync(TenantB, request);
        var missing = await Sut().UpdateAsync(TenantC, request);

        Assert.Equal(existing.Error.Code, missing.Error.Code);
        Assert.Equal(
            existing.Error.Message.Replace(TenantB.ToString(), "<id>"),
            missing.Error.Message.Replace(TenantC.ToString(), "<id>"));
        AssertNoStoreAccess();
    }

    [Fact]
    public async Task Update_ForeignTenant_IsRefusedEvenWhenItWouldHaveBeenActivationSafe()
    {
        // Scope is checked first: a foreign target is refused before the activation rule is
        // ever considered, so the two refusals cannot be confused for one another.
        SetCurrentTenant(TenantA);

        var result = await Sut().UpdateAsync(TenantB, new UpdateTenantRequest("Renamed", IsActive: true));

        Assert.Equal("Tenant.NotFound", result.Error.Code);
        AssertNoStoreAccess();
    }

    // ── CASE 6: the caller's own tenant is genuinely absent ──────────────────

    [Fact]
    public async Task Update_WhenCurrentTenantIsMissingFromTheStore_IsNotFound_AndWasLookedUp()
    {
        SetCurrentTenant(TenantA);
        _tenants
            .Setup(r => r.GetByIdWithUserTenantsAsync(TenantA, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Tenant?)null);

        var result = await Sut().UpdateAsync(TenantA, new UpdateTenantRequest("Renamed", IsActive: null));

        Assert.True(result.IsFailure);
        Assert.Equal("Tenant.NotFound", result.Error.Code);
        _tenants.Verify(r => r.GetByIdWithUserTenantsAsync(TenantA, It.IsAny<CancellationToken>()), Times.Once);
        _uow.VerifyNoOtherCalls();
    }

    // ── CASE 7: no validated tenant context ──────────────────────────────────

    [Fact]
    public async Task Update_WithNoTenantContext_FailsClosed_WithoutQueryingAnything()
    {
        _tenantProvider
            .Setup(p => p.GetCurrentTenant())
            .Throws(new InvalidOperationException("No tenant context available."));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Sut().UpdateAsync(TenantA, new UpdateTenantRequest("Renamed", IsActive: null)));

        AssertNoStoreAccess();
    }

    // ── PART H: the DTO contract that makes the rule expressible ─────────────
    //
    // `IsActive` is nullable for one reason: to tell "the client said nothing" apart from
    // "the client asked for false". If binding collapsed those, every caller omitting the
    // field would look like a deactivation request and CASE 1 would become unreachable.
    //
    // The API registers `AddControllers()` with no `AddJsonOptions`, so MVC's default
    // System.Text.Json behaviour applies; these assert the record binds as the rule requires.

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void OmittedIsActive_BindsAsNull_NotFalse()
    {
        var request = JsonSerializer.Deserialize<UpdateTenantRequest>("""{"name":"Renamed"}""", Web)!;

        Assert.Equal("Renamed", request.Name);
        Assert.Null(request.IsActive);
        Assert.False(request.IsActive.HasValue);
    }

    [Fact]
    public void ExplicitNullIsActive_BindsAsNull()
    {
        var request = JsonSerializer.Deserialize<UpdateTenantRequest>("""{"name":"Renamed","isActive":null}""", Web)!;

        Assert.Null(request.IsActive);
    }

    [Fact]
    public void ExplicitFalseIsActive_BindsAsFalse_AndIsDistinguishableFromOmission()
    {
        var omitted = JsonSerializer.Deserialize<UpdateTenantRequest>("""{"name":"Renamed"}""", Web)!;
        var explicitFalse = JsonSerializer.Deserialize<UpdateTenantRequest>("""{"name":"Renamed","isActive":false}""", Web)!;

        Assert.False(explicitFalse.IsActive);
        Assert.True(explicitFalse.IsActive.HasValue);
        Assert.NotEqual(omitted.IsActive, explicitFalse.IsActive);
    }

    [Fact]
    public void ExplicitTrueIsActive_BindsAsTrue()
    {
        var request = JsonSerializer.Deserialize<UpdateTenantRequest>("""{"name":"Renamed","isActive":true}""", Web)!;

        Assert.True(request.IsActive);
    }
}
