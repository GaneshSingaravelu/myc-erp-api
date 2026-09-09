using ERP.Application.Abstractions.Identity;
using ERP.Application.Abstractions.MultiTenancy;
using ERP.Identity.Entities;
using ERP.Identity.Persistence;
using ERP.Persistence.Context;
using ERP.Persistence.Interceptors;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ERP.UnitTests.Identity;

/// <summary>
/// Shared test base for the non-Auth Identity services (User / Role / Menu /
/// TenantManagement / …). Wires up an isolated InMemory MasterDbContext + the
/// real repository implementations + MasterUnitOfWork.
///
/// Why real repos over an InMemory DB (not Mock&lt;IUserRepository&gt;)?
/// =================================================================
/// 1. Mirrors the existing AuthServiceTestBase pattern — one mental model.
/// 2. Verifies the EF Core LINQ shapes (Include chains, distinct queries) actually
///    work against the configured model, not a mock that always succeeds.
/// 3. Repository contract is small enough that mock setup boilerplate would
///    outweigh the speed gain.
///
/// Each test class inherits this and gets an isolated DB instance per test
/// (xUnit creates a new fixture per [Fact]).
/// </summary>
public abstract class IdentityServiceTestBase : IDisposable
{
    protected readonly MasterDbContext Context;
    protected readonly UserRepository Users;
    protected readonly RoleRepository Roles;
    protected readonly PermissionRepository Permissions;
    protected readonly MenuRepository Menus;
    protected readonly FeatureFlagRepository FeatureFlags;
    protected readonly TenantRepository Tenants;
    protected readonly MasterUnitOfWork UnitOfWork;

    /// <summary>
    /// Validated current tenant for services that make tenant authorization
    /// decisions. Mirrors what TenantMiddleware supplies at runtime. Tests that
    /// exercise tenant scoping override <see cref="SetCurrentTenant"/>.
    /// </summary>
    private readonly Mock<ITenantProvider> _tenantProviderMock = CreateTenantProviderMock();
    protected ITenantProvider TenantProvider => _tenantProviderMock.Object;

    /// <summary>An active (or deliberately inactive) tenant membership row.</summary>
    protected static UserTenant CreateMembership(Guid userId, Guid tenantId, bool isActive = true) =>
        new() { UserId = userId, TenantId = tenantId, IsActive = isActive };

    private static Mock<ITenantProvider> CreateTenantProviderMock()
    {
        var mock = new Mock<ITenantProvider>();
        mock.Setup(p => p.GetCurrentTenant())
            .Returns(new TenantInfo { Id = Guid.Empty, Identifier = "test-tenant", IsActive = true });
        return mock;
    }

    /// <summary>Point the validated tenant context at a specific tenant.</summary>
    protected void SetCurrentTenant(Guid tenantId, string identifier = "test-tenant")
        => _tenantProviderMock
            .Setup(p => p.GetCurrentTenant())
            .Returns(new TenantInfo { Id = tenantId, Identifier = identifier, IsActive = true });

    /// <summary>
    /// One-time, thread-safe registration of Identity entity configurations.
    /// Mirrors AuthServiceTestBase — the CLR guarantees static constructors run
    /// exactly once even under parallel xUnit execution, eliminating the race
    /// on MasterDbContext.ConfigurationAssemblies.
    /// </summary>
    static IdentityServiceTestBase()
    {
        var identityAssembly = typeof(AppUser).Assembly;
        if (!MasterDbContext.ConfigurationAssemblies.Contains(identityAssembly))
            MasterDbContext.ConfigurationAssemblies.Add(identityAssembly);
    }

    protected IdentityServiceTestBase()
    {
        // Wire the AuditableEntityInterceptor so soft-delete + audit columns
        // behave the same in tests as in production. Without it, EF's hard
        // delete runs and the IsDeleted/DeletedAt asserts can't fire.
        var currentUserMock = new Mock<ICurrentUserService>();
        currentUserMock.SetupGet(c => c.UserId).Returns(Guid.NewGuid().ToString());
        currentUserMock.SetupGet(c => c.TenantId).Returns((string?)null);

        var options = new DbContextOptionsBuilder<MasterDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .AddInterceptors(new AuditableEntityInterceptor(currentUserMock.Object))
            .Options;

        Context = new MasterDbContext(options);

        Users = new UserRepository(Context);
        Roles = new RoleRepository(Context);
        Permissions = new PermissionRepository(Context);
        Menus = new MenuRepository(Context);
        FeatureFlags = new FeatureFlagRepository(Context);
        Tenants = new TenantRepository(Context);
        UnitOfWork = new MasterUnitOfWork(Context);
    }

    // ── Seed helpers ──

    protected AppUser CreateTestUser(
        string userName = "testuser",
        string password = "Password@123",
        bool isActive = true)
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
            SecurityStamp = Guid.NewGuid().ToString()
        };
    }

    protected AppRole CreateTestRole(string name = "Admin", bool isSystemRole = false)
    {
        return new AppRole
        {
            Name = name,
            NormalizedName = name.ToUpperInvariant(),
            Description = $"{name} role",
            IsSystemRole = isSystemRole
        };
    }

    protected Permission CreateTestPermission(
        string code = "JOBCARD_CREATE",
        string category = "Production")
    {
        return new Permission
        {
            Code = code,
            Name = code.Replace('_', ' '),
            Category = category
        };
    }

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

    /// <summary>
    /// Persist a single entity and return it (with the DB-assigned id).
    /// Reduces test boilerplate when seeding lookup fixtures.
    /// </summary>
    protected async Task<T> PersistAsync<T>(T entity) where T : class
    {
        await Context.Set<T>().AddAsync(entity);
        await Context.SaveChangesAsync();
        return entity;
    }

    public void Dispose()
    {
        Context.Dispose();
        GC.SuppressFinalize(this);
    }
}
