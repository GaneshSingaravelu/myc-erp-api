namespace ERP.Identity.Configuration;

/// <summary>
/// JWT SETTINGS — Configuration for JWT token generation.
///
/// WHAT IS JWT? (Very Common Interview Question!)
/// ================================================
/// JWT = JSON Web Token. It's a way to securely transmit user identity between
/// the Angular frontend and our API backend.
///
/// HOW JWT AUTHENTICATION WORKS:
/// ==============================
///
/// 1. LOGIN:
///    Angular sends: POST /api/v1/auth/login { "username": "john", "password": "***" }
///    API validates password, creates JWT token, returns it:
///    { "accessToken": "eyJhbG...", "refreshToken": "abc123...", "expiresIn": 3600 }
///
/// 2. SUBSEQUENT REQUESTS:
///    Angular stores the token and sends it with every request:
///    GET /api/v1/jobcards
///    Headers: Authorization: Bearer eyJhbG...
///             X-Tenant-Id: abc-manufacturing
///
/// 3. API VALIDATES:
///    ASP.NET Core JWT middleware:
///    - Checks the token signature (not tampered?)
///    - Checks expiration (not expired?)
///    - Extracts claims (who is this user? what roles?)
///    - Sets HttpContext.User with the claims
///
/// JWT TOKEN STRUCTURE (three parts separated by dots):
/// =====================================================
///   eyJhbGciOiJIUzI1NiIs.eyJzdWIiOiJ1c2VyLTEyMyIs.SflKxwRJSMeKKF2QT4fwpM
///   │                     │                           │
///   HEADER               PAYLOAD                     SIGNATURE
///   (algorithm)          (claims: userId,             (ensures token
///                         email, roles, etc.)          wasn't modified)
///
/// STORED IN: appsettings.json under "JwtSettings" section
/// </summary>
public class JwtSettings
{
    public const string SectionName = "JwtSettings";

    /// <summary>
    /// Secret key used to SIGN the token. Must be at least 32 characters.
    /// NEVER commit the real key to source control! Use environment variables in production.
    /// </summary>
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>Who issued this token (our API). Example: "ERP.Api"</summary>
    public string Issuer { get; set; } = string.Empty;

    /// <summary>Who this token is for (our Angular app). Example: "ERP.Angular"</summary>
    public string Audience { get; set; } = string.Empty;

    /// <summary>How long the access token is valid (in minutes). Default: 60 minutes.</summary>
    public int AccessTokenExpirationMinutes { get; set; } = 60;

    /// <summary>How long the refresh token is valid (in days). Default: 7 days.</summary>
    public int RefreshTokenExpirationDays { get; set; } = 7;
}
