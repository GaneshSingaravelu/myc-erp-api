using ERP.DbMigrator;
using ERP.Identity.Entities;
using ERP.Persistence.Context;
using ERP.Persistence.PostgreSql.Extensions;
using ERP.Persistence.SqlServer.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.Configuration;

namespace ERP.UnitTests.Persistence;

/// <summary>
/// P2-a (ARCH-IMPL-01 / ARCH-IMPL-02) — the Company Master foundation, offline.
///
/// WHAT THIS PINS
/// ==============
/// - <see cref="Company"/> maps to <c>Companies</c> in the MASTER model only, on both providers.
/// - <c>Tenants.CompanyId</c> is NULLABLE (existing tenants have no company and none is
///   invented), with a Restrict foreign key and a plain index.
/// - Each provider's Master lineage gains exactly one incremental migration, whose Up/Down
///   contain only those schema additions — no data operations, no change to any other table.
/// - Each provider assembly still has exactly one Master snapshot, and it matches the model.
/// - The tenant (AppDb) model and snapshots know nothing about Company.
///
/// Nothing here opens a connection. Master contexts are built through
/// <see cref="MasterDbContextFactory.BuildOptions"/> exactly as <c>dotnet ef</c> builds them.
/// AppDb contexts are built directly on the provider seams rather than through the AppDb
/// design-time factory, which registers an extra configuration assembly into a shared static
/// list at call time (see TestEntityConfigurationRegistration).
///
/// Applying the migrations to real databases — the incremental-upgrade check L-10 — is a
/// separate, live gate and is NOT asserted here.
/// </summary>
public class CompanyMasterFoundationTests
{
    public enum Provider { SqlServer, PostgreSql }

    private const string SqlServerMigrationId = "20261012130942_AddCompanyFoundation";
    private const string PostgreSqlMigrationId = "20261012131005_AddCompanyFoundation";

    // Never connected to. They only need to be parseable by each provider.
    private const string SqlServerConnectionString =
        "Server=localhost;Database=erp_company_test;Trusted_Connection=true;TrustServerCertificate=true;";
    private const string PostgreSqlConnectionString =
        "Host=localhost;Database=erp_company_test;Username=erp_test;Password=not-a-real-password";

    // ── Harness ───────────────────────────────────────────────────────────────

    private static MasterDbContext CreateMaster(Provider provider)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = provider.ToString(),
                ["ConnectionStrings:MasterDb"] =
                    provider == Provider.SqlServer ? SqlServerConnectionString : PostgreSqlConnectionString,
            })
            .Build();

        return new MasterDbContext(MasterDbContextFactory.BuildOptions(configuration));
    }

    private static AppDbContext CreateAppDb(Provider provider)
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        if (provider == Provider.SqlServer)
            builder.UseSqlServerProvider(SqlServerConnectionString);
        else
            builder.UsePostgreSqlProvider(PostgreSqlConnectionString);

        return new AppDbContext(builder.Options);
    }

    private static IRelationalModel MasterRelational(Provider provider)
    {
        using var context = CreateMaster(provider);
        return context.GetService<IDesignTimeModel>().Model.GetRelationalModel();
    }

    private static IEntityType MasterEntity<T>(Provider provider)
    {
        using var context = CreateMaster(provider);
        return context.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(T))!;
    }

    private static string MigrationId(Provider provider) =>
        provider == Provider.SqlServer ? SqlServerMigrationId : PostgreSqlMigrationId;

    private static string ProviderName(Provider provider) =>
        provider == Provider.SqlServer ? "Microsoft.EntityFrameworkCore.SqlServer" : "Npgsql.EntityFrameworkCore.PostgreSQL";

    private static Migration CompanyMigration(Provider provider)
    {
        using var context = CreateMaster(provider);
        var migrations = context.GetService<IMigrationsAssembly>();
        return migrations.CreateMigration(migrations.Migrations[MigrationId(provider)], ProviderName(provider));
    }

    private static IModel? SnapshotModel(DbContext context) =>
        context.GetService<IMigrationsAssembly>().ModelSnapshot?.Model;

    // ── Company entity ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(Provider.SqlServer, "uniqueidentifier", "nvarchar(50)", "nvarchar(200)", "bit", "datetime2")]
    [InlineData(Provider.PostgreSql, "uuid", "character varying(50)", "character varying(200)", "boolean", "timestamp with time zone")]
    public void Company_MapsToCompanies_WithPinnedColumns(
        Provider provider, string guidType, string codeType, string nameType, string boolType, string dateTimeType)
    {
        var table = MasterRelational(provider).FindTable("Companies", schema: null);

        Assert.NotNull(table);
        Assert.Equal(
            ["Code", "CreatedAt", "Id", "IsActive", "Name"],
            table!.Columns.Select(c => c.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.All(table.Columns, c => Assert.False(c.IsNullable, $"Companies.{c.Name} must be NOT NULL"));

        Assert.Equal(guidType, table.FindColumn("Id")!.StoreType);
        Assert.Equal(codeType, table.FindColumn("Code")!.StoreType);
        Assert.Equal(nameType, table.FindColumn("Name")!.StoreType);
        Assert.Equal(boolType, table.FindColumn("IsActive")!.StoreType);
        Assert.Equal(dateTimeType, table.FindColumn("CreatedAt")!.StoreType);

        Assert.Equal("PK_Companies", table.PrimaryKey!.Name);
        Assert.Equal(["Id"], table.PrimaryKey.Columns.Select(c => c.Name).ToArray());
    }

    [Theory]
    [InlineData(Provider.SqlServer)]
    [InlineData(Provider.PostgreSql)]
    public void Company_Id_IsApplicationAssigned_LikeTenant(Provider provider)
    {
        Assert.Equal(ValueGenerated.Never, MasterEntity<Company>(provider).FindProperty(nameof(Company.Id))!.ValueGenerated);
        Assert.Equal(ValueGenerated.Never, MasterEntity<Tenant>(provider).FindProperty(nameof(Tenant.Id))!.ValueGenerated);
    }

    // Uniqueness of the stored code only. Canonical form (Trim + ToLowerInvariant) is the
    // application's job, as for Tenant.Code — so the index is plain: no filter, no collation.
    [Theory]
    [InlineData(Provider.SqlServer)]
    [InlineData(Provider.PostgreSql)]
    public void Company_Code_HasAPlainUniqueIndex(Provider provider)
    {
        var index = Assert.Single(
            MasterRelational(provider).FindTable("Companies", schema: null)!.Indexes,
            i => i.Name == "IX_Companies_Code");

        Assert.True(index.IsUnique);
        Assert.Equal(["Code"], index.Columns.Select(c => c.Name).ToArray());
        Assert.Null(index.Filter);
        Assert.Null(index.Columns.Single().Collation);
    }

    // ── Tenant → Company relationship ────────────────────────────────────────

    [Theory]
    [InlineData(Provider.SqlServer)]
    [InlineData(Provider.PostgreSql)]
    public void Tenant_CompanyId_IsNullable_WithRestrictForeignKey_AndPlainIndex(Provider provider)
    {
        var tenant = MasterEntity<Tenant>(provider);
        var companyId = tenant.FindProperty(nameof(Tenant.CompanyId))!;

        Assert.Equal(typeof(Guid?), companyId.ClrType);
        Assert.True(companyId.IsNullable);

        var foreignKey = Assert.Single(tenant.GetForeignKeys(), fk => fk.PrincipalEntityType.ClrType == typeof(Company));
        Assert.False(foreignKey.IsRequired);
        Assert.Equal(DeleteBehavior.Restrict, foreignKey.DeleteBehavior);

        var tenants = MasterRelational(provider).FindTable("Tenants", schema: null)!;
        var constraint = Assert.Single(tenants.ForeignKeyConstraints, f => f.Name == "FK_Tenants_Companies_CompanyId");
        Assert.Equal(["CompanyId"], constraint.Columns.Select(c => c.Name).ToArray());
        Assert.Equal("Companies", constraint.PrincipalTable.Name);
        Assert.Equal(["Id"], constraint.PrincipalColumns.Select(c => c.Name).ToArray());
        Assert.Equal(ReferentialAction.Restrict, constraint.OnDeleteAction);

        var index = Assert.Single(tenants.Indexes, i => i.Name == "IX_Tenants_CompanyId");
        Assert.False(index.IsUnique);
        Assert.Equal(["CompanyId"], index.Columns.Select(c => c.Name).ToArray());
    }

    // The only change to Tenants is the one added nullable column.
    [Theory]
    [InlineData(Provider.SqlServer)]
    [InlineData(Provider.PostgreSql)]
    public void Tenants_GainsOnlyCompanyId(Provider provider)
    {
        var tenants = MasterRelational(provider).FindTable("Tenants", schema: null)!;

        Assert.Equal(
            ["Code", "CompanyId", "ConnectionString", "CreatedAt", "DbProvider", "Id", "IsActive", "Name"],
            tenants.Columns.Select(c => c.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(["CompanyId"], tenants.Columns.Where(c => c.IsNullable).Select(c => c.Name).ToArray());
    }

    // ── Master only — the tenant (AppDb) side never sees Company ─────────────

    [Theory]
    [InlineData(Provider.SqlServer)]
    [InlineData(Provider.PostgreSql)]
    public void AppDbModelAndSnapshot_HaveNoCompany(Provider provider)
    {
        using var context = CreateAppDb(provider);

        Assert.Null(context.Model.FindEntityType(typeof(Company)));
        Assert.DoesNotContain(context.Model.GetEntityTypes(), e => e.GetTableName() == "Companies");

        var snapshot = SnapshotModel(context);
        Assert.NotNull(snapshot);
        Assert.DoesNotContain(snapshot!.GetEntityTypes(),
            e => e.Name == typeof(Company).FullName || e.GetTableName() == "Companies");
    }

    // ── Lineage and snapshot ─────────────────────────────────────────────────

    [Theory]
    [InlineData(Provider.SqlServer, "ERP.Persistence.SqlServer", "20260509162834_AddPasswordResetToken")]
    [InlineData(Provider.PostgreSql, "ERP.Persistence.PostgreSql", "20261007022731_InitialMasterBaseline")]
    public void MasterLineage_EndsWithTheCompanyMigration_RightAfterThePhase1Head(
        Provider provider, string assemblyName, string phase1Head)
    {
        using var context = CreateMaster(provider);
        var migrations = context.GetService<IMigrationsAssembly>();
        var ids = migrations.Migrations.Keys.ToList();

        Assert.Equal(MigrationId(provider), ids[^1]);
        Assert.Equal(phase1Head, ids[^2]);

        var type = migrations.Migrations[MigrationId(provider)];
        Assert.Equal(assemblyName, type.Assembly.GetName().Name);
        Assert.Equal($"{assemblyName}.Migrations.MasterDb", type.Namespace);
    }

    // The SQL Server Master snapshot has always lived in the root Migrations namespace while
    // later migrations live under MasterDb; generating this migration left it there.
    [Theory]
    [InlineData(Provider.SqlServer, "ERP.Persistence.SqlServer.Migrations")]
    [InlineData(Provider.PostgreSql, "ERP.Persistence.PostgreSql.Migrations.MasterDb")]
    public void EachProviderAssembly_HasExactlyOneMasterSnapshot(Provider provider, string snapshotNamespace)
    {
        using var context = CreateMaster(provider);
        var assembly = context.GetService<IMigrationsAssembly>().Assembly;

        var snapshots = assembly.GetTypes()
            .Where(t => typeof(ModelSnapshot).IsAssignableFrom(t) && !t.IsAbstract)
            .Where(t => t.GetCustomAttributes(typeof(DbContextAttribute), inherit: false)
                .Cast<DbContextAttribute>()
                .Any(a => a.ContextType == typeof(MasterDbContext)))
            .ToList();

        var snapshot = Assert.Single(snapshots);
        Assert.Equal(snapshotNamespace, snapshot.Namespace);
    }

    // Before P2-a only the PostgreSQL Master lineage had this guard; SQL Server drift went unseen.
    [Theory]
    [InlineData(Provider.SqlServer)]
    [InlineData(Provider.PostgreSql)]
    public void MasterModel_HasNoPendingChangesAgainstItsSnapshot(Provider provider)
    {
        using var context = CreateMaster(provider);

        Assert.False(context.Database.HasPendingModelChanges());
    }

    // ── The migration itself: schema additions only ──────────────────────────

    [Theory]
    [InlineData(Provider.SqlServer)]
    [InlineData(Provider.PostgreSql)]
    public void CompanyMigration_Up_IsExactlyTheSchemaAdditions(Provider provider)
    {
        var up = CompanyMigration(provider).UpOperations;

        Assert.Collection(up,
            op =>
            {
                var add = Assert.IsType<AddColumnOperation>(op);
                Assert.Equal(("Tenants", "CompanyId"), (add.Table, add.Name));
                Assert.Equal(typeof(Guid), add.ClrType);
                Assert.True(add.IsNullable);
                Assert.Null(add.DefaultValue);
                Assert.Null(add.DefaultValueSql);
            },
            op =>
            {
                var create = Assert.IsType<CreateTableOperation>(op);
                Assert.Equal("Companies", create.Name);
                Assert.Equal(["Id", "Code", "Name", "IsActive", "CreatedAt"], create.Columns.Select(c => c.Name).ToArray());
            },
            op =>
            {
                var index = Assert.IsType<CreateIndexOperation>(op);
                Assert.Equal(("IX_Tenants_CompanyId", "Tenants", false), (index.Name, index.Table, index.IsUnique));
            },
            op =>
            {
                var index = Assert.IsType<CreateIndexOperation>(op);
                Assert.Equal(("IX_Companies_Code", "Companies", true), (index.Name, index.Table, index.IsUnique));
            },
            op =>
            {
                var fk = Assert.IsType<AddForeignKeyOperation>(op);
                Assert.Equal(("FK_Tenants_Companies_CompanyId", "Tenants", "Companies"), (fk.Name, fk.Table, fk.PrincipalTable));
                Assert.Equal(ReferentialAction.Restrict, fk.OnDelete);
            });
    }

    [Theory]
    [InlineData(Provider.SqlServer)]
    [InlineData(Provider.PostgreSql)]
    public void CompanyMigration_Down_RemovesExactlyTheAdditions(Provider provider)
    {
        var down = CompanyMigration(provider).DownOperations;

        Assert.Collection(down,
            op => Assert.Equal(("FK_Tenants_Companies_CompanyId", "Tenants"),
                (Assert.IsType<DropForeignKeyOperation>(op).Name, ((DropForeignKeyOperation)op).Table)),
            op => Assert.Equal("Companies", Assert.IsType<DropTableOperation>(op).Name),
            op => Assert.Equal(("IX_Tenants_CompanyId", "Tenants"),
                (Assert.IsType<DropIndexOperation>(op).Name, ((DropIndexOperation)op).Table)),
            op => Assert.Equal(("CompanyId", "Tenants"),
                (Assert.IsType<DropColumnOperation>(op).Name, ((DropColumnOperation)op).Table)));
    }

    // No seed rows, no raw SQL, no data rewrite: existing tenant rows are left exactly as they are.
    [Theory]
    [InlineData(Provider.SqlServer)]
    [InlineData(Provider.PostgreSql)]
    public void CompanyMigration_ContainsNoDataOperations(Provider provider)
    {
        var migration = CompanyMigration(provider);
        var all = migration.UpOperations.Concat(migration.DownOperations).ToList();

        Assert.DoesNotContain(all, op => op is SqlOperation or InsertDataOperation or UpdateDataOperation
            or DeleteDataOperation or AlterColumnOperation or RenameColumnOperation or RenameTableOperation);
    }
}
