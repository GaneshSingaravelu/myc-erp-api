using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using ERP.Application.Abstractions.Identity;
using ERP.Identity.Abstractions;
using ERP.Identity.Abstractions.Persistence;
using ERP.Identity.Configuration;
using ERP.Identity.Entities;
using ERP.Identity.Persistence;
using ERP.Identity.Services;
using ERP.Persistence.Context;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace ERP.Identity.Extensions;

/// <summary>
/// IDENTITY DI REGISTRATION — Sets up JWT authentication for the entire API.
///
/// WHAT HAPPENS WHEN YOU CALL AddIdentityServices():
/// ===================================================
/// 1. Reads JwtSettings from appsettings.json
/// 2. Registers TokenService in DI
/// 3. Configures ASP.NET Core's Authentication middleware to use JWT Bearer
/// 4. Sets up token validation parameters (how to verify incoming tokens)
///
/// AFTER THIS SETUP, EVERY REQUEST:
/// =================================
/// 1. ASP.NET Core checks for "Authorization: Bearer xxx" header
/// 2. If present, validates the JWT token (signature, expiration, issuer, audience)
/// 3. If valid, sets HttpContext.User with claims from the token
/// 4. If [Authorize] is on the controller/action, denies access if token is invalid/missing
/// 5. If no [Authorize], the request proceeds (HttpContext.User may be anonymous)
/// </summary>
public static class IdentityServiceExtensions
{
    public static IServiceCollection AddIdentityServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Read JWT settings from configuration
        var jwtSettings = configuration.GetSection(JwtSettings.SectionName).Get<JwtSettings>()
            ?? throw new InvalidOperationException("JwtSettings section is missing from configuration.");

        // ── Startup guard: fail fast if the JWT secret key is weak ──
        // HMAC-SHA256 requires a key of at least 256 bits (32 bytes).
        // A placeholder or missing key should crash at startup, not silently produce weak tokens.
        const int minimumKeyLength = 32;
        if (string.IsNullOrWhiteSpace(jwtSettings.SecretKey)
            || jwtSettings.SecretKey.Length < minimumKeyLength
            || jwtSettings.SecretKey.Contains("REPLACE_ME", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"JwtSettings:SecretKey is missing, a placeholder, or too short. " +
                $"It must be at least {minimumKeyLength} characters. " +
                $"Set it via environment variable, user-secrets, or appsettings.Development.json.");
        }

        services.Configure<JwtSettings>(configuration.GetSection(JwtSettings.SectionName));

        // ── Startup guard (R2-DEV-03A): refresh-token digest key ──
        // Refresh tokens are stored only as HMAC-SHA-256 digests under this key. It must be
        // provisioned (never generated at runtime) and identical on every instance; a missing,
        // placeholder or short key stops startup rather than producing unverifiable digests.
        var refreshTokenSettings = configuration.GetSection(RefreshTokenSettings.SectionName).Get<RefreshTokenSettings>();
        RefreshTokenSettings.Validate(refreshTokenSettings);
        services.Configure<RefreshTokenSettings>(configuration.GetSection(RefreshTokenSettings.SectionName));
        services.AddSingleton<IRefreshTokenHasher, RefreshTokenHasher>();

        // Register Identity entity configurations with MasterDbContext
        // Without this, EF Core won't discover table mappings for Users, Roles, Permissions, etc.
        MasterDbContext.ConfigurationAssemblies.Add(typeof(IdentityServiceExtensions).Assembly);

        // Register token service
        services.AddSingleton<ITokenService, TokenService>();

        // Register Master-DB repositories + UoW.
        // Scoped lifetime so all repositories share the same MasterDbContext within
        // a single HTTP request — that's what makes IMasterUnitOfWork.SaveChangesAsync()
        // commit changes staged by every repository in one transaction.
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IRoleRepository, RoleRepository>();
        services.AddScoped<IPermissionRepository, PermissionRepository>();
        services.AddScoped<IMenuRepository, MenuRepository>();
        services.AddScoped<IFeatureFlagRepository, FeatureFlagRepository>();
        services.AddScoped<IUserSessionRepository, UserSessionRepository>();
        services.AddScoped<IUserPasswordHistoryRepository, UserPasswordHistoryRepository>();
        services.AddScoped<ITenantRepository, TenantRepository>();
        services.AddScoped<IMasterUnitOfWork, MasterUnitOfWork>();

        // Register identity services
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<IRoleService, RoleService>();
        services.AddScoped<ITenantManagementService, TenantManagementService>();
        services.AddScoped<IMenuService, MenuService>();

        // Configure JWT Bearer Authentication
        services.AddAuthentication(options =>
        {
            // Set JWT Bearer as the default authentication scheme
            options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
        })
        .AddJwtBearer(options =>
        {
            // Configure how incoming JWT tokens are validated
            options.TokenValidationParameters = new TokenValidationParameters
            {
                // Validate the token's signature (was it signed with our secret key?)
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(
                    Encoding.UTF8.GetBytes(jwtSettings.SecretKey)),

                // Validate who issued the token (must be our API)
                ValidateIssuer = true,
                ValidIssuer = jwtSettings.Issuer,

                // Validate who the token is for (must be our Angular app)
                ValidateAudience = true,
                ValidAudience = jwtSettings.Audience,

                // Validate the token hasn't expired
                ValidateLifetime = true,

                // Allow 0 seconds of clock skew (strict expiration)
                ClockSkew = TimeSpan.Zero
            };

            // Configure JWT events for SignalR support and session-based revocation
            options.Events = new JwtBearerEvents
            {
                // SignalR can't send headers, so it sends the token as a query parameter
                OnMessageReceived = context =>
                {
                    var accessToken = context.Request.Query["access_token"];
                    var path = context.HttpContext.Request.Path;

                    if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs"))
                    {
                        context.Token = accessToken;
                    }

                    return Task.CompletedTask;
                },

                // REQUEST-TIME ACCESS TOKEN REVOCATION
                // Validates the token's JTI against active sessions in the database.
                // Without this, logout/revoke only invalidates refresh tokens —
                // already-issued access tokens remain valid until natural expiry.
                //
                // R2-DEV-03A: read FRESH on every request (no per-instance cache). The former
                // 2-minute positive cache could only be invalidated on the instance that revoked
                // the session, so other instances kept accepting a revoked token for up to two
                // minutes. Cost: one indexed lookup (UserSessions.AccessTokenJti) per
                // authenticated request.
                OnTokenValidated = async context =>
                {
                    var jti = context.Principal?.FindFirst(JwtRegisteredClaimNames.Jti)?.Value;
                    var userIdStr = context.Principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;

                    if (jti is null || userIdStr is null || !Guid.TryParse(userIdStr, out var userId))
                    {
                        context.Fail("Token missing required claims.");
                        return;
                    }

                    var sessions = context.HttpContext.RequestServices.GetRequiredService<IUserSessionRepository>();
                    var sessionExists = await sessions.IsAccessTokenSessionActiveAsync(
                        userId, jti, DateTime.UtcNow, context.HttpContext.RequestAborted);

                    if (!sessionExists)
                    {
                        var logger = context.HttpContext.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("ERP.Identity.JtiValidation");
                        logger?.LogWarning("Access denied: JTI {Jti} for user {UserId} has no active session", jti, userId);
                        context.Fail("Token has been revoked.");
                    }
                }
            };
        });

        // Add authorization (to use [Authorize] attribute)
        services.AddAuthorization();

        return services;
    }
}
