namespace ERP.Application.Abstractions.Identity.Contracts;

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

/// <summary>
/// Request to update a tenant's editable details.
///
/// <para><c>IsActive</c> is nullable on purpose. Activation state is not editable through
/// this endpoint, and a non-nullable <c>bool</c> could not tell an omitted field from an
/// explicit <c>false</c> — every client that left it out would have looked like it was
/// asking to deactivate the tenant. <c>null</c> means "no activation request"; a supplied
/// value is accepted only when it already matches the stored state.</para>
/// </summary>
public record UpdateTenantRequest(
    string Name,
    bool? IsActive);
