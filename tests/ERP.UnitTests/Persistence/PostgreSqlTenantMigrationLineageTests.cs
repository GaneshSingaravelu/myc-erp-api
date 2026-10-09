using ERP.DbMigrator;
using ERP.Persistence.Context;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.Configuration;

namespace ERP.UnitTests.Persistence;

/// <summary>
/// Tests for the PostgreSQL tenant (AppDb) migration lineage (slice P1-j / DB-02, Tenant half).
///
/// THE LINEAGE CONTRACT
/// ====================
/// PostgreSQL tenant databases have never had a deployed migration lineage, so theirs starts
/// from ONE baseline migration that represents the CURRENT <see cref="AppDbContext"/> model.
/// It deliberately does not replay SQL Server's three historical tenant migrations: provider
/// parity means the same current schema semantics, not the same historical migration IDs.
///
/// WHAT IS PINNED WHERE
/// ====================
/// Exact shapes — the eight tables, the five foreign keys — are asserted against the BASELINE
/// migration, which never changes once shipped. Current-model contracts (UTC timestamps,
/// <c>DueDate</c> as <c>date</c>, the unique indexes, no pending changes) are asserted against
/// the SNAPSHOT, which moves forward with every later migration. Future PostgreSQL tenant
/// migrations are therefore appended without rewriting these tests.
///
/// WHY THE DESIGN-TIME FACTORY
/// ===========================
/// Options come from <see cref="AppDbContextFactory.BuildOptions"/> — the path `dotnet ef`
/// uses — because it registers the Workflow module's configurations as well as Production's.
/// Without Workflow, ProcessTemplates and ProcessSteps are absent from the model and the
/// pending-changes check would compare against an incomplete schema. Building options and
/// reading the migrations assembly never opens a connection.
///
/// Applying the lineage to a real PostgreSQL server, with the UTC and DateOnly runtime checks,
/// was verified once against a throwaway database and recorded with this slice; the full
/// cross-provider parity suite is P1-k (DB-03), not this file.
/// </summary>
public class PostgreSqlTenantMigrationLineageTests
{
    private const string BaselineMigrationId = "20261008162932_InitialTenantBaseline";
    private const string LineageNamespace = "ERP.Persistence.PostgreSql.Migrations.AppDb";
    private const string MasterBaselineMigrationId = "20261007022731_InitialMasterBaseline";

    // Never connected to. It only needs to be parseable by Npgsql when options are built.
    private const string PostgreSqlConnectionString =
        "Host=localhost;Database=erp_tenant_test;Username=erp_test;Password=not-a-real-password";

    private const string TimestampWithTimeZone = "timestamp with time zone";

    private static readonly string[] TenantTables =
    [
        "AuditLogs", "JobCards", "Plants", "ProcessSteps",
        "ProcessTemplates", "UserPlantAccess", "UserPreferences", "WorkOrders",
    ];

    // ── Harness ───────────────────────────────────────────────────────────────

    private static DbContextOptions<AppDbContext> PostgreSqlOptions()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "PostgreSql",
                ["Tenants:0:ConnectionString"] = PostgreSqlConnectionString,
            })
            .Build();

        return AppDbContextFactory.BuildOptions(configuration);
    }

    private static AppDbContext CreatePostgreSqlContext() => new(PostgreSqlOptions());

    private static MasterDbContext CreatePostgreSqlMasterContext()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "PostgreSql",
                ["ConnectionStrings:MasterDb"] = PostgreSqlConnectionString,
            })
            .Build();

        return new MasterDbContext(MasterDbContextFactory.BuildOptions(configuration));
    }

    private static IMigrationsAssembly MigrationsAssemblyOf(DbContext context) =>
        context.GetService<IMigrationsAssembly>();

    /// <summary>
    /// The snapshot's model, finalized and initialized the way EF itself prepares it before
    /// diffing, so its relational (table/column/index) view can be read.
    /// </summary>
    private static IRelationalModel SnapshotRelationalModel(AppDbContext context)
    {
        var snapshot = MigrationsAssemblyOf(context).ModelSnapshot
            ?? throw new InvalidOperationException("No AppDbContext snapshot in the PostgreSQL migrations assembly.");

        IModel model = snapshot.Model;
        if (model is IMutableModel mutable)
            model = mutable.FinalizeModel();

        model = context.GetService<IModelRuntimeInitializer>().Initialize(model, designTime: true, validationLogger: null);
        return model.GetRelationalModel();
    }

    private static Migration BaselineMigration(AppDbContext context)
    {
        var migrations = MigrationsAssemblyOf(context);
        return migrations.CreateMigration(migrations.Migrations[BaselineMigrationId], "Npgsql.EntityFrameworkCore.PostgreSQL");
    }

    private static ITable Table(IRelationalModel model, string table) =>
        model.FindTable(table, schema: null)
            ?? throw new InvalidOperationException($"Table {table} not found in the PostgreSQL tenant snapshot.");

    /// <summary>
    /// Columns whose mapped ENTITY property is of type <typeparamref name="T"/> (or <c>T?</c>).
    ///
    /// <para>Selected by the property's CLR type, not the column's provider CLR type: a column
    /// wrongly typed as <c>text</c> maps to a <c>string</c> provider type and would silently
    /// drop out of a provider-type filter — exactly the column the assertion exists to catch.</para>
    /// </summary>
    private static List<IColumn> ColumnsForPropertiesOf<T>(IRelationalModel model) where T : struct =>
        model.Tables
            .SelectMany(t => t.Columns)
            .Where(c => c.PropertyMappings.Any(m =>
                (Nullable.GetUnderlyingType(m.Property.ClrType) ?? m.Property.ClrType) == typeof(T)))
            .ToList();

    // ── 1. Lineage: starts at the baseline, in the PostgreSQL assembly ───────

    [Fact]
    public void PostgreSqlTenantLineage_StartsAtTheBaseline_AndDoesNotReplaySqlServerHistory()
    {
        using var context = CreatePostgreSqlContext();

        var ids = MigrationsAssemblyOf(context).Migrations.Keys.ToList();

        // The lineage starts at the PostgreSQL-native baseline. Future tenant
        // migrations are expected to be appended.
        Assert.Equal(BaselineMigrationId, ids[0]);

        // Provider parity means equivalent current schema, not replaying
        // SQL Server's historical tenant migration lineage.
        Assert.DoesNotContain("20260407122342_InitialTenant", ids);
        Assert.DoesNotContain("20260502180307_AddPlantIdToWorkOrders", ids);
        Assert.DoesNotContain("20261001112110_ChangeWorkOrderDueDateToDate", ids);
    }

    [Fact]
    public void BaselineAndSnapshot_LiveInThePostgreSqlAppDbNamespace()
    {
        using var context = CreatePostgreSqlContext();
        var migrations = MigrationsAssemblyOf(context);

        var migrationType = migrations.Migrations[BaselineMigrationId];
        Assert.Equal("ERP.Persistence.PostgreSql", migrationType.Assembly.GetName().Name);
        Assert.Equal(LineageNamespace, migrationType.Namespace);

        var snapshot = migrations.ModelSnapshot;
        Assert.NotNull(snapshot);
        Assert.Equal(LineageNamespace, snapshot.GetType().Namespace);
    }

    // Both lineages share one assembly. Each context must see only its own migrations and
    // snapshot — the P1-i Master lineage is unaffected by adding the tenant one.
    [Fact]
    public void MasterAndTenantLineages_StaySeparate()
    {
        using var tenant = CreatePostgreSqlContext();
        using var master = CreatePostgreSqlMasterContext();

        var tenantIds = MigrationsAssemblyOf(tenant).Migrations.Keys.ToList();
        var masterIds = MigrationsAssemblyOf(master).Migrations.Keys.ToList();

        Assert.Equal(MasterBaselineMigrationId, masterIds[0]);
        Assert.DoesNotContain(BaselineMigrationId, masterIds);
        Assert.DoesNotContain(MasterBaselineMigrationId, tenantIds);

        Assert.Equal(
            "ERP.Persistence.PostgreSql.Migrations.MasterDb",
            MigrationsAssemblyOf(master).ModelSnapshot!.GetType().Namespace);
    }

    // ── 2. Snapshot equals the current model ─────────────────────────────────

    // If an entity or configuration changes without a new PostgreSQL migration, this fails —
    // the same check `dotnet ef migrations has-pending-model-changes` performs.
    //
    // WHY THIS ONE TEST DISABLES SERVICE-PROVIDER CACHING
    // ===================================================
    // EF caches the built model per internal service provider, and that provider is shared
    // with other test classes that configure Npgsql for AppDbContext. Those classes
    // deliberately see a Production-only model (TestEntityConfigurationRegistration registers
    // only Production), so whichever class builds first decides the cached model. When one of
    // them wins, this check compares the snapshot against a model WITHOUT the Workflow tables
    // and reports ProcessTemplates/ProcessSteps as pending — measured at 2 failures in 10
    // full-suite runs, with Workflow present in the configuration list at the time.
    //
    // An uncached provider builds the model here, after AppDbContextFactory has registered
    // Workflow, so the comparison is against the full tenant model `dotnet ef` sees. It changes
    // nothing for any other test: no model-cache key factory, no shared registration change.
    [Fact]
    public void CurrentTenantModel_HasNoPendingChangesAgainstPostgreSqlSnapshot()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>(PostgreSqlOptions())
            .EnableServiceProviderCaching(false)
            .Options;
        using var context = new AppDbContext(options);

        Assert.False(context.Database.HasPendingModelChanges());
    }

    // ── 3. PostgreSQL-native store types ─────────────────────────────────────

    // Every DateTime in a tenant database is a UTC instant (P1-h). On PostgreSQL that requires
    // `timestamp with time zone`.
    [Fact]
    public void EveryDateTimeColumn_IsTimestampWithTimeZone()
    {
        using var context = CreatePostgreSqlContext();
        var model = SnapshotRelationalModel(context);

        var dateTimeColumns = ColumnsForPropertiesOf<DateTime>(model);

        Assert.NotEmpty(dateTimeColumns);
        Assert.All(dateTimeColumns, c => Assert.Equal(TimestampWithTimeZone, c.StoreType));
    }

    [Fact]
    public void EveryGuidColumn_IsUuid()
    {
        using var context = CreatePostgreSqlContext();
        var model = SnapshotRelationalModel(context);

        var guidColumns = ColumnsForPropertiesOf<Guid>(model);

        Assert.NotEmpty(guidColumns);
        Assert.All(guidColumns, c => Assert.Equal("uuid", c.StoreType));
    }

    // Selected by entity property type, so nullable bool? properties are included too.
    [Fact]
    public void EveryBooleanColumn_IsBoolean()
    {
        using var context = CreatePostgreSqlContext();
        var model = SnapshotRelationalModel(context);

        var boolColumns = ColumnsForPropertiesOf<bool>(model);

        Assert.NotEmpty(boolColumns);
        Assert.All(boolColumns, c => Assert.Equal("boolean", c.StoreType));
    }

    // P1-g: WorkOrder.DueDate is a calendar date, not an instant. It must stay a `date`
    // column — never a timestamp — and must not pick up the UTC DateTime conversion.
    [Fact]
    public void WorkOrderDueDate_IsNullableDate()
    {
        using var context = CreatePostgreSqlContext();
        var model = SnapshotRelationalModel(context);

        var dueDate = Table(model, "WorkOrders").FindColumn("DueDate")
            ?? throw new InvalidOperationException("WorkOrders.DueDate not found.");

        Assert.Equal("date", dueDate.StoreType);
        Assert.True(dueDate.IsNullable);
        Assert.All(dueDate.PropertyMappings, m =>
            Assert.Equal(typeof(DateOnly?), m.Property.ClrType));
    }

    // ── 4. Unique indexes ────────────────────────────────────────────────────

    [Theory]
    [InlineData("JobCards", "IX_JobCards_JobCardNumber")]
    [InlineData("Plants", "IX_Plants_Code")]
    [InlineData("ProcessTemplates", "IX_ProcessTemplates_Name_Version")]
    [InlineData("UserPlantAccess", "IX_UserPlantAccess_UserId_PlantId")]
    [InlineData("UserPreferences", "IX_UserPreferences_UserId_Key")]
    [InlineData("WorkOrders", "IX_WorkOrders_OrderNumber")]
    public void UniqueIndex_ExistsAndIsUnique(string table, string indexName)
    {
        using var context = CreatePostgreSqlContext();
        var model = SnapshotRelationalModel(context);

        var index = Assert.Single(Table(model, table).Indexes, i => i.Name == indexName);

        Assert.True(index.IsUnique);
    }

    // ── 5. Identifier naming unchanged: quoted PascalCase, no snake_case ─────

    [Fact]
    public void Identifiers_KeepCurrentPascalCaseNaming()
    {
        using var context = CreatePostgreSqlContext();
        var model = SnapshotRelationalModel(context);

        Assert.NotNull(model.FindTable("WorkOrders", schema: null));
        Assert.NotNull(model.FindTable("UserPlantAccess", schema: null));
        Assert.Null(model.FindTable("work_orders", schema: null));
        Assert.Null(model.FindTable("user_plant_access", schema: null));
        Assert.NotNull(Table(model, "WorkOrders").FindColumn("OrderNumber"));
    }

    // ── 6. The baseline migration itself ─────────────────────────────────────

    // The baseline must build the whole tenant schema from EMPTY: only table and index
    // creation, and exactly the eight tenant tables. The baseline never changes once shipped,
    // so pinning its exact table set is durable.
    [Fact]
    public void BaselineUp_CreatesExactlyTheEightTenantTablesFromEmpty()
    {
        using var context = CreatePostgreSqlContext();
        var migration = BaselineMigration(context);

        Assert.All(migration.UpOperations, op =>
            Assert.True(op is CreateTableOperation or CreateIndexOperation,
                $"Unexpected baseline operation {op.GetType().Name}."));

        var created = migration.UpOperations.OfType<CreateTableOperation>().Select(t => t.Name).Order();
        Assert.Equal(TenantTables.Order(), created);
    }

    [Fact]
    public void BaselineUp_CreatesTheFiveForeignKeys()
    {
        using var context = CreatePostgreSqlContext();
        var migration = BaselineMigration(context);

        var foreignKeys = migration.UpOperations
            .OfType<CreateTableOperation>()
            .SelectMany(t => t.ForeignKeys)
            .Select(fk => fk.Name)
            .Order();

        Assert.Equal(
            [
                "FK_JobCards_JobCards_ParentJobCardId",
                "FK_JobCards_WorkOrders_WorkOrderId",
                "FK_ProcessSteps_ProcessSteps_NextStepId",
                "FK_ProcessSteps_ProcessTemplates_TemplateId",
                "FK_UserPlantAccess_Plants_PlantId",
            ],
            foreignKeys);
    }

    [Fact]
    public void BaselineUp_UsesNoSqlServerStoreTypes()
    {
        using var context = CreatePostgreSqlContext();
        var migration = BaselineMigration(context);

        var storeTypes = migration.UpOperations
            .OfType<CreateTableOperation>()
            .SelectMany(t => t.Columns)
            .Select(c => c.ColumnType ?? string.Empty)
            .ToList();

        Assert.All(storeTypes, type =>
        {
            Assert.DoesNotContain("nvarchar", type, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("datetime2", type, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("uniqueidentifier", type, StringComparison.OrdinalIgnoreCase);
        });
    }

    // Down must exactly undo Up: every table the baseline creates, and nothing else, is dropped.
    [Fact]
    public void BaselineDown_DropsExactlyTheTablesUpCreates()
    {
        using var context = CreatePostgreSqlContext();
        var migration = BaselineMigration(context);

        var created = migration.UpOperations.OfType<CreateTableOperation>().Select(t => t.Name).Order();
        var dropped = migration.DownOperations.OfType<DropTableOperation>().Select(t => t.Name).Order();

        Assert.Equal(created, dropped);
        Assert.All(migration.DownOperations, op => Assert.IsType<DropTableOperation>(op));
    }
}
