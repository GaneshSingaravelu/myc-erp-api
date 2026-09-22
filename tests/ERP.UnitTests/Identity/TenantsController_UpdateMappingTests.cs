using ERP.Api.Controllers.v1;
using ERP.Application.Abstractions.Identity;
using ERP.Application.Abstractions.Identity.Contracts;
using ERP.Application.Abstractions.MultiTenancy;
using ERP.SharedKernel.Results;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ERP.UnitTests.Identity;

/// <summary>
/// S-T3 — PUT /tenants/{id} maps its two refusals to different status codes.
///
/// The action previously answered every failure with 404. Once the service gained a second
/// refusal that is a statement about the REQUEST rather than the target, that became wrong in
/// both directions: an activation refusal reported as 404 would tell the caller their own
/// tenant does not exist, and it would hide a real rule behind a lookup failure.
///
/// The split is deliberate and asymmetric. Scope failures stay 404 so a caller naming another
/// tenant's id cannot tell it apart from an id that names nothing — the existence-oracle
/// property the service establishes by never looking a foreign id up. Everything else is 400.
///
/// These tests drive the controller directly against a mocked service, because the mapping is
/// the contract under test; what the service decides is proven in
/// <c>TenantManagementService_TenantScopedWriteTests</c>.
/// </summary>
public class TenantsController_UpdateMappingTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private readonly Mock<ITenantManagementService> _tenantService = new(MockBehavior.Strict);
    private readonly Mock<ITenantProvisioningService> _provisioningService = new(MockBehavior.Strict);
    private readonly TenantsController _sut;

    public TenantsController_UpdateMappingTests()
    {
        _sut = new TenantsController(
            _tenantService.Object,
            _provisioningService.Object,
            NullLogger<TenantsController>.Instance);
    }

    private static UpdateTenantRequest Request(bool? isActive = null) => new("Renamed", isActive);

    private void ServiceReturns(Result<TenantResponse> result) =>
        _tenantService
            .Setup(s => s.UpdateAsync(TenantA, It.IsAny<UpdateTenantRequest>()))
            .ReturnsAsync(result);

    [Fact]
    public async Task NotFound_MapsTo404()
    {
        ServiceReturns(Result.Failure<TenantResponse>(Error.NotFound("Tenant", TenantA)));

        var result = await _sut.Update(TenantA, Request());

        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        Assert.Equal(404, notFound.StatusCode);
    }

    [Fact]
    public async Task ActivationImmutable_MapsTo400_NotTo404()
    {
        ServiceReturns(Result.Failure<TenantResponse>(Error.Validation(
            "Tenant.ActivationImmutable",
            "A tenant's active state cannot be changed through this endpoint.")));

        var result = await _sut.Update(TenantA, Request(isActive: false));

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(400, badRequest.StatusCode);
        Assert.IsNotType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task BothRefusals_KeepThisControllerSExistingErrorBodyShape()
    {
        // The body stays the Error record itself, as every other action on this controller
        // already returns. The 404 contract is therefore unchanged by the new mapping — only
        // the status code of the activation refusal is new.
        ServiceReturns(Result.Failure<TenantResponse>(Error.NotFound("Tenant", TenantA)));
        var notFound = Assert.IsType<NotFoundObjectResult>(await _sut.Update(TenantA, Request()));
        var notFoundBody = Assert.IsType<Error>(notFound.Value);
        Assert.Equal("Tenant.NotFound", notFoundBody.Code);

        _tenantService.Reset();
        ServiceReturns(Result.Failure<TenantResponse>(Error.Validation(
            "Tenant.ActivationImmutable", "A tenant's active state cannot be changed through this endpoint.")));
        var badRequest = Assert.IsType<BadRequestObjectResult>(await _sut.Update(TenantA, Request(isActive: false)));
        var badRequestBody = Assert.IsType<Error>(badRequest.Value);
        Assert.Equal("Tenant.ActivationImmutable", badRequestBody.Code);
    }

    [Fact]
    public async Task Success_StillReturns200WithTheTenant()
    {
        var response = new TenantResponse(
            TenantA, "ACME", "Renamed", "SqlServer", IsActive: true,
            CreatedAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), UserCount: 3);
        ServiceReturns(Result.Success(response));

        var result = await _sut.Update(TenantA, Request());

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(200, ok.StatusCode);
        Assert.Same(response, ok.Value);
    }

    [Fact]
    public async Task AnUnrecognisedRefusalFallsBackTo400_NotTo404()
    {
        // Fail safe rather than fail confusing: a future service error must not silently
        // inherit "not found" semantics just because 404 used to be the catch-all here.
        ServiceReturns(Result.Failure<TenantResponse>(Error.Validation("Tenant.Whatever", "Some other rule.")));

        var result = await _sut.Update(TenantA, Request());

        Assert.IsType<BadRequestObjectResult>(result);
    }
}
