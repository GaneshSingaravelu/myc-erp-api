namespace ERP.Application.Abstractions.Identity.Contracts;

/// <summary>
/// DATA TRANSFER OBJECTS (DTOs) for Authentication.
///
/// WHAT IS A DTO? (Interview Question!)
/// ======================================
/// A DTO is a simple object that carries data between layers.
/// It contains NO business logic — just properties.
///
/// WHY NOT USE ENTITIES DIRECTLY?
/// ==============================
/// 1. SECURITY: Entity has PasswordHash — you don't want to expose that in API responses
/// 2. SHAPE: API response may need different shape than the database entity
/// 3. VALIDATION: Request DTO has validation rules; Entity has business rules
/// 4. DECOUPLING: Changing the entity doesn't break the API contract
///
/// NAMING CONVENTION:
///   {Action}Request  → what the client sends (input)
///   {Action}Response → what the API returns (output)
/// </summary>

// ──────────────────────────────────────────────────────
// LOGIN
// ──────────────────────────────────────────────────────
public record LoginRequest(string UserName, string Password);

public record LoginResponse(
    string AccessToken,
    string RefreshToken,
    DateTime ExpiresAt,
    UserDto User);

// ──────────────────────────────────────────────────────
// REGISTER
// ──────────────────────────────────────────────────────
public record RegisterRequest(
    string UserName,
    string Email,
    string Password,
    string FirstName,
    string LastName);

// ──────────────────────────────────────────────────────
// REFRESH TOKEN
// ──────────────────────────────────────────────────────
public record RefreshTokenRequest(string AccessToken, string RefreshToken);

// ──────────────────────────────────────────────────────
// LOGOUT
// ──────────────────────────────────────────────────────
public record LogoutRequest(string RefreshToken);

// ──────────────────────────────────────────────────────
// CHANGE PASSWORD
// ──────────────────────────────────────────────────────
public record ChangePasswordRequest(string CurrentPassword, string NewPassword);

// ──────────────────────────────────────────────────────
// USER DTOs
// ──────────────────────────────────────────────────────
public record UserDto(
    Guid Id,
    string UserName,
    string Email,
    string FirstName,
    string LastName,
    string FullName,
    bool IsActive,
    DateTime? LastLoginAt,
    IList<string> Roles);

public record CreateUserRequest(
    string UserName,
    string Email,
    string Password,
    string FirstName,
    string LastName);

public record UpdateUserRequest(
    string Email,
    string FirstName,
    string LastName,
    bool IsActive);

/// <summary>
/// Role assignment is TENANT-SCOPED: the UserRoles row is keyed on
/// (UserId, RoleId, TenantId), so the tenant has to be stated, not guessed.
///
/// <para><b>TenantId is required.</b> It is deliberately not inferred from the
/// target user's default tenant, nor from the caller's <c>X-Tenant-Id</c>: the
/// first cannot express "Admin in tenant A, Viewer in tenant B", and the second
/// is the administrator's tenant, which has nothing to do with the target
/// user.</para>
///
/// <para><see cref="RoleIds"/> replaces the user's roles <b>within
/// <see cref="TenantId"/> only</b>. An empty list clears them for that tenant and
/// leaves every other tenant untouched.</para>
/// </summary>
public record AssignRolesRequest(Guid TenantId, IList<Guid> RoleIds);

public record AssignTenantsRequest(IList<Guid> TenantIds);

public record UserTenantDto(Guid TenantId, string TenantCode, string TenantName, bool IsDefault);

// ──────────────────────────────────────────────────────
// ROLE DTOs
// ──────────────────────────────────────────────────────
public record RoleDto(
    Guid Id,
    string Name,
    string? Description,
    bool IsSystemRole,
    IList<PermissionDto> Permissions);

public record CreateRoleRequest(string Name, string? Description);
public record UpdateRoleRequest(string Name, string? Description);
public record AssignPermissionsRequest(IList<Guid> PermissionIds);

// ──────────────────────────────────────────────────────
// FORGOT / RESET PASSWORD
// ──────────────────────────────────────────────────────
public record ForgotPasswordRequest(string Email);

public record ResetPasswordRequest(string Email, string Token, string NewPassword);

/// <summary>
/// Response for forgot-password. ResetToken is only populated in non-Production
/// environments (for smoke testing without an email server). In Production the
/// token is sent via email and this field is null.
/// </summary>
public record ForgotPasswordResponse(string Message, string? ResetToken = null);

// ──────────────────────────────────────────────────────
// PERMISSION DTOs
// ──────────────────────────────────────────────────────
public record PermissionDto(Guid Id, string Code, string Name, string Category);
