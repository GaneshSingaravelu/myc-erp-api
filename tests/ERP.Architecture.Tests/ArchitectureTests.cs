using System.Reflection;
using NetArchTest.Rules;

namespace ERP.Architecture.Tests;

/// <summary>
/// Architecture regression tests — enforces dependency direction, bounded-context boundaries,
/// and security invariants. Prevents architectural drift as the codebase grows.
///
/// These tests use NetArchTest to analyze assembly references at the type level.
/// They run in CI and fail the build if violations are introduced.
/// </summary>
public class ArchitectureTests
{
    // Assembly references for each architectural layer
    private static readonly Assembly SharedKernelAssembly = typeof(SharedKernel.Entities.BaseEntity).Assembly;
    private static readonly Assembly ApplicationAbstractionsAssembly = typeof(Application.Abstractions.Persistence.IRepository<>).Assembly;
    private static readonly Assembly PersistenceAssembly = typeof(Persistence.Context.AppDbContext).Assembly;
    private static readonly Assembly IdentityAssembly = typeof(Identity.Services.IAuthService).Assembly;
    private static readonly Assembly InfrastructureAssembly = typeof(Infrastructure.Tenant.TenantMiddleware).Assembly;

    // Module assemblies
    private static readonly Assembly WorkflowDomainAssembly = typeof(Workflow.Domain.Entities.ProcessTemplate).Assembly;
    private static readonly Assembly WorkflowApplicationAssembly = typeof(Workflow.Application.DTOs.ProcessTemplateResponse).Assembly;
    private static readonly Assembly ProductionDomainAssembly = typeof(Production.Domain.Entities.WorkOrder).Assembly;
    private static readonly Assembly ProductionApplicationAssembly = typeof(Production.Application.DTOs.WorkOrderResponse).Assembly;

    // ─── DEPENDENCY DIRECTION: Domain/Application must NOT depend on Infrastructure ───

    [Fact]
    public void SharedKernel_Should_Not_Depend_On_EfCore()
    {
        var result = Types.InAssembly(SharedKernelAssembly)
            .ShouldNot()
            .HaveDependencyOn("Microsoft.EntityFrameworkCore")
            .GetResult();

        Assert.True(result.IsSuccessful,
            "SharedKernel must not reference EF Core. Domain entities must be persistence-ignorant.");
    }

    [Fact]
    public void SharedKernel_Should_Not_Depend_On_AspNetCore()
    {
        var result = Types.InAssembly(SharedKernelAssembly)
            .ShouldNot()
            .HaveDependencyOn("Microsoft.AspNetCore")
            .GetResult();

        Assert.True(result.IsSuccessful,
            "SharedKernel must not reference ASP.NET Core. Domain must be framework-agnostic.");
    }

    [Fact]
    public void ApplicationAbstractions_Should_Not_Depend_On_EfCore()
    {
        var result = Types.InAssembly(ApplicationAbstractionsAssembly)
            .ShouldNot()
            .HaveDependencyOn("Microsoft.EntityFrameworkCore")
            .GetResult();

        Assert.True(result.IsSuccessful,
            "Application.Abstractions must not reference EF Core. Use IRepository/IUnitOfWork interfaces.");
    }

    [Fact]
    public void ApplicationAbstractions_Should_Not_Depend_On_AspNetCore()
    {
        var result = Types.InAssembly(ApplicationAbstractionsAssembly)
            .ShouldNot()
            .HaveDependencyOn("Microsoft.AspNetCore")
            .GetResult();

        Assert.True(result.IsSuccessful,
            "Application.Abstractions must not reference ASP.NET Core.");
    }

    // ─── MODULE DOMAIN ISOLATION ───

    [Fact]
    public void WorkflowDomain_Should_Not_Depend_On_Infrastructure()
    {
        var result = Types.InAssembly(WorkflowDomainAssembly)
            .ShouldNot()
            .HaveDependencyOnAny(
                "Microsoft.EntityFrameworkCore",
                "ERP.Persistence",
                "ERP.Infrastructure",
                "ERP.Identity")
            .GetResult();

        Assert.True(result.IsSuccessful,
            "Workflow.Domain must not depend on Infrastructure/Persistence/Identity.");
    }

    [Fact]
    public void ProductionDomain_Should_Not_Depend_On_Infrastructure()
    {
        var result = Types.InAssembly(ProductionDomainAssembly)
            .ShouldNot()
            .HaveDependencyOnAny(
                "Microsoft.EntityFrameworkCore",
                "ERP.Persistence",
                "ERP.Infrastructure",
                "ERP.Identity")
            .GetResult();

        Assert.True(result.IsSuccessful,
            "Production.Domain must not depend on Infrastructure/Persistence/Identity.");
    }

    [Fact]
    public void WorkflowApplication_Should_Not_Depend_On_EfCore()
    {
        var result = Types.InAssembly(WorkflowApplicationAssembly)
            .ShouldNot()
            .HaveDependencyOn("Microsoft.EntityFrameworkCore")
            .GetResult();

        Assert.True(result.IsSuccessful,
            "Workflow.Application must not reference EF Core directly.");
    }

    [Fact]
    public void ProductionApplication_Should_Not_Depend_On_EfCore()
    {
        var result = Types.InAssembly(ProductionApplicationAssembly)
            .ShouldNot()
            .HaveDependencyOn("Microsoft.EntityFrameworkCore")
            .GetResult();

        Assert.True(result.IsSuccessful,
            "Production.Application must not reference EF Core directly.");
    }

    // ─── CROSS-MODULE BOUNDARY: Modules must not depend on each other ───

    [Fact]
    public void WorkflowDomain_Should_Not_Depend_On_ProductionModule()
    {
        var result = Types.InAssembly(WorkflowDomainAssembly)
            .ShouldNot()
            .HaveDependencyOnAny(
                "ERP.Production.Domain",
                "ERP.Production.Application",
                "ERP.Production.Infrastructure")
            .GetResult();

        Assert.True(result.IsSuccessful,
            "Workflow module must not depend on Production module (bounded context isolation).");
    }

    [Fact]
    public void ProductionDomain_Should_Not_Depend_On_WorkflowModule()
    {
        var result = Types.InAssembly(ProductionDomainAssembly)
            .ShouldNot()
            .HaveDependencyOnAny(
                "ERP.Workflow.Domain",
                "ERP.Workflow.Application",
                "ERP.Workflow.Infrastructure")
            .GetResult();

        Assert.True(result.IsSuccessful,
            "Production module must not depend on Workflow module (bounded context isolation).");
    }

    // ─── SECURITY INVARIANTS ───

    [Fact]
    public void Identity_Entities_Should_Not_Be_Referenced_By_Module_Domains()
    {
        var workflowResult = Types.InAssembly(WorkflowDomainAssembly)
            .ShouldNot()
            .HaveDependencyOn("ERP.Identity")
            .GetResult();

        var productionResult = Types.InAssembly(ProductionDomainAssembly)
            .ShouldNot()
            .HaveDependencyOn("ERP.Identity")
            .GetResult();

        Assert.True(workflowResult.IsSuccessful,
            "Workflow.Domain must not reference Identity entities directly.");
        Assert.True(productionResult.IsSuccessful,
            "Production.Domain must not reference Identity entities directly.");
    }

    [Fact]
    public void Persistence_Should_Not_Depend_On_AspNetCore()
    {
        var result = Types.InAssembly(PersistenceAssembly)
            .ShouldNot()
            .HaveDependencyOn("Microsoft.AspNetCore")
            .GetResult();

        Assert.True(result.IsSuccessful,
            "ERP.Persistence must not reference ASP.NET Core. Persistence is framework-agnostic.");
    }
}
