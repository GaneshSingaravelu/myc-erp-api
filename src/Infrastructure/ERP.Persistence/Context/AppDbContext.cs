using ERP.Persistence.Conversions;
using ERP.SharedKernel.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Persistence.Context;

/// <summary>
/// APP DB CONTEXT — The bridge between your C# code and the database.
///
/// WHAT IS DbContext? (Very Common Interview Question!)
/// =====================================================
/// DbContext is EF Core's main class. Think of it as a "session" with the database.
/// It does THREE things:
///
/// 1. MAPS C# classes to database tables (via DbSet&lt;T&gt; properties)
///    DbSet&lt;JobCard&gt; JobCards → maps to "JobCards" table in SQL
///
/// 2. TRACKS changes to your objects
///    When you modify a JobCard object, EF Core knows what changed
///    and generates the correct UPDATE SQL statement.
///
/// 3. SAVES changes to the database
///    _context.SaveChangesAsync() → generates and executes SQL
///    INSERT/UPDATE/DELETE statements for all tracked changes.
///
/// LIFECYCLE (Interview Question!):
/// =================================
/// DbContext is registered as SCOPED in Dependency Injection.
/// This means: ONE DbContext instance per HTTP request.
///
/// - Request starts → New DbContext created
/// - Handler uses DbContext (queries, updates)
/// - SaveChangesAsync() → All changes saved in ONE transaction
/// - Request ends → DbContext disposed (garbage collected)
///
/// WHY SCOPED AND NOT SINGLETON?
/// ==============================
/// - Singleton: ONE instance for ALL requests → memory leak, thread safety issues
/// - Transient: NEW instance every time → can't track changes across repository calls
/// - Scoped: ONE per request → perfect for Unit of Work pattern
///
/// NOTE ON MULTI-TENANCY:
/// =======================
/// This DbContext receives its connection string from the TenantDbContextFactory.
/// It doesn't know WHICH database it's connected to — it just uses whatever
/// connection was configured for the current tenant.
/// </summary>
public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    /// <summary>
    /// Called when EF Core builds the model (table structure).
    /// We apply entity configurations from separate files here.
    ///
    /// WHY SEPARATE CONFIGURATION FILES?
    /// ===================================
    /// Instead of putting all table mappings in OnModelCreating (which would be HUGE),
    /// we split them into one file per entity using IEntityTypeConfiguration&lt;T&gt;.
    /// ApplyConfigurationsFromAssembly() discovers and applies all of them automatically.
    /// </summary>
    /// <summary>
    /// Additional assemblies containing IEntityTypeConfiguration classes.
    /// Module assemblies (Workflow, Production) register their configurations here
    /// so AppDbContext discovers them without a direct project reference.
    ///
    /// Populated by each module's ServiceExtensions during DI registration.
    /// </summary>
    public static readonly List<System.Reflection.Assembly> ConfigurationAssemblies = [];

    /// <summary>
    /// Every persisted DateTime in a tenant database is a UTC instant, enforced in one place —
    /// the same convention <c>MasterDbContext</c> applies, so the two databases cannot drift
    /// apart on what a stored timestamp means.
    ///
    /// <para><c>DateOnly</c> is a different CLR type and is deliberately not matched, so
    /// <c>WorkOrder.DueDate</c> keeps its <c>date</c> column from P1-g. Store types are
    /// unchanged on both providers, so this adds no migration. See
    /// <see cref="Conversions.UtcDateTime"/> for why non-UTC writes fail instead of being
    /// converted.</para>
    /// </summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);

        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        configurationBuilder.Properties<DateTime?>().HaveConversion<UtcNullableDateTimeConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Apply configurations from the Persistence assembly (Identity entities, etc.)
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        // Apply configurations from module assemblies (Workflow, Production, etc.)
        foreach (var assembly in ConfigurationAssemblies)
            modelBuilder.ApplyConfigurationsFromAssembly(assembly);

        // Global query filter for soft delete — EVERY query automatically excludes deleted records
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (typeof(AuditableEntity).IsAssignableFrom(entityType.ClrType))
            {
                modelBuilder.Entity(entityType.ClrType)
                    .HasQueryFilter(CreateSoftDeleteFilter(entityType.ClrType));
            }
        }
    }

    /// <summary>
    /// Creates a lambda expression: entity => !((AuditableEntity)entity).IsDeleted
    /// This filter is applied to EVERY query on entities that inherit AuditableEntity.
    /// To include deleted records, use: _context.JobCards.IgnoreQueryFilters().ToListAsync()
    /// </summary>
    private static System.Linq.Expressions.LambdaExpression CreateSoftDeleteFilter(Type entityType)
    {
        var parameter = System.Linq.Expressions.Expression.Parameter(entityType, "e");
        var property = System.Linq.Expressions.Expression.Property(parameter, nameof(AuditableEntity.IsDeleted));
        var condition = System.Linq.Expressions.Expression.Equal(property, System.Linq.Expressions.Expression.Constant(false));
        return System.Linq.Expressions.Expression.Lambda(condition, parameter);
    }
}
