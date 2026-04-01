using ERP.Persistence.Context;
using Microsoft.Extensions.DependencyInjection;

namespace ERP.Production.Infrastructure.Extensions;

/// <summary>
/// Registers Production module services with the DI container.
/// Called from Program.cs (composition root).
/// </summary>
public static class ProductionServiceExtensions
{
    public static IServiceCollection AddProductionModule(this IServiceCollection services)
    {
        // Register Production entity configurations with AppDbContext
        // This allows AppDbContext to discover WorkOrder/JobCard table mappings
        // without a direct project reference from ERP.Persistence to ERP.Production
        AppDbContext.ConfigurationAssemblies.Add(typeof(ProductionServiceExtensions).Assembly);

        return services;
    }
}
