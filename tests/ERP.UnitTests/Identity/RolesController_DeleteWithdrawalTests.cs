using System.Reflection;
using ERP.Api.Controllers.v1;
using ERP.Application.Abstractions.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace ERP.UnitTests.Identity;

/// <summary>
/// S-A4 — withdrawal of DELETE /roles/{id} (finding R-4).
///
/// The endpoint is disabled at the HTTP boundary rather than tenant-scoped. RoleService
/// .DeleteAsync still exists and is still unsafe — it deletes by raw id with no tenant
/// check — so the property under test is that nothing can reach it through HTTP any more.
///
/// These tests sit at the controller, not the service, because that is where the contract
/// now lives. The strict mock is deliberate: any call to IRoleService at all fails the test,
/// which is a stronger statement than verifying one method was not called.
///
/// The second property is that the answer is a constant. If the action looked a role up
/// before refusing, the disabled endpoint would still tell a caller whether an id names a
/// real role in another tenant — turning a withdrawn capability into an existence oracle.
/// </summary>
public class RolesController_DeleteWithdrawalTests
{
    private const string ExpectedMessage = "Deleting roles is not supported.";

    private readonly Mock<IRoleService> _roleService = new(MockBehavior.Strict);
    private readonly RolesController _sut;

    public RolesController_DeleteWithdrawalTests()
    {
        _sut = new RolesController(_roleService.Object);
    }

    /// <summary>Reads the anonymous response body's `error` member.</summary>
    private static string? ErrorOf(ObjectResult result)
        => result.Value?.GetType().GetProperty("error")?.GetValue(result.Value) as string;

    [Fact]
    public void Delete_ReturnsMethodNotAllowed()
    {
        var result = Assert.IsType<ObjectResult>(_sut.Delete(Guid.NewGuid()));

        Assert.Equal(405, result.StatusCode);
    }

    [Fact]
    public void Delete_ReturnsTheExactWithdrawalMessage()
    {
        var result = Assert.IsType<ObjectResult>(_sut.Delete(Guid.NewGuid()));

        Assert.Equal(ExpectedMessage, ErrorOf(result));
    }

    [Fact]
    public void Delete_NeverTouchesTheRoleService()
    {
        _sut.Delete(Guid.NewGuid());

        // Strict mock: an unconfigured call would already have thrown above. This states the
        // intent explicitly for anyone reading the test rather than the mock setup.
        _roleService.VerifyNoOtherCalls();
    }

    [Fact]
    public void Delete_AnswersIdenticallyForEveryId()
    {
        // Stands in for: a real global role, a role in this tenant, a role in another
        // tenant, a legacy unowned role, and an id naming nothing at all. Since no lookup
        // happens, none of those cases can be distinguished — which is the point.
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.Empty, Guid.NewGuid() };

        var results = ids
            .Select(id => Assert.IsType<ObjectResult>(_sut.Delete(id)))
            .ToList();

        Assert.All(results, r => Assert.Equal(405, r.StatusCode));
        Assert.All(results, r => Assert.Equal(ExpectedMessage, ErrorOf(r)));
        _roleService.VerifyNoOtherCalls();
    }

    [Fact]
    public void Delete_IsStillRoutedAndStillAdminOnly()
    {
        // Withdrawal means the capability is refused, not that the endpoint vanished or lost
        // its gate. An unauthenticated or non-Admin caller must still be rejected by
        // authorization before ever reaching the 405.
        var method = typeof(RolesController).GetMethod(nameof(RolesController.Delete))!;

        Assert.NotNull(method.GetCustomAttribute<HttpDeleteAttribute>());
        Assert.Equal("{id:guid}", method.GetCustomAttribute<HttpDeleteAttribute>()!.Template);

        var authorize = typeof(RolesController).GetCustomAttribute<AuthorizeAttribute>();
        Assert.NotNull(authorize);
        Assert.Equal("Admin", authorize!.Roles);
    }
}
