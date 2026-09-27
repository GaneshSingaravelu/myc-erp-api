using ERP.Application.Abstractions.Caching;
using ERP.Application.Abstractions.Email;
using ERP.Application.Abstractions.Identity;
using ERP.Application.Abstractions.MultiTenancy;
using ERP.Infrastructure.Caching;
using ERP.Infrastructure.Email;
using ERP.Infrastructure.Tenant;
using ERP.Persistence.Context;
using ERP.Persistence.Interceptors;
using ERP.Persistence.PostgreSql.Extensions;
using ERP.Persistence.Providers;
using ERP.Persistence.SqlServer.Extensions;
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

        // The deployment's database engine. Resolved HERE, outside the AddDbContext
        // lambda, so a missing or unsupported Database:Provider fails at STARTUP rather
        // than lazily on the first request that resolves a MasterDbContext.
        // DatabaseProviderResolver fails closed — there is no fallback provider.
        var masterProvider = DatabaseProviderResolver.Parse(
            configuration[DatabaseProviderResolver.ConfigurationKey]);

        services.AddDbContext<MasterDbContext>((sp, options) =>
        {
            // Provider-specific EF configuration (migrations assembly, retry policy,
            // command timeout) is owned by the two provider seams and deliberately NOT
            // duplicated here — the same seams TenantDbContextFactory uses.
            switch (masterProvider)
            {
                case DatabaseProvider.SqlServer:
                    options.UseSqlServerProvider(masterConnectionString);
                    break;

                case DatabaseProvider.PostgreSql:
                    options.UsePostgreSqlProvider(masterConnectionString);
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Unsupported database provider for the Master database: '{masterProvider}'. " +
                        $"Supported providers: {DatabaseProviderResolver.SupportedProviders}");
            }

            // Both interceptors must survive provider switching. AuditableEntityInterceptor
            // populates CreatedAt/LastModifiedAt/DeletedAt; DomainEventDispatcherInterceptor
            // dispatches domain events on save. They are attached to the OUTER options
            // builder, independently of which provider was selected above.
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
