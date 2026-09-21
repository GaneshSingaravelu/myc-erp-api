using ERP.Application.Abstractions.MultiTenancy;
using ERP.Identity.Abstractions.Persistence;
using ERP.Identity.Entities;
using ERP.Identity.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ERP.UnitTests.Identity;

/// <summary>
/// S-T2B — GET /tenants/{id} is confined to the tenant the caller is signed in to.
///
/// Before this phase the service looked up whatever id the route carried, so any tenant's
/// Admin could read any other tenant's record, including its user count. The remediation is
/// a scope check that runs <b>before</b> the repository is consulted.
///
/// That ordering is the whole security property, so these tests are built to prove it rather
/// than to prove the returned value. The repository is a <b>strict</b> mock with no default
/// behaviour: a lookup for a foreign id does not return the wrong answer, it throws and fails
/// the test. The unit of work is strict too, so a read path that writes anything is caught.
///
/// The tenant context comes from the same <c>ITenantProvider</c> seam the production code
/// reads, mirroring <c>RoleService</c> and <c>UserService</c>. These tests deliberately do NOT
/// derive from <c>IdentityServiceTestBase</c>: that base supplies a real <c>TenantRepository</c>
/// over a live context, which can prove an outcome but cannot prove that no query was issued.
/// </summary>
public class TenantManagementService_TenantScopedReadTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid TenantC = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    private readonly Mock<ITenantRepository> _tenants = new(MockBehavior.Strict);
    private readonly Mock<IMasterUnitOfWork> _uow = new(MockBehavior.Strict);
    private readonly Mock<ITenantProvider> _tenantProvider = new(MockBehavior.Strict);

    private TenantManagementService Sut() => new(
        _tenants.Object,
        _uow.Object,
        _tenantProvider.Object,
        NullLogger<TenantManagementService>.Instance);

    /// <summary>Point the validated tenant context at a specific tenant.</summary>
    private void SetCurrentTenant(Guid tenantId) =>
        _tenantProvider
            .Setup(p => p.GetCurrentTenant())
            .Returns(new TenantInfo { Id = tenantId, Identifier = "tenant-" + tenantId, IsActive = true });

    private static Tenant TenantRow(Guid id, string code, int members = 0)
    {
        var tenant = new Tenant
        {
            Id = id,
            Code = code,
            Name = code + " Manufacturing",
            ConnectionString = "Server=(local);Database=ERP_" + code,
            DbProvider = "SqlServer",
            IsActive = true,
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };

        for (var i = 0; i < members; i++)
        {
            tenant.UserTenants.Add(new UserTenant { UserId = Guid.NewGuid(), TenantId = id, IsActive = true });
        }

        return tenant;
    }

    /// <summary>Nothing was read and nothing was written.</summary>
    private void AssertNoStoreAccess()
    {
        _tenants.VerifyNoOtherCalls();
        _uow.VerifyNoOtherCalls();
    }

    // ── CASE 1: the caller's own tenant ──────────────────────────────────────

    [Fact]
    public async Task CurrentTenant_IsReturnedWithItsUserCount()
    {
        SetCurrentTenant(TenantA);
        _tenants
            .Setup(r => r.GetByIdWithUserTenantsAsync(TenantA, It.IsAny<CancellationToken>()))
            .ReturnsAsync(TenantRow(TenantA, "ACME", members: 2));

        var result = await Sut().GetByIdAsync(TenantA);

        Assert.True(result.IsSuccess);
        Assert.Equal(TenantA, result.Value.Id);
        Assert.Equal("ACME", result.Value.Code);
        Assert.Equal("ACME Manufacturing", result.Value.Name);
        Assert.Equal(2, result.Value.UserCount);

        _tenants.Verify(r => r.GetByIdWithUserTenantsAsync(TenantA, It.IsAny<CancellationToken>()), Times.Once);
        _uow.VerifyNoOtherCalls();
    }

    // ── CASE 2: a foreign tenant that exists ─────────────────────────────────

    [Fact]
    public async Task ForeignTenantThatExists_IsNotFound_AndIsNeverLookedUp()
    {
        // TenantB is a real tenant in the world. The point is that the service never asks:
        // the strict mock has no setup for it, so any lookup throws rather than answering.
        SetCurrentTenant(TenantA);

        var result = await Sut().GetByIdAsync(TenantB);

        Assert.True(result.IsFailure);
        Assert.Equal("Tenant.NotFound", result.Error.Code);
        AssertNoStoreAccess();
    }

    // ── CASE 3: a foreign tenant that does not exist ─────────────────────────

    [Fact]
    public async Task ForeignTenantThatDoesNotExist_IsNotFound_AndIsNeverLookedUp()
    {
        SetCurrentTenant(TenantA);

        var result = await Sut().GetByIdAsync(TenantC);

        Assert.True(result.IsFailure);
        Assert.Equal("Tenant.NotFound", result.Error.Code);
        AssertNoStoreAccess();
    }

    [Fact]
    public async Task ExistingAndNonExistentForeignTenants_AreAnsweredIdentically()
    {
        // Same classification, same error code, same message template — and, decisively, the
        // same amount of work. The two messages differ only in the id the CALLER supplied,
        // which tells the caller nothing they did not already know.
        SetCurrentTenant(TenantA);

        var existing = await Sut().GetByIdAsync(TenantB);
        var missing = await Sut().GetByIdAsync(TenantC);

        Assert.Equal(existing.IsFailure, missing.IsFailure);
        Assert.Equal(existing.Error.Code, missing.Error.Code);
        Assert.Equal(
            existing.Error.Message.Replace(TenantB.ToString(), "<id>"),
            missing.Error.Message.Replace(TenantC.ToString(), "<id>"));
        AssertNoStoreAccess();
    }

    [Fact]
    public async Task TheSameForeignId_IsAnsweredIdenticallyWhetherOrNotItExists()
    {
        // The literal-equality form of the same claim: one id, two worlds. The service
        // cannot behave differently between them because it never consults either one.
        SetCurrentTenant(TenantA);

        var first = await Sut().GetByIdAsync(TenantB);

        // A second service instance whose store WOULD have resolved TenantB, had it asked.
        var wouldResolve = new Mock<ITenantRepository>(MockBehavior.Strict);
        wouldResolve
            .Setup(r => r.GetByIdWithUserTenantsAsync(TenantB, It.IsAny<CancellationToken>()))
            .ReturnsAsync(TenantRow(TenantB, "BETA", members: 7));
        var second = await new TenantManagementService(
            wouldResolve.Object,
            _uow.Object,
            _tenantProvider.Object,
            NullLogger<TenantManagementService>.Instance).GetByIdAsync(TenantB);

        Assert.Equal(first.Error.Code, second.Error.Code);
        Assert.Equal(first.Error.Message, second.Error.Message);
        wouldResolve.VerifyNoOtherCalls();
    }

    // ── CASE 4: the caller's own tenant is genuinely absent ──────────────────

    [Fact]
    public async Task CurrentTenantMissingFromStore_IsNotFound_AndWasLookedUp()
    {
        // The data-miss branch still exists and is still reachable — the guard did not
        // swallow it. This is the coverage the pre-existing test would have lost.
        SetCurrentTenant(TenantA);
        _tenants
            .Setup(r => r.GetByIdWithUserTenantsAsync(TenantA, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Tenant?)null);

        var result = await Sut().GetByIdAsync(TenantA);

        Assert.True(result.IsFailure);
        Assert.Equal("Tenant.NotFound", result.Error.Code);
        _tenants.Verify(r => r.GetByIdWithUserTenantsAsync(TenantA, It.IsAny<CancellationToken>()), Times.Once);
        _uow.VerifyNoOtherCalls();
    }

    // ── CASE 5: Guid.Empty ───────────────────────────────────────────────────

    [Fact]
    public async Task EmptyGuid_IsForeignToARealTenant_AndIsNeverLookedUp()
    {
        // Guid.Empty is not a tenant id any validated context can carry, so it is simply
        // foreign. Worth pinning because it is the default value an unset context would
        // report — see the fail-closed test below for why that state cannot arise.
        SetCurrentTenant(TenantA);

        var result = await Sut().GetByIdAsync(Guid.Empty);

        Assert.True(result.IsFailure);
        Assert.Equal("Tenant.NotFound", result.Error.Code);
        AssertNoStoreAccess();
    }

    // ── CASE 6: no validated tenant context ──────────────────────────────────

    [Fact]
    public async Task MissingTenantContext_FailsClosed_WithoutQueryingAnything()
    {
        // HttpContextTenantProvider throws when TenantMiddleware has not populated
        // Items["CurrentTenant"]. The service must let that surface rather than fall back to
        // an unscoped read — a fallback would silently restore the vulnerability whenever
        // tenant resolution was bypassed, which is exactly the state S-T2A just removed.
        _tenantProvider
            .Setup(p => p.GetCurrentTenant())
            .Throws(new InvalidOperationException("No tenant context available."));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Sut().GetByIdAsync(TenantB));

        AssertNoStoreAccess();
    }

    // ── The guard is read-scoped only ────────────────────────────────────────

    [Fact]
    public async Task ReadingDoesNotWrite()
    {
        SetCurrentTenant(TenantA);
        _tenants
            .Setup(r => r.GetByIdWithUserTenantsAsync(TenantA, It.IsAny<CancellationToken>()))
            .ReturnsAsync(TenantRow(TenantA, "ACME"));

        await Sut().GetByIdAsync(TenantA);

        // Strict mock: SaveChangesAsync has no setup, so any commit would already have thrown.
        _uow.VerifyNoOtherCalls();
    }
}
