using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using ERP.Identity.Abstractions;
using ERP.Identity.Configuration;
using ERP.Identity.Entities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace ERP.Identity.Services;

/// <summary>
/// TOKEN SERVICE IMPLEMENTATION — Creates and validates JWT tokens.
///
/// WHAT HAPPENS INSIDE GenerateAccessToken:
/// ==========================================
/// 1. Create CLAIMS (key-value pairs stored in the token):
///    - "sub" (subject) → user ID
///    - "email" → user's email
///    - "role" → user's roles (can have multiple)
///    - "permission" → user's permissions
///    - "tenant_id" → which tenant this user belongs to
///
/// 2. Create SIGNING CREDENTIALS:
///    - Use the secret key from JwtSettings
///    - Use HMAC-SHA256 algorithm (symmetric — same key signs and verifies)
///
/// 3. Create the TOKEN:
///    - Combine header + payload (claims) + signature
///    - Set expiration time
///    - Serialize to the compact string format: xxxxx.yyyyy.zzzzz
///
/// REGISTERED AS: Singleton (stateless, thread-safe, expensive to create)
/// </summary>
public class TokenService : ITokenService
{
    private readonly JwtSettings _jwtSettings;

    public TokenService(IOptions<JwtSettings> jwtSettings)
    {
        _jwtSettings = jwtSettings.Value;
    }

    public string GenerateAccessToken(
        AppUser user,
        IList<string> roles,
        IList<string> permissions,
        string tenantId,
        string jti)
    {
        // Step 1: Create claims (data stored in the token)
        // JTI is provided by the caller so it matches the UserSession.AccessTokenJti
        // This enables request-time revocation by checking jti against active sessions
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.UserName),
            new(ClaimTypes.Email, user.Email),
            new("tenant_id", tenantId),
            new("security_stamp", user.SecurityStamp),
            new(JwtRegisteredClaimNames.Jti, jti)
        };

        // Add role claims (a user can have multiple roles)
        foreach (var role in roles)
            claims.Add(new Claim(ClaimTypes.Role, role));

        // Add permission claims
        foreach (var permission in permissions)
            claims.Add(new Claim("permission", permission));

        // Step 2: Create signing credentials
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtSettings.SecretKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        // Step 3: Create the token
        var token = new JwtSecurityToken(
            issuer: _jwtSettings.Issuer,
            audience: _jwtSettings.Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(_jwtSettings.AccessTokenExpirationMinutes),
            signingCredentials: credentials);

        // Step 4: Serialize to string
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public string GenerateRefreshToken()
    {
        // Generate a cryptographically secure random string
        var randomBytes = new byte[64];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(randomBytes);
        return Convert.ToBase64String(randomBytes);
    }

    public ClaimsPrincipal? GetPrincipalFromExpiredToken(string token)
    {
        var validationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = false, // ← KEY: Don't validate expiration (token IS expired)
            ValidateIssuerSigningKey = true,
            ValidIssuer = _jwtSettings.Issuer,
            ValidAudience = _jwtSettings.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(_jwtSettings.SecretKey))
        };

        var tokenHandler = new JwtSecurityTokenHandler();
        var principal = tokenHandler.ValidateToken(token, validationParameters, out var securityToken);

        // Verify the token was signed with our algorithm
        if (securityToken is not JwtSecurityToken jwtToken
            || !jwtToken.Header.Alg.Equals(SecurityAlgorithms.HmacSha256,
                StringComparison.InvariantCultureIgnoreCase))
        {
            return null;
        }

        return principal;
    }
}
