using ERP.Persistence.Context;
using Microsoft.Extensions.DependencyInjection;

namespace ERP.Workflow.Infrastructure.Extensions;

/// <summary>
/// Registers Workflow module services with the DI container.
/// Called from Program.cs (composition root).
///
/// This method tells AppDbContext to also scan the Workflow.Infrastructure assembly
/// for entity configurations (ProcessTemplateConfiguration, ProcessStepConfiguration).
/// </summary>
public static class WorkflowServiceExtensions
{
    public static IServiceCollection AddWorkflowModule(this IServiceCollection services)
    {
        // Register Workflow entity configurations with AppDbContext
        // This allows AppDbContext to discover ProcessTemplate/ProcessStep table mappings
        // without a direct project reference from ERP.Persistence to ERP.Workflow
        AppDbContext.ConfigurationAssemblies.Add(typeof(WorkflowServiceExtensions).Assembly);

        return services;
    }
}
