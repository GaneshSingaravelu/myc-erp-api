using ERP.Application.Abstractions.Identity.Contracts;
using ERP.SharedKernel.Results;

namespace ERP.Application.Abstractions.Identity;

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

    /// <summary>
    /// Issues a reset token bound to <paramref name="tenantId"/> (Code or Id, from X-Tenant-Id).
    /// Only issued when the tenant is active and the user is an active member; otherwise the
    /// same generic success is returned and nothing is issued.
    /// </summary>
    Task<Result<ForgotPasswordResponse>> ForgotPasswordAsync(ForgotPasswordRequest request, string tenantId, bool exposeToken = false);

    /// <summary>
    /// Redeems a reset token. It must have been issued through the same <paramref name="tenantId"/>,
    /// which must still be active, with the user still an active member.
    /// </summary>
    Task<Result> ResetPasswordAsync(ResetPasswordRequest request, string tenantId);
}
