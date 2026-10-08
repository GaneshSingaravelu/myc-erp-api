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
/// Tests for the PostgreSQL MasterDb migration lineage (slice P1-i / DB-02, Master half).
///
/// THE LINEAGE CONTRACT
/// ====================
/// PostgreSQL has never had a deployed migration lineage, so its Master lineage starts from
/// ONE baseline migration that represents the CURRENT <see cref="MasterDbContext"/> model. It
/// deliberately does not replay SQL Server's migration history: provider parity means the same
/// current schema semantics, not the same historical migration IDs.
///
/// These tests pin that contract without a database. They resolve the migrations assembly the
/// same way <c>dotnet ef</c> does — through <see cref="MasterDbContextFactory.BuildOptions"/>
/// with <c>Database:Provider = PostgreSql</c> — and read the generated migration and snapshot
/// from it. Building options and reading the migrations assembly never opens a connection.
///
/// Applying the lineage to a real PostgreSQL server, and the runtime UTC and Tenant.Code
/// checks against it, were verified once against a throwaway database and recorded with this
/// slice; the full cross-provider parity suite is P1-k (DB-03), not this file.
/// </summary>
public class PostgreSqlMasterMigrationLineageTests
{
    private const string BaselineMigrationId = "20261007022731_InitialMasterBaseline";
    private const string LineageNamespace = "ERP.Persistence.PostgreSql.Migrations.MasterDb";

    // Never connected to. It only needs to be parseable by Npgsql when options are built.
    private const string PostgreSqlConnectionString =
        "Host=localhost;Database=erp_master_test;Username=erp_test;Password=not-a-real-password";

    private const string TimestampWithTimeZone = "timestamp with time zone";

    // ── Harness ───────────────────────────────────────────────────────────────

    private static MasterDbContext CreatePostgreSqlContext()
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

    private static IMigrationsAssembly MigrationsAssemblyOf(MasterDbContext context) =>
        context.GetService<IMigrationsAssembly>();

    /// <summary>
    /// The snapshot's model, finalized and initialized the way EF itself prepares it before
    /// diffing, so its relational (table/column/index) view can be read.
    /// </summary>
    private static IRelationalModel SnapshotRelationalModel(MasterDbContext context)
    {
        var snapshot = MigrationsAssemblyOf(context).ModelSnapshot
            ?? throw new InvalidOperationException("No MasterDbContext snapshot in the PostgreSQL migrations assembly.");

        IModel model = snapshot.Model;
        if (model is IMutableModel mutable)
            model = mutable.FinalizeModel();

        model = context.GetService<IModelRuntimeInitializer>().Initialize(model, designTime: true, validationLogger: null);
        return model.GetRelationalModel();
    }

    private static Migration BaselineMigration(MasterDbContext context)
    {
        var migrations = MigrationsAssemblyOf(context);
        return migrations.CreateMigration(migrations.Migrations[BaselineMigrationId], "Npgsql.EntityFrameworkCore.PostgreSQL");
    }

    private static IColumn Column(IRelationalModel model, string table, string column) =>
        model.FindTable(table, schema: null)?.FindColumn(column)
            ?? throw new InvalidOperationException($"Column {table}.{column} not found in the PostgreSQL Master snapshot.");

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

    // ── 1. One baseline, in the PostgreSQL assembly, under Migrations/MasterDb ─

    [Fact]
    public void PostgreSqlMasterLineage_StartsAtTheBaseline_AndDoesNotReplaySqlServerHistory()
    {
        using var context = CreatePostgreSqlContext();

        var ids = MigrationsAssemblyOf(context).Migrations.Keys.ToList();

        // The lineage starts at the PostgreSQL-native baseline. Future Master
        // migrations are expected to be appended.
        Assert.Equal(BaselineMigrationId, ids[0]);

        // Provider parity means equivalent current schema, not replaying
        // SQL Server's historical migration lineage.
        Assert.DoesNotContain("20260407122318_InitialMaster", ids);
        Assert.DoesNotContain("20260509162834_AddPasswordResetToken", ids);
    }

    [Fact]
    public void BaselineAndSnapshot_LiveInThePostgreSqlMasterDbNamespace()
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

    // ── 2. Snapshot equals the current model ─────────────────────────────────

    // If an entity or configuration changes without a new PostgreSQL migration, this fails —
    // the same check `dotnet ef migrations has-pending-model-changes` performs.
    [Fact]
    public void CurrentMasterModel_HasNoPendingChangesAgainstPostgreSqlSnapshot()
    {
        using var context = CreatePostgreSqlContext();

        Assert.False(context.Database.HasPendingModelChanges());
    }

    // ── 3. PostgreSQL-native store types ─────────────────────────────────────

    // Every DateTime in Master is a UTC instant (P1-h). On PostgreSQL that requires
    // `timestamp with time zone`; a `timestamp without time zone` column would reject the
    // UTC values the converter guarantees, or store them without their meaning.
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

    [Fact]
    public void TenantsPrimaryKey_IsUuid()
    {
        using var context = CreatePostgreSqlContext();
        var model = SnapshotRelationalModel(context);

        var primaryKey = model.FindTable("Tenants", schema: null)!.PrimaryKey!;

        var keyColumn = Assert.Single(primaryKey.Columns);
        Assert.Equal("Id", keyColumn.Name);
        Assert.Equal("uuid", keyColumn.StoreType);
    }

    // ── 4. Tenant.Code and its unique index (P1-f) ──────────────────────────

    // P1-f canonicalizes Tenant.Code in the application so lookups do not depend on
    // collation. The unique index is what makes that canonical form a real identity on
    // PostgreSQL, where comparison is case-sensitive.
    [Fact]
    public void TenantCode_IsRequiredVarchar50()
    {
        using var context = CreatePostgreSqlContext();
        var model = SnapshotRelationalModel(context);

        var code = Column(model, "Tenants", "Code");

        Assert.Equal("character varying(50)", code.StoreType);
        Assert.False(code.IsNullable);
    }

    [Fact]
    public void TenantCode_HasUniqueIndex_IX_Tenants_Code()
    {
        using var context = CreatePostgreSqlContext();
        var model = SnapshotRelationalModel(context);

        var index = Assert.Single(
            model.FindTable("Tenants", schema: null)!.Indexes,
            i => i.Name == "IX_Tenants_Code");

        Assert.True(index.IsUnique);
        Assert.Equal(["Code"], index.Columns.Select(c => c.Name));
    }

    // ── 5. Identifier naming unchanged: quoted PascalCase, no snake_case ─────

    [Fact]
    public void Identifiers_KeepCurrentPascalCaseNaming()
    {
        using var context = CreatePostgreSqlContext();
        var model = SnapshotRelationalModel(context);

        Assert.NotNull(model.FindTable("Tenants", schema: null));
        Assert.NotNull(model.FindTable("UserRoles", schema: null));
        Assert.Null(model.FindTable("tenants", schema: null));
        Assert.Null(model.FindTable("user_roles", schema: null));
        Assert.NotNull(model.FindTable("Users", schema: null)!.FindColumn("NormalizedUserName"));
    }

    // ── 6. The baseline migration itself ─────────────────────────────────────

    // The baseline must build the schema from EMPTY: only table and index creation, no
    // ALTERs of objects that would have had to exist already.
    [Fact]
    public void BaselineUp_CreatesTheSchemaFromEmpty()
    {
        using var context = CreatePostgreSqlContext();
        var migration = BaselineMigration(context);

        Assert.All(migration.UpOperations, op =>
            Assert.True(op is CreateTableOperation or CreateIndexOperation,
                $"Unexpected baseline operation {op.GetType().Name}."));

        var created = migration.UpOperations.OfType<CreateTableOperation>().Select(t => t.Name).ToList();
        Assert.Contains("Tenants", created);
        Assert.Contains("Users", created);

        // AuditLog is a tenant-database entity and must not appear in the Master lineage.
        Assert.DoesNotContain("AuditLogs", created);
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
