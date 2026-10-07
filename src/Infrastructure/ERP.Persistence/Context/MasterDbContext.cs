using ERP.Persistence.Conversions;
using ERP.SharedKernel.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Persistence.Context;

/// <summary>
/// MASTER DB CONTEXT — Manages the Master database containing identity, access control, and navigation.
///
/// MASTER DB vs TENANT DB:
/// ========================
///   MasterDbContext (this) → ERP_Master: Tenants, Users, Roles, Permissions, Sessions, Menus, FeatureFlags
///   AppDbContext           → ERP_{Code}: Plants, WorkOrders, JobCards, ProcessTemplates (per-tenant)
///
/// WHY TWO CONTEXTS?
/// ==================
///   - Authentication queries Master DB (user exists? password valid? roles for this tenant?)
///   - Business queries hit Tenant DB (work orders, job cards for this specific company)
///   - Master DB is shared across all tenants; Tenant DBs are isolated per company
///   - Prevents cross-database FK issues (all Master DB FKs are within the same database)
///
/// REGISTRATION: Scoped lifetime (one per HTTP request), configured with a fixed connection string.
/// </summary>
public class MasterDbContext : DbContext
{
    public MasterDbContext(DbContextOptions<MasterDbContext> options) : base(options)
    {
    }

    /// <summary>
    /// Additional assemblies containing IEntityTypeConfiguration classes for Master DB entities.
    /// The Identity assembly registers its configurations here.
    /// </summary>
    public static readonly List<System.Reflection.Assembly> ConfigurationAssemblies = [];

    /// <summary>
    /// Every persisted DateTime in this database is a UTC instant, enforced in one place.
    ///
    /// <para>Applied as a CONVENTION rather than per property: it covers every mapped
    /// <c>DateTime</c>/<c>DateTime?</c> including ones added later, so a new timestamp cannot
    /// be silently left out. <c>DateOnly</c> is a different CLR type and is deliberately not
    /// matched — <c>WorkOrder.DueDate</c> keeps its <c>date</c> column.</para>
    ///
    /// <para>Store types are unchanged on both providers, so this adds no migration. See
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

        // Apply configurations from this assembly (base configurations).
        // Exclude AuditLogConfiguration — AuditLog is a tenant-DB-only entity
        // (populated by AuditLogInterceptor wired in TenantDbContextFactory).
        modelBuilder.ApplyConfigurationsFromAssembly(
            typeof(MasterDbContext).Assembly,
            type => type != typeof(Configurations.AuditLogConfiguration));

        // Apply configurations from registered assemblies (Identity entities, etc.)
        foreach (var assembly in ConfigurationAssemblies)
            modelBuilder.ApplyConfigurationsFromAssembly(assembly);

        // Global query filter for soft delete on AuditableEntity types
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
    /// Creates a lambda: entity => !((AuditableEntity)entity).IsDeleted
    /// Applied to EVERY query on AuditableEntity types.
    /// Use .IgnoreQueryFilters() to include deleted records.
    /// </summary>
    private static System.Linq.Expressions.LambdaExpression CreateSoftDeleteFilter(Type entityType)
    {
        var parameter = System.Linq.Expressions.Expression.Parameter(entityType, "e");
        var property = System.Linq.Expressions.Expression.Property(parameter, nameof(AuditableEntity.IsDeleted));
        var condition = System.Linq.Expressions.Expression.Equal(property, System.Linq.Expressions.Expression.Constant(false));
        return System.Linq.Expressions.Expression.Lambda(condition, parameter);
    }
}
