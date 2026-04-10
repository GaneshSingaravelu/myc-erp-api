namespace ERP.Identity.DTOs;

// ── RESPONSE DTOs ────────────────────────────────────────────

public record TenantResponse(
    Guid Id,
    string Code,
    string Name,
    string DbProvider,
    bool IsActive,
    DateTime CreatedAt,
    int UserCount);

public record TenantListResponse(
    Guid Id,
    string Code,
    string Name,
    string DbProvider,
    bool IsActive,
    DateTime CreatedAt);

// ── REQUEST DTOs ─────────────────────────────────────────────

/// <summary>
/// Request to provision a new tenant.
/// The API will: create Tenant record → create database → run migrations → seed admin access.
/// </summary>
public record CreateTenantRequest(
    string Code,
    string Name,
    string ConnectionString,
    string DbProvider = "SqlServer");

public record UpdateTenantRequest(
    string Name,
    bool IsActive);
