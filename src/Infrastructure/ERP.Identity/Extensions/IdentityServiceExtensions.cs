using System.Text;
using ERP.Identity.Configuration;
using ERP.Identity.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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

        services.Configure<JwtSettings>(configuration.GetSection(JwtSettings.SectionName));

        // Register token service
        services.AddSingleton<ITokenService, TokenService>();

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

            // Configure SignalR JWT authentication
            // SignalR can't send headers, so it sends the token as a query parameter
            options.Events = new JwtBearerEvents
            {
                OnMessageReceived = context =>
                {
                    var accessToken = context.Request.Query["access_token"];
                    var path = context.HttpContext.Request.Path;

                    // If the request is for a SignalR hub, read token from query string
                    if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs"))
                    {
                        context.Token = accessToken;
                    }

                    return Task.CompletedTask;
                }
            };
        });

        // Add authorization (to use [Authorize] attribute)
        services.AddAuthorization();

        return services;
    }
}
