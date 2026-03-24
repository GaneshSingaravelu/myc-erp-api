namespace ERP.Infrastructure.Tenant;

/// <summary>
/// TENANT INFO — Holds all information about a tenant (company/client).
///
/// WHAT IS A TENANT? (Multi-Tenancy Concept)
/// ===========================================
/// In our ERP, each client company is a "tenant". Each tenant:
/// - Has their own DATABASE (DB-per-tenant isolation)
/// - Has their own users, roles, permissions
/// - May use different database providers (SQL Server vs PostgreSQL)
/// - Can have multiple plants (factories) within the same database
///
/// WHERE IS THIS DATA STORED?
/// ===========================
/// In the MASTER database — a central database that stores tenant metadata.
/// The master DB is separate from tenant databases:
///
///   Master DB:
///   ┌─────────────────────────────────────────────┐
///   │ Tenants table:                              │
///   │  Id | Identifier    | ConnectionString | DB │
///   │  1  | "abc-mfg"     | "Server=...ABC"  | SS │
///   │  2  | "xyz-corp"    | "Server=...XYZ"  | PG │
///   └─────────────────────────────────────────────┘
///         │                         │
///         ▼                         ▼
///   ┌──────────┐            ┌──────────────┐
///   │ ABC_DB   │            │ XYZ_DB       │
///   │(SqlSvr)  │            │(PostgreSQL)  │
///   └──────────┘            └──────────────┘
///
/// DESIGN PATTERN: This is a simple Data Transfer Object (DTO)
/// </summary>
public class TenantInfo
{
    /// <summary>Unique internal ID</summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Human-readable identifier sent in the X-Tenant-Id header.
    /// Example: "abc-manufacturing", "xyz-corp"
    /// </summary>
    public string Identifier { get; set; } = string.Empty;

    /// <summary>Display name of the company. Example: "ABC Manufacturing Pvt Ltd"</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The connection string to this tenant's database.
    /// Example: "Server=localhost;Database=ERP_ABC;User Id=sa;Password=***;"
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Which database provider this tenant uses.
    /// Values: "SqlServer", "PostgreSql", "Oracle"
    /// </summary>
    public string DbProvider { get; set; } = "SqlServer";

    /// <summary>Is this tenant active? Inactive tenants cannot log in.</summary>
    public bool IsActive { get; set; } = true;
}
