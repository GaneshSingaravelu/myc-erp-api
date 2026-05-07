using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ERP.Application.Abstractions.Email;
using ERP.Identity.Abstractions;
using ERP.Identity.Abstractions.Persistence;
using ERP.Identity.Configuration;
using ERP.Identity.Entities;
using ERP.Identity.Persistence;
using ERP.Identity.Services;
using ERP.Persistence.Context;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace ERP.UnitTests.Identity;

/// <summary>
/// Base class for AuthService unit tests.
/// Provides a fresh InMemory MasterDbContext, mock dependencies,
/// and helper methods for seeding test data.
///
/// Each test class inherits this and gets an isolated database instance.
/// </summary>
public abstract class AuthServiceTestBase : IDisposable
{
    protected readonly MasterDbContext Context;
    protected readonly Mock<ITokenService> TokenServiceMock;
    protected readonly Mock<IMemoryCache> CacheMock;
    protected Mock<IEmailService> EmailServiceMock = null!;
    protected readonly AuthService Sut; // System Under Test

    protected static readonly JwtSettings TestJwtSettings = new()
    {
        SecretKey = "test-secret-key-32-chars-minimum!!",
        Issuer = "test-issuer",
        Audience = "test-audience",
        AccessTokenExpirationMinutes = 60,
        RefreshTokenExpirationDays = 7
    };

    /// <summary>
    /// One-time, thread-safe registration of Identity entity configurations.
    /// CLR guarantees static constructors run exactly once, even under parallel xUnit execution.
    /// This eliminates the race condition on MasterDbContext.ConfigurationAssemblies (shared static List).
    /// </summary>
    static AuthServiceTestBase()
    {
        var identityAssembly = typeof(AppUser).Assembly;
        if (!MasterDbContext.ConfigurationAssemblies.Contains(identityAssembly))
            MasterDbContext.ConfigurationAssemblies.Add(identityAssembly);
    }

    protected AuthServiceTestBase()
    {
        // Each test gets its own InMemory DB (unique name prevents cross-test leakage)
        var options = new DbContextOptionsBuilder<MasterDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        Context = new MasterDbContext(options);

        TokenServiceMock = new Mock<ITokenService>();
        CacheMock = new Mock<IMemoryCache>();

        // Default mock behavior — overridden in specific tests when needed
        TokenServiceMock.Setup(t => t.GenerateRefreshToken())
            .Returns("mock-refresh-token");
        TokenServiceMock.Setup(t => t.GenerateAccessToken(
                It.IsAny<AppUser>(), It.IsAny<IList<string>>(),
                It.IsAny<IList<string>>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns("mock-access-token");

        // Wire real repository implementations over the InMemory context.
        // This keeps the test investment in seeded graphs (SeedUserWithRoleAsync, etc.)
        // while still verifying AuthService against the refactored repository contracts.
        // All repositories share the same DbContext instance → IMasterUnitOfWork.SaveChangesAsync
        // commits changes staged through any of them in a single EF Core SaveChanges call.
        var users = new UserRepository(Context);
        var sessions = new UserSessionRepository(Context);
        var passwordHistory = new UserPasswordHistoryRepository(Context);
        var tenants = new TenantRepository(Context);
        var unitOfWork = new MasterUnitOfWork(Context);

        var emailServiceMock = new Mock<IEmailService>();
        EmailServiceMock = emailServiceMock;

        Sut = new AuthService(
            users,
            sessions,
            passwordHistory,
            tenants,
            unitOfWork,
            TokenServiceMock.Object,
            CacheMock.Object,
            emailServiceMock.Object,
            Options.Create(TestJwtSettings),
            NullLogger<AuthService>.Instance);
    }

    // ── Helper: Create a standard test user ──

    protected AppUser CreateTestUser(
        string userName = "testuser",
        string password = "Password@123",
        bool isActive = true,
        bool isLocked = false,
        int accessFailedCount = 0,
        DateTime? lockoutEnd = null)
    {
        return new AppUser
        {
            UserName = userName,
            NormalizedUserName = userName.ToUpperInvariant(),
            Email = $"{userName}@example.com",
            NormalizedEmail = $"{userName.ToUpperInvariant()}@EXAMPLE.COM",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password, workFactor: 4),
            FirstName = "Test",
            LastName = "User",
            IsActive = isActive,
            IsLocked = isLocked,
            AccessFailedCount = accessFailedCount,
            LockoutEnd = lockoutEnd,
            SecurityStamp = Guid.NewGuid().ToString()
        };
    }

    // ── Helper: Create a test tenant ──

    protected Tenant CreateTestTenant(string code = "ABC")
    {
        return new Tenant
        {
            Code = code,
            Name = $"{code} Manufacturing",
            ConnectionString = $"Server=localhost;Database=ERP_{code}",
            DbProvider = "SqlServer",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
    }

    // ── Helper: Seed user with role and permission graph ──

    protected async Task<(AppUser User, Tenant Tenant, AppRole Role, Permission Permission)> SeedUserWithRoleAsync(
        string userName = "testuser",
        string password = "Password@123",
        string tenantCode = "ABC",
        string roleName = "Admin",
        string permissionCode = "JOBCARD_CREATE")
    {
        var user = CreateTestUser(userName, password);
        var tenant = CreateTestTenant(tenantCode);
        var permission = new Permission { Code = permissionCode, Name = "Create Job Card", Category = "Production" };
        var role = new AppRole { Name = roleName, NormalizedName = roleName.ToUpperInvariant(), IsSystemRole = true };

        await Context.Set<AppUser>().AddAsync(user);
        await Context.Set<Tenant>().AddAsync(tenant);
        await Context.Set<Permission>().AddAsync(permission);
        await Context.Set<AppRole>().AddAsync(role);
        await Context.SaveChangesAsync();

        var rolePermission = new AppRolePermission { RoleId = role.Id, PermissionId = permission.Id };
        var userRole = new AppUserRole { UserId = user.Id, RoleId = role.Id, TenantId = tenant.Id };
        // UserTenant is the cross-tenant isolation gate enforced at login. Production
        // seeds it for every user-tenant assignment, so happy-path tests must too —
        // otherwise the new IUserRepository.IsAssignedToTenantAsync check rejects login.
        var userTenant = new UserTenant { UserId = user.Id, TenantId = tenant.Id, IsActive = true };

        await Context.Set<AppRolePermission>().AddAsync(rolePermission);
        await Context.Set<AppUserRole>().AddAsync(userRole);
        await Context.Set<UserTenant>().AddAsync(userTenant);
        await Context.SaveChangesAsync();

        return (user, tenant, role, permission);
    }

    // ── Helper: Create a UserSession ──

    protected UserSession CreateTestSession(
        Guid userId,
        Guid tenantId,
        string refreshToken = "valid-refresh-token",
        string jti = "original-jti",
        bool isRevoked = false,
        DateTime? expiresAt = null,
        DateTime? refreshTokenExpiry = null)
    {
        return new UserSession
        {
            UserId = userId,
            TenantId = tenantId,
            RefreshToken = refreshToken,
            RefreshTokenExpiry = refreshTokenExpiry ?? DateTime.UtcNow.AddDays(7),
            AccessTokenJti = jti,
            ExpiresAt = expiresAt ?? DateTime.UtcNow.AddDays(7),
            IsRevoked = isRevoked,
            CreatedAt = DateTime.UtcNow
        };
    }

    // ── Helper: Build a ClaimsPrincipal for refresh token tests ──

    protected static ClaimsPrincipal CreatePrincipal(
        Guid userId,
        string? tenantClaim = null,
        string? securityStamp = null,
        string? jti = null)
    {
        // Use JWT-native short claim names ("sub", "jti") — production now disables
        // inbound claim mapping in TokenService.GetPrincipalFromExpiredToken, so the
        // principal AuthService sees has these short names rather than ClaimTypes URIs.
        // Keeping the test mock aligned with the production claim contract avoids
        // false greens where tests pass against names production no longer reads.
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId.ToString())
        };

        if (tenantClaim is not null)
            claims.Add(new Claim("tenant_id", tenantClaim));

        if (securityStamp is not null)
            claims.Add(new Claim("security_stamp", securityStamp));

        if (jti is not null)
            claims.Add(new Claim(JwtRegisteredClaimNames.Jti, jti));

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    public void Dispose()
    {
        Context.Dispose();
        GC.SuppressFinalize(this);
    }
}
