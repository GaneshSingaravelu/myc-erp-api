using ERP.Application.Abstractions.Identity.Contracts;
using ERP.Identity.Entities;
using ERP.Identity.Services;
using Microsoft.EntityFrameworkCore;

namespace ERP.UnitTests.Identity;

/// <summary>
/// Tenant-scoped role assignment, against a REAL relational provider.
///
/// These tests exist because the InMemory suite could not have caught the defect
/// they guard: it enforces neither foreign keys nor unique indexes, so UserRoles
/// rows with TenantId = Guid.Empty inserted cleanly in tests and blew up against
/// SQL Server with FK_UserRoles_Tenants_TenantId.
///
/// Everything asserted here is about persistence and integrity — the stored
/// TenantId, cross-tenant preservation, and the (UserId, RoleId, TenantId) unique
/// index. Pure validation cases stay in <see cref="UserServiceTests"/>.
/// </summary>
public class UserService_AssignRolesRelationalTests : RelationalIdentityTestBase
{
    private readonly UserService _sut;

    public UserService_AssignRolesRelationalTests()
    {
        _sut = new UserService(Users, Roles, Tenants, UnitOfWork);
    }

    /// <summary>Seeds a user, two tenants with membership in both, and three roles.</summary>
    private async Task<(AppUser User, Tenant TenantA, Tenant TenantB)> SeedAsync()
    {
        var user = await PersistAsync(CreateTestUser("alice"));
        var tenantA = await PersistAsync(CreateTestTenant("AAA"));
        var tenantB = await PersistAsync(CreateTestTenant("BBB"));
        await PersistAsync(CreateMembership(user.Id, tenantA.Id));
        await PersistAsync(CreateMembership(user.Id, tenantB.Id));
        return (user, tenantA, tenantB);
    }

    private Task<List<AppUserRole>> RowsAsync(Guid userId) =>
        Context.Set<AppUserRole>().AsNoTracking().Where(ur => ur.UserId == userId).ToListAsync();

    // ── H: the fixture must actually enforce what it claims ───────────────────

    [Fact]
    public async Task Sqlite_EnforcesForeignKeys()
    {
        // Asserted, not assumed. If this ever regresses, every FK-dependent test
        // below would silently become vacuous — exactly how the original defect
        // slipped through the InMemory suite.
        Assert.True(await ForeignKeysAreEnforcedAsync());
    }

    [Fact]
    public async Task Insert_WithoutTenant_IsRejectedByForeignKey()
    {
        Assert.True(await ForeignKeysAreEnforcedAsync());

        var user = await PersistAsync(CreateTestUser("alice"));
        var role = await PersistAsync(CreateTestRole("Admin"));

        // This is verbatim what the old AssignRolesAsync produced: no TenantId.
        Context.Set<AppUserRole>().Add(new AppUserRole { UserId = user.Id, RoleId = role.Id });

        await Assert.ThrowsAsync<DbUpdateException>(() => Context.SaveChangesAsync());
    }

    // ── A: the requested tenant is what gets persisted ────────────────────────

    [Fact]
    public async Task AssignRoles_PersistsRequestedTenantId()
    {
        var (user, tenantA, _) = await SeedAsync();
        var role = await PersistAsync(CreateTestRole("Admin"));

        var result = await _sut.AssignRolesAsync(
            user.Id, new AssignRolesRequest(tenantA.Id, new[] { role.Id }));

        Assert.True(result.IsSuccess);
        var row = Assert.Single(await RowsAsync(user.Id));
        Assert.Equal(tenantA.Id, row.TenantId);
        Assert.Equal(role.Id, row.RoleId);
    }

    // ── B: the reason this change exists ──────────────────────────────────────

    [Fact]
    public async Task AssignRoles_InOneTenant_LeavesOtherTenantsUntouched()
    {
        var (user, tenantA, tenantB) = await SeedAsync();
        var viewer = await PersistAsync(CreateTestRole("Viewer"));
        var admin = await PersistAsync(CreateTestRole("Admin"));

        // Pre-existing assignment in tenant B that must survive.
        await PersistAsync(new AppUserRole
        {
            UserId = user.Id,
            RoleId = viewer.Id,
            TenantId = tenantB.Id
        });

        var result = await _sut.AssignRolesAsync(
            user.Id, new AssignRolesRequest(tenantA.Id, new[] { admin.Id }));

        Assert.True(result.IsSuccess);
        var rows = await RowsAsync(user.Id);

        // The old global RemoveUserRoles(user.UserRoles) would have deleted the
        // tenant B row here — silent cross-tenant data loss.
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.TenantId == tenantB.Id && r.RoleId == viewer.Id);
        Assert.Contains(rows, r => r.TenantId == tenantA.Id && r.RoleId == admin.Id);
    }

    // ── C: replacement within the requested tenant ────────────────────────────

    [Fact]
    public async Task AssignRoles_ReplacesOnlyWithinRequestedTenant()
    {
        var (user, tenantA, tenantB) = await SeedAsync();
        var oldRole = await PersistAsync(CreateTestRole("Viewer"));
        var newRole = await PersistAsync(CreateTestRole("Admin"));

        await PersistAsync(new AppUserRole
        { UserId = user.Id, RoleId = oldRole.Id, TenantId = tenantA.Id });
        await PersistAsync(new AppUserRole
        { UserId = user.Id, RoleId = oldRole.Id, TenantId = tenantB.Id });

        var result = await _sut.AssignRolesAsync(
            user.Id, new AssignRolesRequest(tenantA.Id, new[] { newRole.Id }));

        Assert.True(result.IsSuccess);
        var rows = await RowsAsync(user.Id);

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.TenantId == tenantA.Id && r.RoleId == newRole.Id);
        Assert.Contains(rows, r => r.TenantId == tenantB.Id && r.RoleId == oldRole.Id);
        Assert.DoesNotContain(rows, r => r.TenantId == tenantA.Id && r.RoleId == oldRole.Id);
    }

    // ── D: empty list clears one tenant only ──────────────────────────────────

    [Fact]
    public async Task AssignRoles_WithEmptyList_ClearsOnlyRequestedTenant()
    {
        var (user, tenantA, tenantB) = await SeedAsync();
        var role = await PersistAsync(CreateTestRole("Admin"));

        await PersistAsync(new AppUserRole
        { UserId = user.Id, RoleId = role.Id, TenantId = tenantA.Id });
        await PersistAsync(new AppUserRole
        { UserId = user.Id, RoleId = role.Id, TenantId = tenantB.Id });

        var result = await _sut.AssignRolesAsync(
            user.Id, new AssignRolesRequest(tenantA.Id, Array.Empty<Guid>()));

        Assert.True(result.IsSuccess);
        var row = Assert.Single(await RowsAsync(user.Id));
        Assert.Equal(tenantB.Id, row.TenantId);
    }

    // ── E: duplicates cannot trip the unique index ────────────────────────────

    [Fact]
    public async Task AssignRoles_WithDuplicateRoleIds_CreatesOneRowPerRole()
    {
        var (user, tenantA, _) = await SeedAsync();
        var role = await PersistAsync(CreateTestRole("Admin"));

        // Against the real (UserId, RoleId, TenantId) unique index, an
        // un-deduplicated insert would throw here.
        var result = await _sut.AssignRolesAsync(
            user.Id, new AssignRolesRequest(tenantA.Id, new[] { role.Id, role.Id, role.Id }));

        Assert.True(result.IsSuccess);
        var row = Assert.Single(await RowsAsync(user.Id));
        Assert.Equal(role.Id, row.RoleId);
    }

    // ── F: the model's own headline case ──────────────────────────────────────

    [Fact]
    public async Task SameRole_CanExistForSameUser_InTwoTenants()
    {
        var (user, tenantA, tenantB) = await SeedAsync();
        var admin = await PersistAsync(CreateTestRole("Admin"));

        Assert.True((await _sut.AssignRolesAsync(
            user.Id, new AssignRolesRequest(tenantA.Id, new[] { admin.Id }))).IsSuccess);
        Assert.True((await _sut.AssignRolesAsync(
            user.Id, new AssignRolesRequest(tenantB.Id, new[] { admin.Id }))).IsSuccess);

        var rows = await RowsAsync(user.Id);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(admin.Id, r.RoleId));
        Assert.Contains(rows, r => r.TenantId == tenantA.Id);
        Assert.Contains(rows, r => r.TenantId == tenantB.Id);
    }

    // ── G: no partial write on a bad role id ──────────────────────────────────

    [Fact]
    public async Task AssignRoles_WithUnknownRole_PersistsNothing()
    {
        var (user, tenantA, _) = await SeedAsync();
        var existing = await PersistAsync(CreateTestRole("Viewer"));
        await PersistAsync(new AppUserRole
        { UserId = user.Id, RoleId = existing.Id, TenantId = tenantA.Id });

        var result = await _sut.AssignRolesAsync(
            user.Id, new AssignRolesRequest(tenantA.Id, new[] { existing.Id, Guid.NewGuid() }));

        Assert.True(result.IsFailure);
        Assert.Equal("Role.NotFound", result.Error.Code);

        var row = Assert.Single(await RowsAsync(user.Id));
        Assert.Equal(existing.Id, row.RoleId);
        Assert.Equal(tenantA.Id, row.TenantId);
    }
}
