using ERP.Application.Abstractions.Caching;
using ERP.Application.Abstractions.Email;
using ERP.Application.Abstractions.Identity;
using ERP.Application.Abstractions.MultiTenancy;
using ERP.Infrastructure.Caching;
using ERP.Infrastructure.Email;
using ERP.Infrastructure.Tenant;
using ERP.Persistence.Context;
using ERP.Persistence.Interceptors;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ERP.Infrastructure.Extensions;

/// <summary>
/// INFRASTRUCTURE DI REGISTRATION — Registers all infrastructure services.
///
/// WHAT IS DEPENDENCY INJECTION? (Very Common Interview Question!)
/// ================================================================
/// DI is a technique where an object receives its dependencies from OUTSIDE
/// rather than creating them itself.
///
/// WITHOUT DI (tight coupling — BAD):
///   public class JobCardHandler
///   {
///       public void Handle()
///       {
///           var repo = new SqlServerRepository();  // ← Hardcoded! Can't swap, can't test
///           var cache = new RedisCache();           // ← Hardcoded!
///       }
///   }
///
/// WITH DI (loose coupling — GOOD):
///   public class JobCardHandler
///   {
///       private readonly IRepository _repo;    // ← Interface, injected by DI container
///       private readonly ICacheService _cache;  // ← Interface, injected by DI container
///
///       public JobCardHandler(IRepository repo, ICacheService cache)
///       {
///           _repo = repo;   // DI container provides the right implementation
///           _cache = cache;
///       }
///   }
///
/// HOW DI WORKS IN ASP.NET CORE:
/// ==============================
/// 1. REGISTER: Tell the DI container which implementation to use for each interface
///    services.AddScoped&lt;IRepository, Repository&gt;();
///
/// 2. RESOLVE: When a controller/service needs IRepository, DI creates Repository
///    and passes it to the constructor automatically.
///
/// 3. DISPOSE: When the scope ends (request ends for Scoped), DI disposes the object.
///
/// WHY EXTENSION METHODS FOR REGISTRATION?
/// ========================================
/// Each project has its own AddXxx() extension method. This keeps registration
/// organized and allows each project to register its own services independently.
///
///   // In Program.cs (clean and readable):
///   builder.Services.AddInfrastructure(configuration);
///   builder.Services.AddIdentity(configuration);
///   builder.Services.AddPersistence(configuration);
///
/// DESIGN PATTERN: Extension Method Pattern + Service Locator (anti-pattern avoided
/// by using constructor injection everywhere)
/// </summary>
public static class InfrastructureServiceExtensions
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // ──────────────────────────────────────────────────────
        // MULTI-TENANCY SERVICES
        // ──────────────────────────────────────────────────────

        // Register HttpContextAccessor — needed to access HttpContext outside controllers
        services.AddHttpContextAccessor();

        // Tenant store: reads tenants from Master DB (replaced ConfigurationTenantStore)
        services.AddScoped<ITenantStore, MasterDbTenantStore>();

        // Tenant provisioning: creates databases + runs migrations for new tenants
        services.AddScoped<ITenantProvisioningService, TenantProvisioningService>();

        // Tenant provider: reads current tenant from HttpContext (set by TenantMiddleware)
        services.AddScoped<ITenantProvider, HttpContextTenantProvider>();

        // Current user service: reads user claims from JWT token
        services.AddScoped<ICurrentUserService, CurrentUserService>();

        // ──────────────────────────────────────────────────────
        // DB CONTEXT FACTORY (Multi-Tenant + Multi-Provider)
        // ──────────────────────────────────────────────────────

        // --- MASTER DB CONTEXT (Identity, Access Control, Menus) ---
        // MasterDbContext uses a fixed connection string (not per-tenant)
        var masterConnectionString = configuration.GetConnectionString("MasterDb")
            ?? throw new InvalidOperationException(
                "MasterDb connection string is missing. " +
                "Set ConnectionStrings:MasterDb in appsettings.Development.json or environment variables.");

        services.AddDbContext<MasterDbContext>((sp, options) =>
        {
            options.UseSqlServer(masterConnectionString, sqlOptions =>
            {
                sqlOptions.MigrationsAssembly("ERP.Persistence.SqlServer");
                sqlOptions.EnableRetryOnFailure(
                    maxRetryCount: 3,
                    maxRetryDelay: TimeSpan.FromSeconds(5),
                    errorNumbersToAdd: null);
                sqlOptions.CommandTimeout(30);
            });
            options.AddInterceptors(
                sp.GetRequiredService<AuditableEntityInterceptor>(),
                sp.GetRequiredService<DomainEventDispatcherInterceptor>());
        });

        // --- TENANT DB CONTEXT FACTORY (Multi-Tenant + Multi-Provider) ---
        // Register the factory that creates the right DbContext per tenant
        services.AddScoped<IDbContextFactory<AppDbContext>, TenantDbContextFactory>();

        // Register AppDbContext as Scoped, resolved through the factory
        // This means: whenever something needs AppDbContext, the factory creates it
        // with the correct connection string for the current tenant
        services.AddScoped(sp =>
            sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext());

        // ──────────────────────────────────────────────────────
        // CACHING
        // ──────────────────────────────────────────────────────

        // In-memory cache (L1 — fast, local to this app instance)
        services.AddMemoryCache();
        services.AddSingleton<ICacheService, MemoryCacheService>();

        // TODO: Add Redis (L2 — distributed cache) when needed:
        // services.AddStackExchangeRedisCache(options =>
        // {
        //     options.Configuration = configuration.GetConnectionString("Redis");
        // });

        // ──────────────────────────────────────────────────────
        // EMAIL (MailKit SMTP)
        // ──────────────────────────────────────────────────────

        services.Configure<EmailSettings>(configuration.GetSection(EmailSettings.SectionName));
        services.AddSingleton<IEmailService, SmtpEmailService>();

        return services;
    }
}
