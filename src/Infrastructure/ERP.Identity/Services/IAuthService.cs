using ERP.Identity.DTOs;
using ERP.SharedKernel.Results;

namespace ERP.Identity.Services;

/// <summary>
/// AUTH SERVICE — Handles login, registration, token refresh, logout, and password management.
/// </summary>
public interface IAuthService
{
    Task<Result<LoginResponse>> LoginAsync(LoginRequest request, string tenantId);
    Task<Result<UserDto>> RegisterAsync(RegisterRequest request);
    Task<Result<LoginResponse>> RefreshTokenAsync(RefreshTokenRequest request, string tenantId);
    Task<Result> LogoutAsync(Guid userId, string refreshToken);
    Task<Result> ChangePasswordAsync(Guid userId, string currentPassword, string newPassword);
}
