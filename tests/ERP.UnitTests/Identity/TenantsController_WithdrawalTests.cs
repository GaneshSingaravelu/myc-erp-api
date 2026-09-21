using System.Reflection;
using ERP.Api.Controllers.v1;
using ERP.Application.Abstractions.Identity;
using ERP.Application.Abstractions.MultiTenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ERP.UnitTests.Identity;

/// <summary>
/// S-T1 — withdrawal of the three PLATFORM operations on TenantsController:
/// POST /tenants, DELETE /tenants/{id} and POST /tenants/{id}/migrate.
///
/// These are not tenant-local operations. Creating a tenant makes a record no tenant owns
/// and dials a caller-supplied database host; deactivating one locks every user of that
/// tenant out; migrating one reads that tenant's connection string and runs schema changes
/// against its database. "Admin" in this application means Admin *of the selected tenant* —
/// roles are scoped to the login tenant through UserTenants — so no principal holds the
/// authority these actions assume. They are withdrawn rather than scoped, because scoping
/// them would require inventing a platform-administration model that does not exist.
///
/// Two properties are under test.
///
/// 1. The refusal is real: the service layer is never reached. TenantManagementService
///    .DeactivateAsync and the provisioning service are still unsafe primitives — they act
///    on a raw id with no tenant check — so the statement that matters is that nothing can
///    reach them through HTTP any more. Both mocks are strict, so ANY call fails the test,
///    which is stronger than verifying one method was not called.
///
/// 2. The answer is a constant. If a withdrawn action looked its target up before refusing,
///    it would still tell a caller whether an id names a real tenant — turning a withdrawn
///    capability into an existence oracle. Create takes no bound body at all, so its answer
///    cannot vary with the payload either.
/// </summary>
public class TenantsController_WithdrawalTests
{
    private const string CreateMessage = "Creating tenants is not supported.";
    private const string DeactivateMessage = "Deactivating tenants is not supported.";
    private const string MigrateMessage = "Tenant database migration is not supported.";

    private readonly Mock<ITenantManagementService> _tenantService = new(MockBehavior.Strict);
    private readonly Mock<ITenantProvisioningService> _provisioningService = new(MockBehavior.Strict);
    private readonly TenantsController _sut;

    public TenantsController_WithdrawalTests()
    {
        _sut = new TenantsController(
            _tenantService.Object,
            _provisioningService.Object,
            NullLogger<TenantsController>.Instance);
    }

    /// <summary>Reads the anonymous response body's `error` member.</summary>
    private static string? ErrorOf(ObjectResult result)
        => result.Value?.GetType().GetProperty("error")?.GetValue(result.Value) as string;

    private void AssertNothingWasCalled()
    {
        _tenantService.VerifyNoOtherCalls();
        _provisioningService.VerifyNoOtherCalls();
    }

    /// <summary>
    /// Stands in for: this tenant, another tenant, and an id naming nothing at all. Since no
    /// lookup happens, none of those can be distinguished — which is the point.
    /// </summary>
    private static readonly Guid[] Targets = [Guid.NewGuid(), Guid.NewGuid(), Guid.Empty];

    public static TheoryData<Guid> TargetIds()
    {
        var data = new TheoryData<Guid>();
        foreach (var id in Targets)
        {
            data.Add(id);
        }

        return data;
    }

    // ── POST /tenants ────────────────────────────────────────────────────────

    [Fact]
    public void Create_ReturnsMethodNotAllowed()
    {
        var result = Assert.IsType<ObjectResult>(_sut.Create());

        Assert.Equal(405, result.StatusCode);
    }

    [Fact]
    public void Create_ReturnsTheExactWithdrawalMessage()
    {
        var result = Assert.IsType<ObjectResult>(_sut.Create());

        Assert.Equal(CreateMessage, ErrorOf(result));
    }

    [Fact]
    public void Create_NeverTouchesTheTenantOrProvisioningService()
    {
        _sut.Create();

        AssertNothingWasCalled();
    }

    [Fact]
    public void Create_BindsNoRequestBody()
    {
        // The action takes no parameter, so no CreateTenantRequest is bound and no model
        // validation runs. A caller therefore cannot make the response vary — in particular
        // cannot provoke a 400 — by shaping the payload, and no caller-supplied connection
        // string is read.
        var method = typeof(TenantsController).GetMethod(nameof(TenantsController.Create))!;

        Assert.Empty(method.GetParameters());
    }

    [Fact]
    public void Create_AnswersIdenticallyOnRepeatedCalls()
    {
        var first = Assert.IsType<ObjectResult>(_sut.Create());
        var second = Assert.IsType<ObjectResult>(_sut.Create());

        Assert.Equal(first.StatusCode, second.StatusCode);
        Assert.Equal(ErrorOf(first), ErrorOf(second));
        AssertNothingWasCalled();
    }

    // ── DELETE /tenants/{id} ─────────────────────────────────────────────────

    [Fact]
    public void Deactivate_ReturnsMethodNotAllowed()
    {
        var result = Assert.IsType<ObjectResult>(_sut.Deactivate(Guid.NewGuid()));

        Assert.Equal(405, result.StatusCode);
    }

    [Fact]
    public void Deactivate_ReturnsTheExactWithdrawalMessage()
    {
        var result = Assert.IsType<ObjectResult>(_sut.Deactivate(Guid.NewGuid()));

        Assert.Equal(DeactivateMessage, ErrorOf(result));
    }

    [Theory]
    [MemberData(nameof(TargetIds))]
    public void Deactivate_NeverTouchesTheTenantService(Guid id)
    {
        _sut.Deactivate(id);

        AssertNothingWasCalled();
    }

    [Fact]
    public void Deactivate_AnswersIdenticallyForEveryId()
    {
        var results = Targets
            .Select(id => Assert.IsType<ObjectResult>(_sut.Deactivate(id)))
            .ToList();

        Assert.All(results, r => Assert.Equal(405, r.StatusCode));
        Assert.All(results, r => Assert.Equal(DeactivateMessage, ErrorOf(r)));
        AssertNothingWasCalled();
    }

    // ── POST /tenants/{id}/migrate ───────────────────────────────────────────

    [Fact]
    public void Migrate_ReturnsMethodNotAllowed()
    {
        var result = Assert.IsType<ObjectResult>(_sut.Migrate(Guid.NewGuid()));

        Assert.Equal(405, result.StatusCode);
    }

    [Fact]
    public void Migrate_ReturnsTheExactWithdrawalMessage()
    {
        var result = Assert.IsType<ObjectResult>(_sut.Migrate(Guid.NewGuid()));

        Assert.Equal(MigrateMessage, ErrorOf(result));
    }

    [Theory]
    [MemberData(nameof(TargetIds))]
    public void Migrate_NeverTouchesTheTenantOrProvisioningService(Guid id)
    {
        // Covers both halves of the old flow: the GetByIdAsync lookup that fetched the
        // provider, and the connection-string read that fed MigrateTenantDatabaseAsync.
        _sut.Migrate(id);

        AssertNothingWasCalled();
    }

    [Fact]
    public void Migrate_AnswersIdenticallyForEveryId()
    {
        var results = Targets
            .Select(id => Assert.IsType<ObjectResult>(_sut.Migrate(id)))
            .ToList();

        Assert.All(results, r => Assert.Equal(405, r.StatusCode));
        Assert.All(results, r => Assert.Equal(MigrateMessage, ErrorOf(r)));
        AssertNothingWasCalled();
    }

    [Fact]
    public void Migrate_NoLongerHasAConnectionStringHelper()
    {
        // The private helper resolved ITenantStore out of the request services, loaded every
        // tenant and returned the target's ConnectionString. Withdrawal has to remove the
        // mechanism, not just the route, so the helper must be gone from the controller.
        var helpers = typeof(TenantsController)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(m => m.Name.Contains("ConnectionString", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(helpers);
    }

    // ── The gate is unchanged ────────────────────────────────────────────────

    [Fact]
    public void WithdrawnActionsAreStillRoutedAndStillAdminOnly()
    {
        // Withdrawal means the capability is refused, not that the endpoints vanished or lost
        // their gate. A non-Admin or unauthenticated caller must still be rejected by
        // authorization before ever reaching the 405.
        var controller = typeof(TenantsController);

        Assert.NotNull(controller.GetMethod(nameof(TenantsController.Create))!
            .GetCustomAttribute<HttpPostAttribute>());

        var delete = controller.GetMethod(nameof(TenantsController.Deactivate))!
            .GetCustomAttribute<HttpDeleteAttribute>();
        Assert.NotNull(delete);
        Assert.Equal("{id:guid}", delete!.Template);

        var migrate = controller.GetMethod(nameof(TenantsController.Migrate))!
            .GetCustomAttribute<HttpPostAttribute>();
        Assert.NotNull(migrate);
        Assert.Equal("{id:guid}/migrate", migrate!.Template);

        var authorize = controller.GetCustomAttribute<AuthorizeAttribute>();
        Assert.NotNull(authorize);
        Assert.Equal("Admin", authorize!.Roles);
    }

    [Fact]
    public void WithdrawnActionsDoNotRelaxAuthorization()
    {
        var controller = typeof(TenantsController);

        foreach (var name in new[]
                 {
                     nameof(TenantsController.Create),
                     nameof(TenantsController.Deactivate),
                     nameof(TenantsController.Migrate),
                 })
        {
            var method = controller.GetMethod(name)!;
            Assert.Null(method.GetCustomAttribute<AllowAnonymousAttribute>());
            Assert.Null(method.GetCustomAttribute<AuthorizeAttribute>());
        }
    }
}
