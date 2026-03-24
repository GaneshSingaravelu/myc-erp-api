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
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Automatically find and apply ALL entity configurations in this assembly
        // (any class implementing IEntityTypeConfiguration<T>)
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

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
