namespace ERP.ParityCheck;

/// <summary>
/// APPROVED EXPECTATIONS (P1-k handoff; inventories verified against baseline 08db90b and
/// pinned offline by ProviderParityTests). Live runs always use <see cref="Approved"/>.
/// Only the offline simulation may substitute a deliberately wrong copy (PC-L2).
/// </summary>
public sealed record ContextExpectations(
    string[] Tables,
    int Columns,
    IReadOnlyDictionary<string, int> ColumnCategories,
    int Indexes,
    int UniqueIndexes,
    int FilteredIndexesSqlServer,
    int FilteredIndexesPostgreSql,
    int ForeignKeys,
    string[] SqlServerLineage,
    string[] PostgreSqlLineage)
{
    public string[] Lineage(Provider provider) => provider == Provider.SqlServer ? SqlServerLineage : PostgreSqlLineage;

    public int FilteredIndexes(Provider provider) =>
        provider == Provider.SqlServer ? FilteredIndexesSqlServer : FilteredIndexesPostgreSql;

    /// <summary>The pinned incremental upgrade this context is checked against (L-10); null if none exists.</summary>
    public UpgradeExpectation? Upgrade { get; init; }
}

/// <summary>
/// L-10 (Master) — the pinned incremental upgrade: the Phase 1 head (<c>From</c>) and the
/// migration that upgrades it (<c>To</c>), per provider, plus the Company objects it must create.
/// Only what L-1's inventory cannot express is pinned here; table/column/index/FK totals after
/// the upgrade are the context's existing expectations.
/// </summary>
public sealed record UpgradeExpectation(
    string SqlServerFrom, string SqlServerTo, string PostgreSqlFrom, string PostgreSqlTo)
{
    public string From(Provider p) => p == Provider.SqlServer ? SqlServerFrom : PostgreSqlFrom;

    public string To(Provider p) => p == Provider.SqlServer ? SqlServerTo : PostgreSqlTo;

    public string[] CompanyColumns { get; init; } = ["Id", "Code", "Name", "IsActive", "CreatedAt"];

    public string CodeIndex { get; init; } = "IX_Companies_Code";

    public string TenantCompanyIdIndex { get; init; } = "IX_Tenants_CompanyId";

    public string ForeignKey { get; init; } = "FK_Tenants_Companies_CompanyId";

    /// <summary>
    /// EF's <c>Restrict</c> as each catalog spells it: SQL Server has no RESTRICT and records the
    /// generated <c>ON DELETE NO ACTION</c>; PostgreSQL records RESTRICT. The live delete probe
    /// is what proves the behaviour; this pins the declaration.
    /// </summary>
    public string DeleteAction(Provider p) => p == Provider.SqlServer ? "NO_ACTION" : "RESTRICT";

    /// <summary>Synthetic pre-existing rows inserted at the Phase 1 head (see LiveRunner.MasterUpgrade).</summary>
    public string[] FixtureTables { get; init; } = ["Tenants", "Users", "UserTenants", "Roles", "UserRoles"];

    public int FixtureRows { get; init; } = 11;

    public string[] Probes { get; init; } = ["invalid-company-fk", "restrict-delete", "duplicate-code"];
}

public sealed record Expectations(ContextExpectations Master, ContextExpectations AppDb)
{
    public ContextExpectations For(DbContextKind kind) => kind == DbContextKind.Master ? Master : AppDb;

    // Column categories shared by both providers (see Schema.Categorize).
    public const string Guid = "guid";
    public const string DateTime = "datetime";
    public const string Bool = "bool";
    public const string Int = "int";
    public const string UnboundedText = "text-unbounded";
    public const string SizedText = "text-sized";
    public const string Date = "date";

    public static readonly Expectations Approved = new(
        Master: new ContextExpectations(
            // P2-a (ARCH-IMPL-01/02): + Companies table, + nullable Tenants.CompanyId, + their
            // indexes and FK, + one incremental Master migration per provider.
            Tables:
            [
                "Companies", "FeatureFlags", "IdentityAuditLogs", "MenuPermissions", "Menus", "Modules",
                "Permissions", "RolePermissions", "Roles", "TenantFeatureFlags", "Tenants",
                "UserPasswordHistory", "UserRoles", "UserSessions", "UserTenants", "Users",
            ],
            Columns: 146,
            ColumnCategories: new Dictionary<string, int>
            {
                [Guid] = 44, [DateTime] = 27, [Bool] = 21, [Int] = 3, [UnboundedText] = 3, [SizedText] = 48, [Date] = 0,
            },
            Indexes: 35,
            UniqueIndexes: 15,
            FilteredIndexesSqlServer: 1,
            FilteredIndexesPostgreSql: 0,
            ForeignKeys: 18,
            SqlServerLineage:
            [
                "20260407122318_InitialMaster", "20260509162834_AddPasswordResetToken",
                "20261012130942_AddCompanyFoundation",
            ],
            PostgreSqlLineage: ["20261007022731_InitialMasterBaseline", "20261012131005_AddCompanyFoundation"])
        {
            // L-10: the first incremental PostgreSQL Master migration, and its SQL Server twin.
            Upgrade = new UpgradeExpectation(
                SqlServerFrom: "20260509162834_AddPasswordResetToken", SqlServerTo: "20261012130942_AddCompanyFoundation",
                PostgreSqlFrom: "20261007022731_InitialMasterBaseline", PostgreSqlTo: "20261012131005_AddCompanyFoundation"),
        },
        AppDb: new ContextExpectations(
            Tables:
            [
                "AuditLogs", "JobCards", "Plants", "ProcessSteps",
                "ProcessTemplates", "UserPlantAccess", "UserPreferences", "WorkOrders",
            ],
            Columns: 96,
            ColumnCategories: new Dictionary<string, int>
            {
                [Guid] = 32, [DateTime] = 19, [Bool] = 12, [Int] = 5, [UnboundedText] = 3, [SizedText] = 24, [Date] = 1,
            },
            Indexes: 23,
            UniqueIndexes: 6,
            FilteredIndexesSqlServer: 0,
            FilteredIndexesPostgreSql: 0,
            ForeignKeys: 5,
            SqlServerLineage:
            [
                "20260407122342_InitialTenant", "20260502180307_AddPlantIdToWorkOrders",
                "20261001112110_ChangeWorkOrderDueDateToDate",
            ],
            PostgreSqlLineage: ["20261008162932_InitialTenantBaseline"]));
}
