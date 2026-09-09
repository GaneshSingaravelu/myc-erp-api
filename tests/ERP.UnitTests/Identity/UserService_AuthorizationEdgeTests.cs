using ERP.Application.Abstractions.Identity.Contracts;
using ERP.Application.Abstractions.MultiTenancy;
using ERP.Identity.Entities;
using ERP.Identity.Services;
using ERP.SharedKernel.Results;
using Moq;

namespace ERP.UnitTests.Identity;

/// <summary>
/// CT-4 remediation, phase P7 — edge hardening across the secured surface.
///
/// <para>This file deliberately does <b>not</b> restate what P2–P6 already prove. It
/// covers three things none of those files could, because each spans more than one
/// endpoint or more than one tenant-context state:</para>
///
/// <list type="number">
/// <item>the service fails closed when the tenant context is absent or degenerate;</item>
/// <item>the six secured resource-id operations treat an out-of-scope target
/// <i>consistently</i>, so no single one of them becomes the weak link;</item>
/// <item>an unknown id and a real-but-foreign id are indistinguishable, for users and
/// for roles alike, so neither becomes a membership-enumeration oracle.</item>
/// </list>
///
/// <para>Scope note: conclusions here are about UsersController / UserService only.
/// Other controllers are untested by this remediation.</para>
/// </summary>
public class UserService_AuthorizationEdgeTests : RelationalIdentityTestBase
{
    private readonly UserService _sut;

    public UserService_AuthorizationEdgeTests()
    {
        _sut = new UserService(Users, Roles, Tenants, UnitOfWork, TenantProvider);
    }

    private async Task<(Tenant A, Tenant B)> TwoTenantsAsync()
    {
        var a = await PersistAsync(CreateTestTenant("AAA"));
        var b = await PersistAsync(CreateTestTenant("BBB"));
        SetCurrentTenant(a.Id, "AAA");
        return (a, b);
    }

    private async Task<AppUser> MemberOfAsync(string name, params Guid[] tenantIds)
    {
        var user = await PersistAsync(CreateTestUser(name));
        foreach (var tenantId in tenantIds)
            await PersistAsync(CreateMembership(user.Id, tenantId));
        return user;
    }

    /// <summary>A service whose tenant context behaves the way <paramref name="setup"/> says.</summary>
    private UserService ServiceWithTenantContext(Action<Mock<ITenantProvider>> setup)
    {
        var mock = new Mock<ITenantProvider>();
        setup(mock);
        return new UserService(Users, Roles, Tenants, UnitOfWork, mock.Object);
    }

    // ── E2 / E3 — degenerate tenant context must fail closed ─────────────────

    [Fact] // E2
    public async Task E2_NoValidatedTenantContext_FailsClosedOnEveryReadAndWrite()
    {
        // HttpContextTenantProvider throws when TenantMiddleware has not populated the
        // context. The service must let that surface rather than fall back to an
        // unscoped query — an unscoped read here would be the original defect returning.
        var (a, _) = await TwoTenantsAsync();
        var target = await MemberOfAsync("alice", a.Id);

        var sut = ServiceWithTenantContext(m => m
            .Setup(p => p.GetCurrentTenant())
            .Throws(new InvalidOperationException("Tenant information not found in HttpContext.")));

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.GetAllAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.GetByIdAsync(target.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.GetUserRoleAssignmentsAsync(target.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.GetUserTenantsAsync(target.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.UpdateAsync(target.Id, new UpdateUserRequest("x@y.local", "X", "Y", true)));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.AssignRolesAsync(target.Id, new AssignRolesRequest(a.Id, Array.Empty<Guid>())));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.AssignTenantsAsync(target.Id, new AssignTenantsRequest(new[] { a.Id })));
    }

    [Fact] // E3
    public async Task E3_EmptyGuidTenantContext_ExposesNothingAndAuthorizesNothing()
    {
        // A Guid.Empty context must behave as "a tenant with no members", never as a
        // wildcard. The InMemory suite's old default was Guid.Empty, so this pins the
        // behaviour that made those defaults harmless.
        var (a, b) = await TwoTenantsAsync();
        var inA = await MemberOfAsync("alice", a.Id);
        var inB = await MemberOfAsync("bob", b.Id);

        var sut = ServiceWithTenantContext(m => m
            .Setup(p => p.GetCurrentTenant())
            .Returns(new TenantInfo { Id = Guid.Empty, Identifier = "none", IsActive = true }));

        Assert.Empty((await sut.GetAllAsync()).Value);
        Assert.True((await sut.GetByIdAsync(inA.Id)).IsFailure);
        Assert.True((await sut.GetByIdAsync(inB.Id)).IsFailure);
        Assert.True((await sut.UpdateAsync(inA.Id, new UpdateUserRequest("x@y.local", "X", "Y", true))).IsFailure);

        // Guid.Empty is rejected as a request tenant regardless of context.
        var roles = await sut.AssignRolesAsync(inA.Id, new AssignRolesRequest(Guid.Empty, Array.Empty<Guid>()));
        Assert.Equal("Role.TenantRequired", roles.Error.Code);

        Context.ChangeTracker.Clear();
        Assert.True(await Users.IsAssignedToTenantAsync(inA.Id, a.Id)); // nothing mutated
    }

    [Fact] // E4
    public async Task E4_ContextNamingAMissingTenant_ExposesNothing()
    {
        var (a, _) = await TwoTenantsAsync();
        var target = await MemberOfAsync("alice", a.Id);

        var sut = ServiceWithTenantContext(m => m
            .Setup(p => p.GetCurrentTenant())
            .Returns(new TenantInfo { Id = Guid.NewGuid(), Identifier = "ghost", IsActive = true }));

        Assert.Empty((await sut.GetAllAsync()).Value);
        Assert.True((await sut.GetByIdAsync(target.Id)).IsFailure);
    }

    // ── E11 / E12 / E13 — cross-endpoint consistency for out-of-scope targets ─

    public enum OutOfScope { TenantLess, InactiveInA, BOnly }

    [Theory] // E11, E12, E13
    [InlineData(OutOfScope.TenantLess)]
    [InlineData(OutOfScope.InactiveInA)]
    [InlineData(OutOfScope.BOnly)]
    public async Task OutOfScopeTarget_IsRefusedIdenticallyByEverySecuredOperation(OutOfScope kind)
    {
        // Each endpoint is proven individually in P2/P3/P5/P6. What is proven here is
        // that they AGREE: one endpoint answering differently would be the weak link an
        // attacker probes for.
        var (a, b) = await TwoTenantsAsync();

        var target = kind switch
        {
            OutOfScope.TenantLess => await PersistAsync(CreateTestUser("nomad")),
            OutOfScope.BOnly => await MemberOfAsync("bob", b.Id),
            _ => await PersistAsync(CreateTestUser("lapsed"))
        };
        if (kind == OutOfScope.InactiveInA)
            await PersistAsync(CreateMembership(target.Id, a.Id, isActive: false));

        var role = await PersistAsync(CreateTestRole("Admin"));

        var results = new (string Operation, Result Result)[]
        {
            ("GET /users/{id}",         await _sut.GetByIdAsync(target.Id)),
            ("GET /users/{id}/roles",   await _sut.GetUserRoleAssignmentsAsync(target.Id)),
            ("GET /users/{id}/tenants", await _sut.GetUserTenantsAsync(target.Id)),
            ("PUT /users/{id}",         await _sut.UpdateAsync(target.Id, new UpdateUserRequest("x@y.local", "X", "Y", true))),
            ("POST /users/{id}/roles",  await _sut.AssignRolesAsync(target.Id, new AssignRolesRequest(a.Id, new[] { role.Id }))),
            ("PUT /users/{id}/tenants", await _sut.AssignTenantsAsync(target.Id, new AssignTenantsRequest(new[] { a.Id }))),
        };

        foreach (var (operation, result) in results)
        {
            Assert.True(result.IsFailure, $"{operation} allowed an out-of-scope target ({kind}).");

            // No operation may disclose where the target actually belongs.
            Assert.DoesNotContain("BBB", result.Error.Message);
            Assert.DoesNotContain(b.Id.ToString(), result.Error.Message);
        }

        // And nothing was written by any of them.
        Context.ChangeTracker.Clear();
        Assert.False(await Users.IsAssignedToTenantAsync(target.Id, a.Id));
        Assert.Empty(await Users.GetUserRoleAssignmentsInTenantAsync(target.Id, a.Id));
    }

    // ── E14 / E15 — unknown id vs real foreign id ────────────────────────────

    [Fact] // E14 + E15
    public async Task UnknownUserAndForeignUser_AreIndistinguishableOnResourceIdOperations()
    {
        // If a foreign-but-real id answered differently from a fabricated one, the
        // endpoint would confirm that a guessed id exists somewhere in the system.
        var (a, b) = await TwoTenantsAsync();
        var foreign = await MemberOfAsync("bob", b.Id);
        var unknown = Guid.NewGuid();

        var pairs = new (string Operation, Result Foreign, Result Unknown)[]
        {
            ("GET /users/{id}",
                await _sut.GetByIdAsync(foreign.Id), await _sut.GetByIdAsync(unknown)),
            ("GET /users/{id}/roles",
                await _sut.GetUserRoleAssignmentsAsync(foreign.Id), await _sut.GetUserRoleAssignmentsAsync(unknown)),
            ("GET /users/{id}/tenants",
                await _sut.GetUserTenantsAsync(foreign.Id), await _sut.GetUserTenantsAsync(unknown)),
            ("PUT /users/{id}",
                await _sut.UpdateAsync(foreign.Id, new UpdateUserRequest("x@y.local", "X", "Y", true)),
                await _sut.UpdateAsync(unknown, new UpdateUserRequest("x@y.local", "X", "Y", true))),
            ("PUT /users/{id}/tenants",
                await _sut.AssignTenantsAsync(foreign.Id, new AssignTenantsRequest(new[] { a.Id })),
                await _sut.AssignTenantsAsync(unknown, new AssignTenantsRequest(new[] { a.Id }))),
        };

        foreach (var (operation, foreignResult, unknownResult) in pairs)
        {
            Assert.True(foreignResult.IsFailure, operation);
            Assert.True(unknownResult.IsFailure, operation);
            Assert.Equal(unknownResult.Error.Code, foreignResult.Error.Code);
        }
    }

    [Fact] // E14/E15 for role assignment, where the approved contract differs
    public async Task RoleAssignment_ForeignAndUnknownTarget_ShareTheTargetError()
    {
        // Role assignment intentionally keeps distinct ACTOR and TARGET errors (S1).
        // That distinction is about the tenant named in the body, not about the target,
        // so a foreign target and an unknown target must still look alike.
        var (a, b) = await TwoTenantsAsync();
        var foreign = await MemberOfAsync("bob", b.Id);
        var role = await PersistAsync(CreateTestRole("Admin"));

        var foreignResult = await _sut.AssignRolesAsync(
            foreign.Id, new AssignRolesRequest(a.Id, new[] { role.Id }));
        var unknownResult = await _sut.AssignRolesAsync(
            Guid.NewGuid(), new AssignRolesRequest(a.Id, new[] { role.Id }));

        Assert.Equal("User.NotInTenant", foreignResult.Error.Code);
        Assert.Equal("User.NotFound", unknownResult.Error.Code);

        // These differ, and that difference is reported rather than papered over: it
        // reveals only whether the id exists at all, which GET /users/{id} already
        // conceals, and never which tenant the target belongs to.
        Assert.DoesNotContain("BBB", foreignResult.Error.Message);
        Assert.DoesNotContain(b.Id.ToString(), foreignResult.Error.Message);
    }

    // ── E22 — role enumeration ───────────────────────────────────────────────

    [Fact] // E22
    public async Task UnknownRoleAndForeignTenantRole_AreIndistinguishable()
    {
        var (a, b) = await TwoTenantsAsync();
        var target = await MemberOfAsync("alice", a.Id);

        var foreignRole = CreateTestRole("BespokeB");
        foreignRole.TenantId = b.Id;
        await PersistAsync(foreignRole);

        var foreignResult = await _sut.AssignRolesAsync(
            target.Id, new AssignRolesRequest(a.Id, new[] { foreignRole.Id }));
        var unknownResult = await _sut.AssignRolesAsync(
            target.Id, new AssignRolesRequest(a.Id, new[] { Guid.NewGuid() }));

        Assert.Equal("Role.NotFound", foreignResult.Error.Code);
        Assert.Equal("Role.NotFound", unknownResult.Error.Code);
        Assert.DoesNotContain("BespokeB", foreignResult.Error.Message);
    }

    // ── Orphan-row guard ─────────────────────────────────────────────────────

    [Fact]
    public async Task SupportedFlows_DoNotCreateRoleRowsWithoutAnActiveMembership()
    {
        // Roles may only be written where an active membership exists, and no supported
        // flow removes or deactivates a membership afterwards — so the known orphan
        // issue cannot be fed by this remediation's surface.
        var (a, _) = await TwoTenantsAsync();
        var created = await _sut.CreateAsync(
            new CreateUserRequest("newuser", "new@example.com", "Password@123", "New", "User"));
        var role = await PersistAsync(CreateTestRole("Admin"));

        Assert.True((await _sut.AssignRolesAsync(
            created.Value.Id, new AssignRolesRequest(a.Id, new[] { role.Id }))).IsSuccess);

        Context.ChangeTracker.Clear();

        var roleRows = await Users.GetUserRoleAssignmentsInTenantAsync(created.Value.Id, a.Id);
        Assert.Single(roleRows);
        Assert.True(await Users.IsAssignedToTenantAsync(created.Value.Id, a.Id));
    }
}
