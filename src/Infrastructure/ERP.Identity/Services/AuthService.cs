using ERP.Identity.DTOs;
using ERP.Identity.Entities;
using ERP.Persistence.Context;
using ERP.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ERP.Identity.Services;

/// <summary>
/// AUTH SERVICE IMPLEMENTATION — Handles login, register, and token refresh.
///
/// LOGIN FLOW:
/// 1. Find user by username
/// 2. Verify password hash using BCrypt
/// 3. Load user's roles and permissions
/// 4. Generate JWT access token + refresh token
/// 5. Store refresh token in database
/// 6. Return tokens + user info
///
/// REGISTER FLOW:
/// 1. Check if username/email already exists
/// 2. Hash password with BCrypt
/// 3. Create user record
/// 4. Return user info (no auto-login — client must call login)
///
/// REFRESH FLOW:
/// 1. Extract user ID from expired access token
/// 2. Find user in database
/// 3. Verify stored refresh token matches
/// 4. Check refresh token hasn't expired
/// 5. Generate new access token + new refresh token
/// 6. Update stored refresh token
/// </summary>
public class AuthService : IAuthService
{
    private readonly AppDbContext _context;
    private readonly ITokenService _tokenService;
    private readonly ILogger<AuthService> _logger;

    public AuthService(AppDbContext context, ITokenService tokenService, ILogger<AuthService> logger)
    {
        _context = context;
        _tokenService = tokenService;
        _logger = logger;
    }

    public async Task<Result<LoginResponse>> LoginAsync(LoginRequest request, string tenantId)
    {
        // Step 1: Find user by username (include roles and permissions)
        var user = await _context.Set<AppUser>()
            .Include(u => u.UserRoles)
                .ThenInclude(ur => ur.Role)
                    .ThenInclude(r => r.RolePermissions)
                        .ThenInclude(rp => rp.Permission)
            .FirstOrDefaultAsync(u => u.UserName == request.UserName);

        if (user is null)
        {
            _logger.LogWarning("Login failed: user '{UserName}' not found", request.UserName);
            return Result.Failure<LoginResponse>(Error.Validation("Auth.InvalidCredentials", "Invalid username or password."));
        }

        if (!user.IsActive)
        {
            _logger.LogWarning("Login failed: user '{UserName}' is inactive", request.UserName);
            return Result.Failure<LoginResponse>(Error.Validation("Auth.AccountInactive", "Account is inactive. Contact administrator."));
        }

        // Step 2: Verify password
        if (!BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
        {
            _logger.LogWarning("Login failed: invalid password for '{UserName}'", request.UserName);
            return Result.Failure<LoginResponse>(Error.Validation("Auth.InvalidCredentials", "Invalid username or password."));
        }

        // Step 3: Get roles and permissions
        var roles = user.UserRoles.Select(ur => ur.Role.Name).ToList();
        var permissions = user.UserRoles
            .SelectMany(ur => ur.Role.RolePermissions)
            .Select(rp => rp.Permission.Code)
            .Distinct()
            .ToList();

        // Step 4: Generate tokens
        var accessToken = _tokenService.GenerateAccessToken(user, roles, permissions, tenantId);
        var refreshToken = _tokenService.GenerateRefreshToken();

        // Step 5: Store refresh token
        user.RefreshToken = refreshToken;
        user.RefreshTokenExpiresAt = DateTime.UtcNow.AddDays(7);
        user.LastLoginAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        _logger.LogInformation("User '{UserName}' logged in successfully", user.UserName);

        // Step 6: Return response
        return Result.Success(new LoginResponse(
            AccessToken: accessToken,
            RefreshToken: refreshToken,
            ExpiresAt: DateTime.UtcNow.AddMinutes(60),
            User: MapToDto(user, roles)));
    }

    public async Task<Result<UserDto>> RegisterAsync(RegisterRequest request)
    {
        // Check duplicates
        if (await _context.Set<AppUser>().AnyAsync(u => u.UserName == request.UserName))
            return Result.Failure<UserDto>(Error.Conflict("Auth.UserNameExists", $"Username '{request.UserName}' is already taken."));

        if (await _context.Set<AppUser>().AnyAsync(u => u.Email == request.Email))
            return Result.Failure<UserDto>(Error.Conflict("Auth.EmailExists", $"Email '{request.Email}' is already registered."));

        // Create user with hashed password
        var user = new AppUser
        {
            UserName = request.UserName,
            Email = request.Email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            FirstName = request.FirstName,
            LastName = request.LastName,
            IsActive = true
        };

        await _context.Set<AppUser>().AddAsync(user);
        await _context.SaveChangesAsync();

        _logger.LogInformation("User '{UserName}' registered successfully", user.UserName);

        return Result.Success(MapToDto(user, []));
    }

    public async Task<Result<LoginResponse>> RefreshTokenAsync(RefreshTokenRequest request, string tenantId)
    {
        // Extract user from expired token
        var principal = _tokenService.GetPrincipalFromExpiredToken(request.AccessToken);
        if (principal is null)
            return Result.Failure<LoginResponse>(Error.Validation("Auth.InvalidToken", "Invalid access token."));

        var userIdClaim = principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (userIdClaim is null || !Guid.TryParse(userIdClaim, out var userId))
            return Result.Failure<LoginResponse>(Error.Validation("Auth.InvalidToken", "Invalid token claims."));

        // Find user and verify refresh token
        var user = await _context.Set<AppUser>()
            .Include(u => u.UserRoles)
                .ThenInclude(ur => ur.Role)
                    .ThenInclude(r => r.RolePermissions)
                        .ThenInclude(rp => rp.Permission)
            .FirstOrDefaultAsync(u => u.Id == userId);

        if (user is null || user.RefreshToken != request.RefreshToken || user.RefreshTokenExpiresAt <= DateTime.UtcNow)
            return Result.Failure<LoginResponse>(Error.Validation("Auth.InvalidRefreshToken", "Invalid or expired refresh token."));

        // Generate new tokens
        var roles = user.UserRoles.Select(ur => ur.Role.Name).ToList();
        var permissions = user.UserRoles.SelectMany(ur => ur.Role.RolePermissions).Select(rp => rp.Permission.Code).Distinct().ToList();

        var newAccessToken = _tokenService.GenerateAccessToken(user, roles, permissions, tenantId);
        var newRefreshToken = _tokenService.GenerateRefreshToken();

        user.RefreshToken = newRefreshToken;
        user.RefreshTokenExpiresAt = DateTime.UtcNow.AddDays(7);
        await _context.SaveChangesAsync();

        return Result.Success(new LoginResponse(
            AccessToken: newAccessToken,
            RefreshToken: newRefreshToken,
            ExpiresAt: DateTime.UtcNow.AddMinutes(60),
            User: MapToDto(user, roles)));
    }

    public async Task<Result> ChangePasswordAsync(Guid userId, string currentPassword, string newPassword)
    {
        var user = await _context.Set<AppUser>().FindAsync(userId);
        if (user is null)
            return Result.Failure(Error.NotFound("User", userId));

        if (!BCrypt.Net.BCrypt.Verify(currentPassword, user.PasswordHash))
            return Result.Failure(Error.Validation("Auth.InvalidPassword", "Current password is incorrect."));

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);
        await _context.SaveChangesAsync();

        return Result.Success();
    }

    private static UserDto MapToDto(AppUser user, IList<string> roles) => new(
        Id: user.Id,
        UserName: user.UserName,
        Email: user.Email,
        FirstName: user.FirstName,
        LastName: user.LastName,
        FullName: user.FullName,
        IsActive: user.IsActive,
        LastLoginAt: user.LastLoginAt,
        Roles: roles);
}
