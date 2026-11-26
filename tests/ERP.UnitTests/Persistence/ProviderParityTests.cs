using System.Text.RegularExpressions;
using ERP.DbMigrator;
using ERP.Persistence.Context;
using ERP.Persistence.Conversions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;

namespace ERP.UnitTests.Persistence;

/// <summary>
/// OFFLINE PROVIDER PARITY (slice P1-k Phase 1 / DB-03) — SQL Server vs PostgreSQL.
///
/// WHAT THIS PROVES
/// ================
/// The same MasterDbContext and AppDbContext model, configured for each engine through the
/// same design-time factories `dotnet ef` uses, describes the same schema: the same entities,
/// properties, tables, columns, keys, indexes and foreign keys, under the same names, with
/// store types that differ ONLY where the approved SQL Server → PostgreSQL mapping says they
/// should. Provider differences that are approved — migration lineages, store-type spellings,
/// the one nullable-unique-index filter — are asserted explicitly rather than ignored.
///
/// HOW THE EXPECTATIONS WORK
/// =========================
/// Every expectation below is an INDEPENDENT pinned value from the approved P1-k handoff or,
/// where the handoff defers to the baseline, from the verified model at commit 08db90b.
/// Nothing here is "provider A equals provider B" alone: two models that drifted together
/// would still match each other, so each comparison is paired with a pinned expectation.
///
/// WHY UNCACHED SERVICE PROVIDERS
/// ==============================
/// EF caches the built model per internal service provider, shared across test classes.
/// Other classes deliberately build a Production-only AppDbContext model, so a cached model
/// can lack the Workflow tables (see PostgreSqlTenantMigrationLineageTests). Each model here
/// is built once, on its own uncached provider, AFTER the design-time factory has registered
/// every configuration assembly. No model-cache key factory, no shared registration change.
///
/// Building options and models never opens a connection. Live, against-a-database parity is
/// Phase 2 and is not part of this file.
/// </summary>
public class ProviderParityTests
{
    public enum Ctx { Master, AppDb }

    public enum Provider { SqlServer, PostgreSql }

    // Never connected to. They only need to be parseable by each provider.
    private const string SqlServerConnectionString =
        "Server=localhost;Database=erp_parity_test;Trusted_Connection=true;TrustServerCertificate=true;";
    private const string PostgreSqlConnectionString =
        "Host=localhost;Database=erp_parity_test;Username=erp_test;Password=not-a-real-password";

    // ── Approved O-2 store-type mapping: SQL Server → PostgreSQL ─────────────

    private static readonly Dictionary<string, string> ApprovedStoreTypeMap = new()
    {
        ["uniqueidentifier"] = "uuid",
        ["datetime2"] = "timestamp with time zone",
        ["bit"] = "boolean",
        ["int"] = "integer",
        ["nvarchar(max)"] = "text",
        ["date"] = "date",
    };

    private static readonly Regex SizedNVarChar = new(@"^nvarchar\((\d+)\)$");

    // ── Pinned inventories (verified against the 08db90b baseline) ───────────
    // P2-a (ARCH-IMPL-01/02) adds the Companies table and nullable Tenants.CompanyId to Master.

    private static readonly string[] MasterTables =
    [
        "Companies", "FeatureFlags", "IdentityAuditLogs", "MenuPermissions", "Menus", "Modules",
        "Permissions", "RolePermissions", "Roles", "TenantFeatureFlags", "Tenants",
        "UserPasswordHistory", "UserRoles", "UserSessions", "UserTenants", "Users",
    ];

    private static readonly string[] AppDbTables =
    [
        "AuditLogs", "JobCards", "Plants", "ProcessSteps",
        "ProcessTemplates", "UserPlantAccess", "UserPreferences", "WorkOrders",
    ];

    private static readonly string[] MasterDateTimeColumns =
    [
        "Companies.CreatedAt",
        "FeatureFlags.CreatedAt",
        "IdentityAuditLogs.CreatedAt",
        "Menus.CreatedAt",
        "RolePermissions.CreatedAt",
        "RolePermissions.ValidFrom",
        "RolePermissions.ValidTo",
        "Roles.CreatedAt",
        "Roles.DeletedAt",
        "Roles.LastModifiedAt",
        "TenantFeatureFlags.EnabledAt",
        "Tenants.CreatedAt",
        "UserPasswordHistory.ChangedAt",
        "UserRoles.AssignedAt",
        "Users.CreatedAt",
        "Users.DeletedAt",
        "Users.LastLoginAt",
        "Users.LastModifiedAt",
        "Users.LastPasswordChangedAt",
        "Users.LockoutEnd",
        "Users.PasswordResetTokenExpiry",
        "UserSessions.CreatedAt",
        "UserSessions.ExpiresAt",
        "UserSessions.LastAccessedAt",
        "UserSessions.RefreshTokenExpiry",
        "UserSessions.RevokedAt",
        "UserTenants.JoinedAt",
    ];

    private static readonly string[] AppDbDateTimeColumns =
    [
        "AuditLogs.CreatedAt",
        "JobCards.CompletedAt",
        "JobCards.CreatedAt",
        "JobCards.DeletedAt",
        "JobCards.LastModifiedAt",
        "JobCards.StartedAt",
        "Plants.CreatedAt",
        "ProcessSteps.CreatedAt",
        "ProcessSteps.DeletedAt",
        "ProcessSteps.LastModifiedAt",
        "ProcessTemplates.CreatedAt",
        "ProcessTemplates.DeletedAt",
        "ProcessTemplates.LastModifiedAt",
        "UserPlantAccess.GrantedAt",
        "WorkOrders.CompletedAt",
        "WorkOrders.CreatedAt",
        "WorkOrders.DeletedAt",
        "WorkOrders.LastModifiedAt",
        "WorkOrders.ReleasedAt",
    ];

    // nvarchar(n) ↔ character varying(n): the complete sized-string inventory with lengths.
    private static readonly Dictionary<string, int> MasterSizedStringColumns = new()
    {
        ["Companies.Code"] = 50, ["Companies.Name"] = 200,
        ["FeatureFlags.Code"] = 100, ["FeatureFlags.Description"] = 500, ["FeatureFlags.Name"] = 200,
        ["IdentityAuditLogs.Action"] = 50, ["IdentityAuditLogs.EntityId"] = 100, ["IdentityAuditLogs.EntityType"] = 200,
        ["IdentityAuditLogs.IpAddress"] = 100, ["IdentityAuditLogs.UserAgent"] = 500,
        ["Menus.Code"] = 100, ["Menus.ComponentName"] = 200, ["Menus.FeatureFlag"] = 100,
        ["Menus.Icon"] = 100, ["Menus.Name"] = 200, ["Menus.Route"] = 300,
        ["Modules.Code"] = 100, ["Modules.Icon"] = 100, ["Modules.Name"] = 200,
        ["Permissions.Category"] = 100, ["Permissions.Code"] = 100, ["Permissions.Description"] = 500, ["Permissions.Name"] = 200,
        ["Roles.Description"] = 500, ["Roles.Name"] = 100, ["Roles.NormalizedName"] = 100,
        ["Tenants.Code"] = 50, ["Tenants.ConnectionString"] = 500, ["Tenants.DbProvider"] = 50, ["Tenants.Name"] = 200,
        ["UserPasswordHistory.PasswordHash"] = 500,
        ["Users.ConcurrencyStamp"] = 100, ["Users.DisplayName"] = 200, ["Users.Email"] = 200,
        ["Users.FirstName"] = 100, ["Users.LastName"] = 100, ["Users.NormalizedEmail"] = 200,
        ["Users.NormalizedUserName"] = 100, ["Users.PasswordHash"] = 500, ["Users.PhoneNumber"] = 20,
        ["Users.SecurityStamp"] = 100, ["Users.UserName"] = 100,
        ["UserSessions.AccessTokenJti"] = 100, ["UserSessions.DeviceInfo"] = 500, ["UserSessions.IpAddress"] = 100,
        ["UserSessions.Location"] = 200, ["UserSessions.RefreshToken"] = 500, ["UserSessions.RevokedReason"] = 200,
    };

    private static readonly Dictionary<string, int> AppDbSizedStringColumns = new()
    {
        ["AuditLogs.Action"] = 50, ["AuditLogs.EntityId"] = 100, ["AuditLogs.EntityType"] = 200,
        ["AuditLogs.IpAddress"] = 100, ["AuditLogs.UserAgent"] = 500,
        ["JobCards.DepartmentName"] = 200, ["JobCards.Instructions"] = 2000, ["JobCards.JobCardNumber"] = 17,
        ["JobCards.Notes"] = 2000, ["JobCards.Status"] = 20,
        ["Plants.Code"] = 50, ["Plants.Location"] = 200, ["Plants.Name"] = 200,
        ["ProcessSteps.DepartmentName"] = 200, ["ProcessSteps.Instructions"] = 2000, ["ProcessSteps.StepType"] = 20,
        ["ProcessTemplates.Description"] = 1000, ["ProcessTemplates.Name"] = 200,
        ["UserPlantAccess.AccessLevel"] = 50,
        ["UserPreferences.Key"] = 200,
        ["WorkOrders.Notes"] = 2000, ["WorkOrders.OrderNumber"] = 50, ["WorkOrders.ProductName"] = 200, ["WorkOrders.Status"] = 20,
    };

    private static readonly string[] MasterUnboundedStringColumns =
        ["IdentityAuditLogs.Details", "RolePermissions.ConditionJson", "Users.PasswordResetTokenHash"];

    private static readonly string[] AppDbUnboundedStringColumns =
        ["AuditLogs.NewValues", "AuditLogs.OldValues", "UserPreferences.Value"];

    private static readonly string[] MasterUniqueIndexes =
    [
        "IX_Companies_Code", "IX_FeatureFlags_Code", "IX_MenuPermissions_MenuId_PermissionId", "IX_Menus_Code", "IX_Modules_Code",
        "IX_Permissions_Code", "IX_RolePermissions_RoleId_PermissionId", "IX_Roles_NormalizedName_TenantId",
        "IX_TenantFeatureFlags_TenantId_FeatureFlagId", "IX_Tenants_Code", "IX_UserRoles_UserId_RoleId_TenantId",
        "IX_UserSessions_RefreshToken", "IX_UserTenants_UserId_TenantId", "IX_Users_NormalizedEmail",
        "IX_Users_NormalizedUserName",
    ];

    private static readonly string[] AppDbUniqueIndexes =
    [
        "IX_JobCards_JobCardNumber", "IX_Plants_Code", "IX_ProcessTemplates_Name_Version",
        "IX_UserPlantAccess_UserId_PlantId", "IX_UserPreferences_UserId_Key", "IX_WorkOrders_OrderNumber",
    ];

    private static readonly string[] MasterForeignKeys =
    [
        "FK_MenuPermissions_Menus_MenuId:Cascade", "FK_MenuPermissions_Permissions_PermissionId:Cascade",
        "FK_Menus_Menus_ParentMenuId:Restrict", "FK_Menus_Modules_ModuleId:Cascade",
        "FK_RolePermissions_Permissions_PermissionId:Cascade", "FK_RolePermissions_Roles_RoleId:Cascade",
        "FK_Roles_Tenants_TenantId:Restrict", "FK_TenantFeatureFlags_FeatureFlags_FeatureFlagId:Cascade",
        "FK_TenantFeatureFlags_Tenants_TenantId:Cascade", "FK_Tenants_Companies_CompanyId:Restrict",
        "FK_UserPasswordHistory_Users_UserId:Cascade",
        "FK_UserRoles_Roles_RoleId:Cascade", "FK_UserRoles_Tenants_TenantId:Restrict",
        "FK_UserRoles_Users_UserId:Cascade", "FK_UserSessions_Tenants_TenantId:Restrict",
        "FK_UserSessions_Users_UserId:Cascade", "FK_UserTenants_Tenants_TenantId:Cascade",
        "FK_UserTenants_Users_UserId:Cascade",
    ];

    private static readonly string[] AppDbForeignKeys =
    [
        "FK_JobCards_JobCards_ParentJobCardId:Restrict", "FK_JobCards_WorkOrders_WorkOrderId:Cascade",
        "FK_ProcessSteps_ProcessSteps_NextStepId:Restrict", "FK_ProcessSteps_ProcessTemplates_TemplateId:Cascade",
        "FK_UserPlantAccess_Plants_PlantId:Cascade",
    ];

    // ── Approved O-7 migration lineages (exact, ordered) ─────────────────────

    private static readonly string[] SqlServerMasterMigrations =
    [
        "20260407122318_InitialMaster",
        "20260509162834_AddPasswordResetToken",
        "20261012130942_AddCompanyFoundation",
    ];

    private static readonly string[] SqlServerAppDbMigrations =
    [
        "20260407122342_InitialTenant",
        "20260502180307_AddPlantIdToWorkOrders",
        "20261001112110_ChangeWorkOrderDueDateToDate",
    ];

    private static readonly string[] PostgreSqlMasterMigrations =
    [
        "20261007022731_InitialMasterBaseline",
        "20261012131005_AddCompanyFoundation",
    ];

    private static readonly string[] PostgreSqlAppDbMigrations =
    [
        "20261008162932_InitialTenantBaseline",
    ];

    // ── Harness ───────────────────────────────────────────────────────────────

    private static IConfiguration Configuration(Provider provider)
    {
        var connectionString = provider == Provider.SqlServer ? SqlServerConnectionString : PostgreSqlConnectionString;
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = provider.ToString(),
                ["ConnectionStrings:MasterDb"] = connectionString,
                ["Tenants:0:ConnectionString"] = connectionString,
            })
            .Build();
    }

    /// <summary>A context on its own UNCACHED service provider (see class remarks).</summary>
    private static DbContext CreateContext(Ctx ctx, Provider provider)
    {
        var configuration = Configuration(provider);
        return ctx == Ctx.Master
            ? new MasterDbContext(new DbContextOptionsBuilder<MasterDbContext>(
                    MasterDbContextFactory.BuildOptions(configuration)).EnableServiceProviderCaching(false).Options)
            : new AppDbContext(new DbContextOptionsBuilder<AppDbContext>(
                    AppDbContextFactory.BuildOptions(configuration)).EnableServiceProviderCaching(false).Options);
    }

    private static IModel BuildDesignTimeModel(Ctx ctx, Provider provider)
    {
        using var context = CreateContext(ctx, provider);
        return context.GetService<IDesignTimeModel>().Model;
    }

    // Each of the four models is built exactly once per run.
    private static readonly Dictionary<(Ctx, Provider), Lazy<IModel>> Models = new()
    {
        [(Ctx.Master, Provider.SqlServer)] = new(() => BuildDesignTimeModel(Ctx.Master, Provider.SqlServer)),
        [(Ctx.Master, Provider.PostgreSql)] = new(() => BuildDesignTimeModel(Ctx.Master, Provider.PostgreSql)),
        [(Ctx.AppDb, Provider.SqlServer)] = new(() => BuildDesignTimeModel(Ctx.AppDb, Provider.SqlServer)),
        [(Ctx.AppDb, Provider.PostgreSql)] = new(() => BuildDesignTimeModel(Ctx.AppDb, Provider.PostgreSql)),
    };

    private static IModel Model(Ctx ctx, Provider provider) => Models[(ctx, provider)].Value;

    private static IRelationalModel Relational(Ctx ctx, Provider provider) => Model(ctx, provider).GetRelationalModel();

    private static Dictionary<string, IColumn> Columns(Ctx ctx, Provider provider) =>
        Relational(ctx, provider).Tables
            .SelectMany(t => t.Columns.Select(c => (Key: $"{t.Name}.{c.Name}", Column: c)))
            .ToDictionary(x => x.Key, x => x.Column);

    private static Type ClrTypeOf(IColumn column)
    {
        var clr = column.PropertyMappings.First().Property.ClrType;
        return Nullable.GetUnderlyingType(clr) ?? clr;
    }

    private static List<IProperty> DateTimeProperties(Ctx ctx, Provider provider) =>
        Model(ctx, provider).GetEntityTypes()
            .SelectMany(e => e.GetProperties())
            .Where(p => (Nullable.GetUnderlyingType(p.ClrType) ?? p.ClrType) == typeof(DateTime))
            .ToList();

    private static string ColumnKey(IProperty property) =>
        $"{property.DeclaringType.GetTableName()}.{property.GetColumnName()}";

    private static string[] Sorted(IEnumerable<string> values) => values.Order(StringComparer.Ordinal).ToArray();

    // ── O-1: entities and properties, including CLR types ─────────────────────

    [Theory]
    [InlineData(Ctx.Master)]
    [InlineData(Ctx.AppDb)]
    public void O1_EntityAndPropertySets_IncludingClrTypes_MatchAcrossProviders(Ctx ctx)
    {
        static string[] Inventory(IModel model) => Sorted(model.GetEntityTypes()
            .SelectMany(e => e.GetProperties().Select(p => $"{e.ClrType.FullName}.{p.Name}:{p.ClrType.FullName}")));

        var sqlServer = Inventory(Model(ctx, Provider.SqlServer));
        var postgreSql = Inventory(Model(ctx, Provider.PostgreSql));

        Assert.NotEmpty(sqlServer);
        Assert.Equal(sqlServer, postgreSql);
    }

    // Pinned per-CLR-type column inventory, verified against the baseline — so two providers
    // that drifted together cannot still pass O-1.
    [Theory]
    [InlineData(Ctx.Master, Provider.SqlServer)]
    [InlineData(Ctx.Master, Provider.PostgreSql)]
    [InlineData(Ctx.AppDb, Provider.SqlServer)]
    [InlineData(Ctx.AppDb, Provider.PostgreSql)]
    public void O1_ClrTypeInventory_IsPinned(Ctx ctx, Provider provider)
    {
        var expected = ctx == Ctx.Master
            ? new Dictionary<string, int> { ["Boolean"] = 21, ["DateTime"] = 27, ["Guid"] = 44, ["Int32"] = 3, ["String"] = 51 }
            : new Dictionary<string, int>
            {
                ["Boolean"] = 12, ["DateOnly"] = 1, ["DateTime"] = 19, ["Guid"] = 32, ["Int32"] = 5, ["String"] = 23,
                // Value object and enums persisted as strings.
                ["JobCardNumber"] = 1, ["JobCardStatus"] = 1, ["StepType"] = 1, ["WorkOrderStatus"] = 1,
            };

        var actual = Columns(ctx, provider).Values
            .GroupBy(c => ClrTypeOf(c).Name)
            .ToDictionary(g => g.Key, g => g.Count());

        Assert.Equal(expected.OrderBy(kv => kv.Key), actual.OrderBy(kv => kv.Key));
    }

    // ── O-2: approved store-type mapping ─────────────────────────────────────

    // Every SQL Server column type is one the approved mapping covers, and its PostgreSQL
    // counterpart is exactly what the mapping prescribes. An unmapped type fails.
    [Theory]
    [InlineData(Ctx.Master)]
    [InlineData(Ctx.AppDb)]
    public void O2_EveryColumn_FollowsTheApprovedStoreTypeMapping(Ctx ctx)
    {
        var sqlServer = Columns(ctx, Provider.SqlServer);
        var postgreSql = Columns(ctx, Provider.PostgreSql);

        Assert.Equal(Sorted(sqlServer.Keys), Sorted(postgreSql.Keys));
        Assert.All(sqlServer, kv =>
        {
            var sqlType = kv.Value.StoreType;
            var sized = SizedNVarChar.Match(sqlType);
            var expectedPostgreSql = sized.Success
                ? $"character varying({sized.Groups[1].Value})"
                : ApprovedStoreTypeMap.TryGetValue(sqlType, out var mapped)
                    ? mapped
                    : throw new Xunit.Sdk.XunitException($"{kv.Key}: SQL Server type '{sqlType}' is not in the approved mapping.");

            Assert.True(expectedPostgreSql == postgreSql[kv.Key].StoreType,
                $"{kv.Key}: SQL Server '{sqlType}' must map to '{expectedPostgreSql}', PostgreSQL has '{postgreSql[kv.Key].StoreType}'.");
        });
    }

    // Approved per-pair counts, asserted independently on BOTH providers.
    [Theory]
    [InlineData(Ctx.Master, "uniqueidentifier", "uuid", 44)]
    [InlineData(Ctx.Master, "datetime2", "timestamp with time zone", 27)]
    [InlineData(Ctx.Master, "bit", "boolean", 21)]
    [InlineData(Ctx.Master, "int", "integer", 3)]
    [InlineData(Ctx.Master, "nvarchar(max)", "text", 3)]
    [InlineData(Ctx.Master, "date", "date", 0)]
    [InlineData(Ctx.AppDb, "uniqueidentifier", "uuid", 32)]
    [InlineData(Ctx.AppDb, "datetime2", "timestamp with time zone", 19)]
    [InlineData(Ctx.AppDb, "bit", "boolean", 12)]
    [InlineData(Ctx.AppDb, "int", "integer", 5)]
    [InlineData(Ctx.AppDb, "nvarchar(max)", "text", 3)]
    [InlineData(Ctx.AppDb, "date", "date", 1)]
    public void O2_ApprovedMappingCounts(Ctx ctx, string sqlServerType, string postgreSqlType, int expected)
    {
        Assert.Equal(expected, Columns(ctx, Provider.SqlServer).Values.Count(c => c.StoreType == sqlServerType));
        Assert.Equal(expected, Columns(ctx, Provider.PostgreSql).Values.Count(c => c.StoreType == postgreSqlType));
    }

    // nvarchar(n) ↔ character varying(n): the complete sized-string inventory, column by column.
    [Theory]
    [InlineData(Ctx.Master)]
    [InlineData(Ctx.AppDb)]
    public void O2_SizedStringInventory_AndLengths_ArePinned(Ctx ctx)
    {
        var expected = ctx == Ctx.Master ? MasterSizedStringColumns : AppDbSizedStringColumns;
        var sqlServer = Columns(ctx, Provider.SqlServer);
        var postgreSql = Columns(ctx, Provider.PostgreSql);

        Assert.Equal(Sorted(expected.Keys),
            Sorted(sqlServer.Where(kv => SizedNVarChar.IsMatch(kv.Value.StoreType)).Select(kv => kv.Key)));
        Assert.Equal(Sorted(expected.Keys),
            Sorted(postgreSql.Where(kv => kv.Value.StoreType.StartsWith("character varying(", StringComparison.Ordinal)).Select(kv => kv.Key)));

        Assert.All(expected, kv =>
        {
            Assert.Equal($"nvarchar({kv.Value})", sqlServer[kv.Key].StoreType);
            Assert.Equal($"character varying({kv.Value})", postgreSql[kv.Key].StoreType);
        });
    }

    [Theory]
    [InlineData(Ctx.Master)]
    [InlineData(Ctx.AppDb)]
    public void O2_UnboundedStringInventory_IsPinned(Ctx ctx)
    {
        var expected = Sorted(ctx == Ctx.Master ? MasterUnboundedStringColumns : AppDbUnboundedStringColumns);

        Assert.Equal(expected, Sorted(Columns(ctx, Provider.SqlServer).Where(kv => kv.Value.StoreType == "nvarchar(max)").Select(kv => kv.Key)));
        Assert.Equal(expected, Sorted(Columns(ctx, Provider.PostgreSql).Where(kv => kv.Value.StoreType == "text").Select(kv => kv.Key)));
    }

    // ── O-3: names and shapes — tables, columns, keys, indexes, foreign keys ─

    [Theory]
    [InlineData(Ctx.Master, 16, 35, 18)]
    [InlineData(Ctx.AppDb, 8, 23, 5)]
    public void O3_ObjectCounts_ArePinnedOnBothProviders(Ctx ctx, int tables, int indexes, int foreignKeys)
    {
        foreach (var provider in new[] { Provider.SqlServer, Provider.PostgreSql })
        {
            var model = Relational(ctx, provider);
            Assert.Equal(tables, model.Tables.Count());
            Assert.Equal(indexes, model.Tables.Sum(t => t.Indexes.Count()));
            Assert.Equal(foreignKeys, model.Tables.Sum(t => t.ForeignKeyConstraints.Count()));
        }
    }

    [Theory]
    [InlineData(Ctx.Master)]
    [InlineData(Ctx.AppDb)]
    public void O3_TableAndColumnNames_ArePinnedAndIdentical(Ctx ctx)
    {
        var expectedTables = Sorted(ctx == Ctx.Master ? MasterTables : AppDbTables);

        Assert.Equal(expectedTables, Sorted(Relational(ctx, Provider.SqlServer).Tables.Select(t => t.Name)));
        Assert.Equal(expectedTables, Sorted(Relational(ctx, Provider.PostgreSql).Tables.Select(t => t.Name)));

        foreach (var table in expectedTables)
        {
            var sqlServer = Relational(ctx, Provider.SqlServer).FindTable(table, schema: null)!;
            var postgreSql = Relational(ctx, Provider.PostgreSql).FindTable(table, schema: null)!;

            Assert.Null(sqlServer.Schema);
            Assert.Null(postgreSql.Schema);
            Assert.Equal(sqlServer.Columns.Select(c => c.Name), postgreSql.Columns.Select(c => c.Name));
            Assert.Equal(sqlServer.Columns.Select(c => c.IsNullable), postgreSql.Columns.Select(c => c.IsNullable));
        }
    }

    [Theory]
    [InlineData(Ctx.Master)]
    [InlineData(Ctx.AppDb)]
    public void O3_PrimaryKeys_HaveIdenticalNamesAndColumns(Ctx ctx)
    {
        static string[] Keys(IRelationalModel model) => Sorted(model.Tables.Select(t =>
            $"{t.Name}:{t.PrimaryKey!.Name}({string.Join(",", t.PrimaryKey.Columns.Select(c => c.Name))})"));

        var sqlServer = Keys(Relational(ctx, Provider.SqlServer));

        Assert.Equal(sqlServer, Keys(Relational(ctx, Provider.PostgreSql)));
        Assert.All(sqlServer, k => Assert.Matches(@"^(\w+):PK_\1\(", k));
    }

    [Theory]
    [InlineData(Ctx.Master)]
    [InlineData(Ctx.AppDb)]
    public void O3_Indexes_HaveIdenticalNamesColumnsAndUniqueness(Ctx ctx)
    {
        // The filter is compared separately (O-9) — it is the one approved difference.
        static string[] Indexes(IRelationalModel model) => Sorted(model.Tables.SelectMany(t => t.Indexes.Select(i =>
            $"{t.Name}:{i.Name}({string.Join(",", i.Columns.Select(c => c.Name))}) unique={i.IsUnique}")));

        Assert.Equal(Indexes(Relational(ctx, Provider.SqlServer)), Indexes(Relational(ctx, Provider.PostgreSql)));
    }

    [Theory]
    [InlineData(Ctx.Master)]
    [InlineData(Ctx.AppDb)]
    public void O3_ForeignKeys_HaveIdenticalShapeAndPinnedDeleteBehaviour(Ctx ctx)
    {
        static string[] Shapes(IRelationalModel model) => Sorted(model.Tables.SelectMany(t => t.ForeignKeyConstraints.Select(f =>
            $"{f.Name}: {t.Name}({string.Join(",", f.Columns.Select(c => c.Name))}) -> " +
            $"{f.PrincipalTable.Name}({string.Join(",", f.PrincipalColumns.Select(c => c.Name))}) {f.OnDeleteAction}")));
        static string[] NamesAndDelete(IRelationalModel model) => Sorted(model.Tables.SelectMany(t =>
            t.ForeignKeyConstraints.Select(f => $"{f.Name}:{f.OnDeleteAction}")));

        var expected = Sorted(ctx == Ctx.Master ? MasterForeignKeys : AppDbForeignKeys);

        Assert.Equal(Shapes(Relational(ctx, Provider.SqlServer)), Shapes(Relational(ctx, Provider.PostgreSql)));
        Assert.Equal(expected, NamesAndDelete(Relational(ctx, Provider.SqlServer)));
        Assert.Equal(expected, NamesAndDelete(Relational(ctx, Provider.PostgreSql)));
    }

    [Theory]
    [InlineData(Ctx.Master, 13, 5)]
    [InlineData(Ctx.AppDb, 3, 2)]
    public void O3_DeleteBehaviourCounts_ArePinnedOnBothProviders(Ctx ctx, int cascade, int restrict)
    {
        foreach (var provider in new[] { Provider.SqlServer, Provider.PostgreSql })
        {
            var actions = Relational(ctx, provider).Tables.SelectMany(t => t.ForeignKeyConstraints).Select(f => f.OnDeleteAction).ToList();
            Assert.Equal(cascade, actions.Count(a => a == ReferentialAction.Cascade));
            Assert.Equal(restrict, actions.Count(a => a == ReferentialAction.Restrict));
            Assert.Equal(cascade + restrict, actions.Count);
        }
    }

    // ── O-4: unique-index parity ─────────────────────────────────────────────

    [Theory]
    [InlineData(Ctx.Master, 15)]
    [InlineData(Ctx.AppDb, 6)]
    public void O4_UniqueIndexes_ArePinnedAndIdentical(Ctx ctx, int expectedCount)
    {
        var expected = Sorted(ctx == Ctx.Master ? MasterUniqueIndexes : AppDbUniqueIndexes);

        foreach (var provider in new[] { Provider.SqlServer, Provider.PostgreSql })
        {
            var unique = Sorted(Relational(ctx, provider).Tables.SelectMany(t => t.Indexes).Where(i => i.IsUnique).Select(i => i.Name));
            Assert.Equal(expectedCount, unique.Length);
            Assert.Equal(expected, unique);
        }
    }

    // ── O-5: UTC DateTime converters ─────────────────────────────────────────

    [Theory]
    [InlineData(Ctx.Master, Provider.SqlServer, 27, "datetime2")]
    [InlineData(Ctx.Master, Provider.PostgreSql, 27, "timestamp with time zone")]
    [InlineData(Ctx.AppDb, Provider.SqlServer, 19, "datetime2")]
    [InlineData(Ctx.AppDb, Provider.PostgreSql, 19, "timestamp with time zone")]
    public void O5_EveryDateTime_HasTheUtcConverter_AndTheProviderStoreType(
        Ctx ctx, Provider provider, int expectedCount, string expectedStoreType)
    {
        var expected = Sorted(ctx == Ctx.Master ? MasterDateTimeColumns : AppDbDateTimeColumns);
        var properties = DateTimeProperties(ctx, provider);

        Assert.Equal(expectedCount, properties.Count);
        Assert.Equal(expected, Sorted(properties.Select(ColumnKey)));

        Assert.All(properties, p =>
        {
            var expectedConverter = p.ClrType == typeof(DateTime?) ? typeof(UtcNullableDateTimeConverter) : typeof(UtcDateTimeConverter);
            Assert.True(p.GetValueConverter()?.GetType() == expectedConverter,
                $"{ColumnKey(p)}: expected {expectedConverter.Name}, found {p.GetValueConverter()?.GetType().Name ?? "no converter"}.");
            Assert.Equal(expectedStoreType, p.GetColumnType());
        });
    }

    // No property OTHER than a DateTime carries a UTC converter, on either provider.
    [Theory]
    [InlineData(Ctx.Master)]
    [InlineData(Ctx.AppDb)]
    public void O5_UtcConverters_AppearOnlyOnDateTimeProperties(Ctx ctx)
    {
        foreach (var provider in new[] { Provider.SqlServer, Provider.PostgreSql })
        {
            var converted = Model(ctx, provider).GetEntityTypes()
                .SelectMany(e => e.GetProperties())
                .Where(p => p.GetValueConverter() is UtcDateTimeConverter or UtcNullableDateTimeConverter)
                .Select(ColumnKey);

            Assert.Equal(Sorted(ctx == Ctx.Master ? MasterDateTimeColumns : AppDbDateTimeColumns), Sorted(converted));
        }
    }

    // ── O-6: WorkOrder.DueDate ───────────────────────────────────────────────

    [Theory]
    [InlineData(Provider.SqlServer)]
    [InlineData(Provider.PostgreSql)]
    public void O6_WorkOrderDueDate_IsNullableDateOnly_MappedToDate_WithoutConverter(Provider provider)
    {
        var dueDate = Model(Ctx.AppDb, provider)
            .FindEntityType(typeof(ERP.Production.Domain.Entities.WorkOrder))!
            .FindProperty("DueDate")!;

        Assert.Equal(typeof(DateOnly?), dueDate.ClrType);
        Assert.True(dueDate.IsNullable);
        Assert.Equal("date", dueDate.GetColumnType());
        Assert.Null(dueDate.GetValueConverter());
    }

    // ── O-7: migration lineage identities ────────────────────────────────────

    [Theory]
    [InlineData(Ctx.Master, Provider.SqlServer)]
    [InlineData(Ctx.Master, Provider.PostgreSql)]
    [InlineData(Ctx.AppDb, Provider.SqlServer)]
    [InlineData(Ctx.AppDb, Provider.PostgreSql)]
    public void O7_EachLineage_IsExactlyTheApprovedOrderedIds_InItsProviderAssembly(Ctx ctx, Provider provider)
    {
        var expected = (ctx, provider) switch
        {
            (Ctx.Master, Provider.SqlServer) => SqlServerMasterMigrations,
            (Ctx.Master, Provider.PostgreSql) => PostgreSqlMasterMigrations,
            (Ctx.AppDb, Provider.SqlServer) => SqlServerAppDbMigrations,
            _ => PostgreSqlAppDbMigrations,
        };

        using var context = CreateContext(ctx, provider);
        var migrations = context.GetService<IMigrationsAssembly>();

        Assert.Equal(expected, migrations.Migrations.Keys.ToArray());
        Assert.Equal(provider == Provider.SqlServer ? "ERP.Persistence.SqlServer" : "ERP.Persistence.PostgreSql",
            migrations.Assembly.GetName().Name);
        Assert.All(migrations.Migrations.Values, t => Assert.Equal(migrations.Assembly, t.Assembly));
    }

    [Fact]
    public void O7_ProviderLineages_AreDisjoint()
    {
        var sqlServer = SqlServerMasterMigrations.Concat(SqlServerAppDbMigrations).ToHashSet();
        var postgreSql = PostgreSqlMasterMigrations.Concat(PostgreSqlAppDbMigrations).ToHashSet();

        Assert.Empty(sqlServer.Intersect(postgreSql));

        foreach (var ctx in new[] { Ctx.Master, Ctx.AppDb })
        {
            using var sqlServerContext = CreateContext(ctx, Provider.SqlServer);
            using var postgreSqlContext = CreateContext(ctx, Provider.PostgreSql);

            Assert.Empty(sqlServerContext.GetService<IMigrationsAssembly>().Migrations.Keys.Intersect(postgreSql));
            Assert.Empty(postgreSqlContext.GetService<IMigrationsAssembly>().Migrations.Keys.Intersect(sqlServer));
        }
    }

    // ── O-8: identifier naming ───────────────────────────────────────────────

    // Names are preserved verbatim across providers (O-3 compares them); here: no naming
    // convention rewrote them, and each provider delimits them its own native way.
    [Theory]
    [InlineData(Ctx.Master)]
    [InlineData(Ctx.AppDb)]
    public void O8_Identifiers_StayPascalCase_WithNoSnakeCaseConversion(Ctx ctx)
    {
        foreach (var provider in new[] { Provider.SqlServer, Provider.PostgreSql })
        {
            var model = Relational(ctx, provider);
            var identifiers = model.Tables.Select(t => t.Name)
                .Concat(model.Tables.SelectMany(t => t.Columns).Select(c => c.Name))
                .ToList();

            Assert.All(identifiers, name => Assert.Matches("^[A-Z][A-Za-z0-9]*$", name));
        }
    }

    [Fact]
    public void O8_EachProvider_QuotesIdentifiersNatively()
    {
        using var sqlServer = CreateContext(Ctx.AppDb, Provider.SqlServer);
        using var postgreSql = CreateContext(Ctx.AppDb, Provider.PostgreSql);

        Assert.Equal("[WorkOrders]", sqlServer.GetService<ISqlGenerationHelper>().DelimitIdentifier("WorkOrders"));
        Assert.Equal("\"WorkOrders\"", postgreSql.GetService<ISqlGenerationHelper>().DelimitIdentifier("WorkOrders"));
    }

    // ── O-9: the approved nullable-unique-index filter difference ────────────

    [Fact]
    public void O9_RolesNormalizedNameTenantIdIndex_IsFilteredOnSqlServerOnly()
    {
        ITableIndex RolesIndex(Provider provider) => Assert.Single(
            Relational(Ctx.Master, provider).FindTable("Roles", schema: null)!.Indexes,
            i => i.Name == "IX_Roles_NormalizedName_TenantId");

        var sqlServer = RolesIndex(Provider.SqlServer);
        var postgreSql = RolesIndex(Provider.PostgreSql);

        Assert.True(sqlServer.IsUnique);
        Assert.True(postgreSql.IsUnique);
        Assert.Equal("[TenantId] IS NOT NULL", sqlServer.Filter);
        Assert.Null(postgreSql.Filter);
    }

    [Theory]
    [InlineData(Ctx.Master, Provider.SqlServer, 1)]
    [InlineData(Ctx.Master, Provider.PostgreSql, 0)]
    [InlineData(Ctx.AppDb, Provider.SqlServer, 0)]
    [InlineData(Ctx.AppDb, Provider.PostgreSql, 0)]
    public void O9_FilteredIndexCounts_ArePinned(Ctx ctx, Provider provider, int expected)
    {
        Assert.Equal(expected, Relational(ctx, provider).Tables.SelectMany(t => t.Indexes).Count(i => i.Filter is not null));
    }
}
