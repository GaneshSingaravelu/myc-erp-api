using ERP.Identity.Entities;

namespace ERP.Identity.Abstractions;

/// <summary>
/// TOKEN SERVICE — Generates and validates JWT tokens.
///
/// NOTE: This interface lives inside ERP.Identity (NOT in ERP.Application.Abstractions)
/// because its signature depends on the AppUser entity, which is an internal Identity concept.
/// It's only consumed by AuthService inside the same module — no need to expose it upward.
///
/// WHY ACCESS TOKEN + REFRESH TOKEN?
/// ===================================
/// Access Token: Short-lived (60 min). Used for API calls.
///   - If stolen, attacker has limited time to use it.
///   - Cannot be revoked (stateless — API doesn't track them).
///
/// Refresh Token: Long-lived (7 days). Used ONLY to get new access tokens.
///   - Stored securely (httpOnly cookie or secure storage).
///   - CAN be revoked (stored in database, we can delete it).
///   - When access token expires, Angular sends refresh token to get a new one.
///
/// FLOW:
///   1. Login → Returns access token (60 min) + refresh token (7 days)
///   2. Angular uses access token for API calls
///   3. After 60 min, access token expires
///   4. Angular sends refresh token to /auth/refresh
///   5. API validates refresh token, issues new access token
///   6. Repeat from step 2
///   7. After 7 days, refresh token expires → user must log in again
/// </summary>
public interface ITokenService
{
    /// <summary>Generate a JWT access token with user claims (id, email, roles, permissions)</summary>
    /// <param name="jti">Unique token ID — must match the UserSession.AccessTokenJti for revocation checks</param>
    string GenerateAccessToken(AppUser user, IList<string> roles, IList<string> permissions, string tenantId, string jti);

    /// <summary>Generate a random refresh token</summary>
    string GenerateRefreshToken();

    /// <summary>
    /// Extract user claims from an EXPIRED access token.
    /// Used during the refresh flow — the old token is expired but we need the user ID
    /// to look up the refresh token in the database.
    /// </summary>
    System.Security.Claims.ClaimsPrincipal? GetPrincipalFromExpiredToken(string token);
}
