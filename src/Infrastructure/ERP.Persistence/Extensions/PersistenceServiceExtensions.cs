using ERP.Application.Abstractions.Persistence;
using ERP.Persistence.Interceptors;
using ERP.Persistence.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace ERP.Persistence.Extensions;

/// <summary>
/// EXTENSION METHOD for registering Persistence services in DI.
///
/// WHAT IS AN EXTENSION METHOD?
/// ==============================
/// A static method that "adds" a new method to an existing type.
/// builder.Services.AddPersistence() reads like a built-in method,
/// but it's defined here in our code.
///
/// WHY A SEPARATE EXTENSION PER PROJECT?
/// =======================================
/// Each project (Persistence, Infrastructure, Identity) registers its own services.
/// The API host (Program.cs) calls them all:
///   builder.Services.AddPersistence();        // registers repos, UoW, interceptors
///   builder.Services.AddInfrastructure();      // registers caching, tenant middleware
///   builder.Services.AddIdentity();            // registers JWT, auth services
///
/// This keeps Program.cs clean and each project responsible for its own DI setup.
/// </summary>
public static class PersistenceServiceExtensions
{
    public static IServiceCollection AddPersistence(this IServiceCollection services)
    {
        // Register interceptors
        services.AddScoped<AuditableEntityInterceptor>();
        services.AddScoped<AuditLogInterceptor>();
        services.AddScoped<DomainEventDispatcherInterceptor>();

        // Register generic repository — any IRepository<T> resolves to Repository<T>
        services.AddScoped(typeof(IRepository<>), typeof(Repository<>));

        // Register Unit of Work
        services.AddScoped<IUnitOfWork, UnitOfWork>();

        return services;
    }
}
