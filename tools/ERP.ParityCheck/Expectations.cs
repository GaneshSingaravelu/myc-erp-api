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
            Tables:
            [
                "FeatureFlags", "IdentityAuditLogs", "MenuPermissions", "Menus", "Modules",
                "Permissions", "RolePermissions", "Roles", "TenantFeatureFlags", "Tenants",
                "UserPasswordHistory", "UserRoles", "UserSessions", "UserTenants", "Users",
            ],
            Columns: 140,
            ColumnCategories: new Dictionary<string, int>
            {
                [Guid] = 42, [DateTime] = 26, [Bool] = 20, [Int] = 3, [UnboundedText] = 3, [SizedText] = 46, [Date] = 0,
            },
            Indexes: 33,
            UniqueIndexes: 14,
            FilteredIndexesSqlServer: 1,
            FilteredIndexesPostgreSql: 0,
            ForeignKeys: 17,
            SqlServerLineage: ["20260407122318_InitialMaster", "20260509162834_AddPasswordResetToken"],
            PostgreSqlLineage: ["20261007022731_InitialMasterBaseline"]),
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
