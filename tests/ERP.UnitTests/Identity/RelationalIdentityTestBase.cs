using ERP.Application.Abstractions.Identity;
using ERP.Application.Abstractions.MultiTenancy;
using ERP.Identity.Entities;
using ERP.Identity.Persistence;
using ERP.Persistence.Context;
using ERP.Persistence.Interceptors;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ERP.UnitTests.Identity;

/// <summary>
/// Relational counterpart to <see cref="IdentityServiceTestBase"/>, backed by
/// SQLite in-memory instead of the EF InMemory provider.
///
/// WHY THIS EXISTS
/// ===============
/// The EF InMemory provider enforces **no foreign keys and no unique indexes**.
/// That is precisely why a real defect shipped undetected: UserRoles rows were
/// being created with TenantId = Guid.Empty, which InMemory accepted happily and
/// SQL Server rejected with FK_UserRoles_Tenants_TenantId. Any test that means to
/// say something about relational integrity has to run on a relational provider.
///
/// This base is additive on purpose. The existing InMemory base stays exactly as
/// it is — migrating the whole Identity suite would be a far larger change than
/// the defect warrants, and pure validation tests gain nothing from SQLite.
///
/// SQLITE SPECIFICS
/// ================
/// * The connection is opened by the fixture and held open for its lifetime. An
///   in-memory SQLite database exists only as long as a connection to it does;
///   letting EF open and close per operation would discard the schema.
/// * EF Core's SQLite provider issues `PRAGMA foreign_keys=ON` per connection.
///   That is asserted rather than assumed — see
///   <see cref="ForeignKeysAreEnforcedAsync"/>, which the FK tests call first.
/// </summary>
public abstract class RelationalIdentityTestBase : IDisposable
{
    private readonly SqliteConnection _connection;

    protected readonly MasterDbContext Context;
    protected readonly UserRepository Users;
    protected readonly RoleRepository Roles;
    protected readonly TenantRepository Tenants;
    protected readonly MasterUnitOfWork UnitOfWork;

    /// <summary>
    /// Validated current tenant for services that make tenant authorization
    /// decisions. Mirrors what TenantMiddleware supplies at runtime. Tests that
    /// exercise tenant scoping override <see cref="SetCurrentTenant"/>.
    /// </summary>
    private readonly Mock<ITenantProvider> _tenantProviderMock = CreateTenantProviderMock();
    protected ITenantProvider TenantProvider => _tenantProviderMock.Object;

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
    /// One-time, thread-safe registration of the Identity entity configurations,
    /// mirroring <see cref="IdentityServiceTestBase"/>.
    /// </summary>
    static RelationalIdentityTestBase()
    {
        var identityAssembly = typeof(AppUser).Assembly;
        if (!MasterDbContext.ConfigurationAssemblies.Contains(identityAssembly))
            MasterDbContext.ConfigurationAssemblies.Add(identityAssembly);
    }

    protected RelationalIdentityTestBase()
    {
        var currentUserMock = new Mock<ICurrentUserService>();
        currentUserMock.SetupGet(c => c.UserId).Returns(Guid.NewGuid().ToString());
        currentUserMock.SetupGet(c => c.TenantId).Returns((string?)null);

        // Held open for the fixture's lifetime — closing it drops the database.
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<MasterDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(new AuditableEntityInterceptor(currentUserMock.Object))
            .Options;

        Context = new MasterDbContext(options);
        Context.Database.EnsureCreated();

        Users = new UserRepository(Context);
        Roles = new RoleRepository(Context);
        Tenants = new TenantRepository(Context);
        UnitOfWork = new MasterUnitOfWork(Context);
    }

    /// <summary>
    /// Reads SQLite's own FK setting. Tests that claim to prove FK enforcement
    /// assert on this first, so a silently disabled pragma can never make those
    /// tests vacuously pass — which is the exact failure mode of the InMemory
    /// suite this base exists to replace.
    /// </summary>
    protected async Task<bool> ForeignKeysAreEnforcedAsync()
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys;";
        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt64(result) == 1;
    }

    // ── Seed helpers (mirrors IdentityServiceTestBase) ──

    protected static AppUser CreateTestUser(string userName = "testuser", bool isActive = true) =>
        new()
        {
            UserName = userName,
            NormalizedUserName = userName.ToUpperInvariant(),
            Email = $"{userName}@example.com",
            NormalizedEmail = $"{userName.ToUpperInvariant()}@EXAMPLE.COM",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("Password@123", workFactor: 4),
            FirstName = "Test",
            LastName = "User",
            IsActive = isActive,
            SecurityStamp = Guid.NewGuid().ToString()
        };

    protected static AppRole CreateTestRole(string name = "Admin") =>
        new()
        {
            Name = name,
            NormalizedName = name.ToUpperInvariant(),
            Description = $"{name} role",
            IsSystemRole = false
        };

    protected static Tenant CreateTestTenant(string code = "ABC") =>
        new()
        {
            Code = TenantCode.Normalize(code), // canonical form, exactly as production writes it
            Name = $"{code} Manufacturing",
            ConnectionString = $"Server=localhost;Database=ERP_{code}",
            DbProvider = "SqlServer",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

    protected static UserTenant CreateMembership(Guid userId, Guid tenantId, bool isActive = true) =>
        new()
        {
            UserId = userId,
            TenantId = tenantId,
            IsDefault = false,
            IsActive = isActive,
            JoinedAt = DateTime.UtcNow
        };

    protected async Task<T> PersistAsync<T>(T entity) where T : class
    {
        await Context.Set<T>().AddAsync(entity);
        await Context.SaveChangesAsync();
        return entity;
    }

    public void Dispose()
    {
        Context.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }
}
