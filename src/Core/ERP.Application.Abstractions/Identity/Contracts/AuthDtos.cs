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

public record AssignRolesRequest(IList<Guid> RoleIds);

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
// PERMISSION DTOs
// ──────────────────────────────────────────────────────
public record PermissionDto(Guid Id, string Code, string Name, string Category);
