using ERP.Application.Abstractions.Identity.Contracts;
using ERP.Identity.Entities;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ERP.UnitTests.Identity;

/// <summary>
/// Unit tests for AuthService.LoginAsync — covers all authentication paths:
/// user lookup, active check, lockout logic, password verification,
/// tenant resolution, token generation, and session creation.
/// </summary>
public class AuthService_LoginTests : AuthServiceTestBase
{
    // ─── User Not Found ───

    [Fact]
    public async Task Login_WhenUserNotFound_ReturnsInvalidCredentials()
    {
        var request = new LoginRequest("nonexistent", "Password@123");

        var result = await Sut.LoginAsync(request, Guid.NewGuid().ToString());

        Assert.True(result.IsFailure);
        Assert.Equal("Auth.InvalidCredentials", result.Error.Code);
    }

    // ─── Inactive Account ───

    [Fact]
    public async Task Login_WhenUserInactive_ReturnsAccountInactive()
    {
        var user = CreateTestUser(isActive: false);
        await Context.Set<AppUser>().AddAsync(user);
        await Context.SaveChangesAsync();

        var request = new LoginRequest(user.UserName, "Password@123");

        var result = await Sut.LoginAsync(request, Guid.NewGuid().ToString());

        Assert.True(result.IsFailure);
        Assert.Equal("Auth.AccountInactive", result.Error.Code);
    }

    // ─── Account Lockout ───

    [Fact]
    public async Task Login_WhenAccountLocked_ReturnsAccountLocked()
    {
        var user = CreateTestUser(isLocked: true, lockoutEnd: DateTime.UtcNow.AddMinutes(10));
        await Context.Set<AppUser>().AddAsync(user);
        await Context.SaveChangesAsync();

        var request = new LoginRequest(user.UserName, "Password@123");

        var result = await Sut.LoginAsync(request, Guid.NewGuid().ToString());

        Assert.True(result.IsFailure);
        Assert.Equal("Auth.AccountLocked", result.Error.Code);
    }

    [Fact]
    public async Task Login_WhenLockoutExpired_AutoUnlocksAndSucceeds()
    {
        // Lockout expired 5 minutes ago — should auto-unlock
        var (user, tenant, _, _) = await SeedUserWithRoleAsync();
        user.IsLocked = true;
        user.LockoutEnd = DateTime.UtcNow.AddMinutes(-5);
        user.AccessFailedCount = 5;
        await Context.SaveChangesAsync();

        var request = new LoginRequest(user.UserName, "Password@123");

        var result = await Sut.LoginAsync(request, tenant.Code);

        Assert.True(result.IsSuccess);
        // Verify auto-unlock persisted
        var updatedUser = await Context.Set<AppUser>().FindAsync(user.Id);
        Assert.False(updatedUser!.IsLocked);
        Assert.Null(updatedUser.LockoutEnd);
        Assert.Equal(0, updatedUser.AccessFailedCount);
    }

    // ─── Password Verification ───

    [Fact]
    public async Task Login_WhenPasswordWrong_ReturnsInvalidCredentials()
    {
        var user = CreateTestUser();
        await Context.Set<AppUser>().AddAsync(user);
        await Context.SaveChangesAsync();

        var request = new LoginRequest(user.UserName, "WrongPassword!");

        var result = await Sut.LoginAsync(request, Guid.NewGuid().ToString());

        Assert.True(result.IsFailure);
        Assert.Equal("Auth.InvalidCredentials", result.Error.Code);
    }

    [Fact]
    public async Task Login_WhenPasswordWrong_IncrementsAccessFailedCount()
    {
        var user = CreateTestUser();
        await Context.Set<AppUser>().AddAsync(user);
        await Context.SaveChangesAsync();

        var request = new LoginRequest(user.UserName, "WrongPassword!");
        await Sut.LoginAsync(request, Guid.NewGuid().ToString());

        var updatedUser = await Context.Set<AppUser>().FindAsync(user.Id);
        Assert.Equal(1, updatedUser!.AccessFailedCount);
    }

    [Fact]
    public async Task Login_WhenFifthFailedAttempt_LocksAccount()
    {
        // User already has 4 failed attempts — 5th should trigger lockout
        var user = CreateTestUser(accessFailedCount: 4);
        await Context.Set<AppUser>().AddAsync(user);
        await Context.SaveChangesAsync();

        var request = new LoginRequest(user.UserName, "WrongPassword!");
        await Sut.LoginAsync(request, Guid.NewGuid().ToString());

        var updatedUser = await Context.Set<AppUser>().FindAsync(user.Id);
        Assert.True(updatedUser!.IsLocked);
        Assert.NotNull(updatedUser.LockoutEnd);
        Assert.True(updatedUser.LockoutEnd > DateTime.UtcNow);
        Assert.Equal(5, updatedUser.AccessFailedCount);
    }

    // ─── Tenant Resolution ───

    [Fact]
    public async Task Login_WhenTenantGuidNotFound_ReturnsInvalidTenant()
    {
        var user = CreateTestUser();
        await Context.Set<AppUser>().AddAsync(user);
        await Context.SaveChangesAsync();

        var request = new LoginRequest(user.UserName, "Password@123");

        // Pass a valid GUID format but no matching Tenant in DB
        var result = await Sut.LoginAsync(request, Guid.NewGuid().ToString());

        Assert.True(result.IsFailure);
        Assert.Equal("Auth.InvalidTenant", result.Error.Code);
    }

    [Fact]
    public async Task Login_WhenTenantCodeNotFound_ReturnsInvalidTenant()
    {
        var user = CreateTestUser();
        await Context.Set<AppUser>().AddAsync(user);
        await Context.SaveChangesAsync();

        var request = new LoginRequest(user.UserName, "Password@123");

        var result = await Sut.LoginAsync(request, "NONEXISTENT");

        Assert.True(result.IsFailure);
        Assert.Equal("Auth.InvalidTenant", result.Error.Code);
    }

    [Fact]
    public async Task Login_WhenTenantIsCode_ReturnsSuccess()
    {
        var (user, tenant, _, _) = await SeedUserWithRoleAsync();

        var request = new LoginRequest(user.UserName, "Password@123");

        var result = await Sut.LoginAsync(request, tenant.Code);

        Assert.True(result.IsSuccess);
        Assert.Equal("mock-access-token", result.Value.AccessToken);
        Assert.Equal("mock-refresh-token", result.Value.RefreshToken);
    }

    [Fact]
    public async Task Login_WhenTenantIsGuid_ReturnsSuccessWithCodeInToken()
    {
        var (user, tenant, _, _) = await SeedUserWithRoleAsync();

        var request = new LoginRequest(user.UserName, "Password@123");

        // Pass the tenant GUID instead of code
        var result = await Sut.LoginAsync(request, tenant.Id.ToString());

        Assert.True(result.IsSuccess);
        // Verify GenerateAccessToken was called with tenant Code (not GUID)
        TokenServiceMock.Verify(t => t.GenerateAccessToken(
            It.IsAny<AppUser>(), It.IsAny<IList<string>>(),
            It.IsAny<IList<string>>(), tenant.Code, It.IsAny<string>()), Times.Once);
    }

    // ─── Successful Login Side Effects ───

    [Fact]
    public async Task Login_OnSuccess_ResetsFailedCount()
    {
        var (user, tenant, _, _) = await SeedUserWithRoleAsync();
        user.AccessFailedCount = 3;
        await Context.SaveChangesAsync();

        var request = new LoginRequest(user.UserName, "Password@123");
        await Sut.LoginAsync(request, tenant.Code);

        var updatedUser = await Context.Set<AppUser>().FindAsync(user.Id);
        Assert.Equal(0, updatedUser!.AccessFailedCount);
        Assert.False(updatedUser.IsLocked);
    }

    [Fact]
    public async Task Login_OnSuccess_CreatesSession()
    {
        var (user, tenant, _, _) = await SeedUserWithRoleAsync();

        var request = new LoginRequest(user.UserName, "Password@123");
        var result = await Sut.LoginAsync(request, tenant.Code);

        Assert.True(result.IsSuccess);

        var session = await Context.Set<UserSession>()
            .FirstOrDefaultAsync(s => s.UserId == user.Id);
        Assert.NotNull(session);
        Assert.Equal(tenant.Id, session.TenantId);
        Assert.False(session.IsRevoked);
        Assert.True(session.ExpiresAt > DateTime.UtcNow);
    }

    [Fact]
    public async Task Login_OnSuccess_SetsLastLoginAt()
    {
        var (user, tenant, _, _) = await SeedUserWithRoleAsync();
        Assert.Null(user.LastLoginAt);

        var request = new LoginRequest(user.UserName, "Password@123");
        await Sut.LoginAsync(request, tenant.Code);

        var updatedUser = await Context.Set<AppUser>().FindAsync(user.Id);
        Assert.NotNull(updatedUser!.LastLoginAt);
    }

    [Fact]
    public async Task Login_OnSuccess_ReturnsCorrectUserDto()
    {
        var (user, tenant, role, _) = await SeedUserWithRoleAsync();

        var request = new LoginRequest(user.UserName, "Password@123");
        var result = await Sut.LoginAsync(request, tenant.Code);

        Assert.True(result.IsSuccess);
        var dto = result.Value.User;
        Assert.Equal(user.Id, dto.Id);
        Assert.Equal(user.UserName, dto.UserName);
        Assert.Equal(user.Email, dto.Email);
        Assert.Contains(role.Name, dto.Roles);
    }

    // ─── Cross-Tenant Isolation Gate (security review finding #1) ───

    [Fact]
    public async Task Login_WhenUserNotAssignedToTenant_ReturnsInvalidCredentials()
    {
        // Build a user in tenant A and a SEPARATE tenant B with no UserTenant link.
        // The user has valid credentials but is not a member of tenant B — login MUST
        // fail with the generic InvalidCredentials code (no tenant-membership leak).
        var (user, _, _, _) = await SeedUserWithRoleAsync(tenantCode: "TENANT_A");
        var foreignTenant = CreateTestTenant("TENANT_B");
        await Context.Set<Tenant>().AddAsync(foreignTenant);
        await Context.SaveChangesAsync();

        var request = new LoginRequest(user.UserName, "Password@123");
        var result = await Sut.LoginAsync(request, foreignTenant.Code);

        Assert.True(result.IsFailure);
        Assert.Equal("Auth.InvalidCredentials", result.Error.Code);

        // No session must be created when membership check fails.
        var sessionExists = await Context.Set<UserSession>().AnyAsync(s => s.UserId == user.Id);
        Assert.False(sessionExists);
    }

    [Fact]
    public async Task Login_WhenUserTenantMembershipInactive_ReturnsInvalidCredentials()
    {
        // Membership row exists but IsActive=false (access revoked) — must be rejected
        // identically to "never assigned" so attackers can't enumerate revoked accounts.
        var (user, tenant, _, _) = await SeedUserWithRoleAsync();
        var membership = await Context.Set<UserTenant>()
            .FirstAsync(ut => ut.UserId == user.Id && ut.TenantId == tenant.Id);
        membership.IsActive = false;
        await Context.SaveChangesAsync();

        var request = new LoginRequest(user.UserName, "Password@123");
        var result = await Sut.LoginAsync(request, tenant.Code);

        Assert.True(result.IsFailure);
        Assert.Equal("Auth.InvalidCredentials", result.Error.Code);
    }
}
