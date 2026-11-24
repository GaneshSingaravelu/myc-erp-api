using System.Reflection;
using System.Text.RegularExpressions;
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
    private static readonly Assembly IdentityAssembly = typeof(Identity.Services.AuthService).Assembly;
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

    // ─── IDENTITY SERVICES: repository pattern enforced ───

    [Fact]
    public void IdentityServices_Should_Not_Depend_On_EfCore()
    {
        // Identity services (AuthService, UserService, RoleService, MenuService,
        // TenantManagementService, …) must talk to the Master DB only through
        // IUserRepository / IRoleRepository / … + IMasterUnitOfWork. Direct use of
        // MasterDbContext or EF Core APIs leaks persistence concerns into the service
        // layer and makes unit-testing require an InMemory DB instead of a mock.
        var result = Types.InAssembly(IdentityAssembly)
            .That()
            .ResideInNamespace("ERP.Identity.Services")
            .ShouldNot()
            .HaveDependencyOn("Microsoft.EntityFrameworkCore")
            .GetResult();

        var offenders = result.FailingTypes?.Select(t => t.Name) ?? Enumerable.Empty<string>();

        Assert.True(result.IsSuccessful,
            "ERP.Identity.Services types must not depend on Microsoft.EntityFrameworkCore. " +
            "Use IUserRepository / IRoleRepository / IPermissionRepository / IMenuRepository / " +
            "IFeatureFlagRepository / ITenantRepository + IMasterUnitOfWork instead. " +
            $"Offending types: {string.Join(", ", offenders)}");
    }

    // ─── SIGNALR: notification hub endpoint stays disabled (R2-DEV-02B-2A) ───

    /// <summary>
    /// ErpNotificationHub was unmapped because its connection and group authorization are
    /// not tenant-safe. Mapping it again — in Program.cs or in any extension method — must be
    /// a deliberate, security-approved change that also updates this test.
    ///
    /// This is a SOURCE scan, not a runtime check: this test project does not reference the
    /// ERP.Api host, so it cannot inspect the built endpoint table. Comments are ignored, so
    /// documentation that mentions the old mapping does not trip it.
    /// </summary>
    [Fact]
    public void NotificationHub_Should_Not_Be_Mapped()
    {
        var srcRoot = Path.Combine(FindRepositoryRoot(), "src");
        var mapHub = new Regex(@"\bMapHub\s*<", RegexOptions.Compiled);
        const string hubPath = "\"/hubs/notifications\"";

        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (IsBuildOutput(file)) continue;

            var code = StripComments(File.ReadAllText(file));
            if (mapHub.IsMatch(code) || code.Contains(hubPath, StringComparison.OrdinalIgnoreCase))
                offenders.Add(Path.GetRelativePath(srcRoot, file));
        }

        Assert.True(offenders.Count == 0,
            "SignalR hub mapping found. ErpNotificationHub (/hubs/notifications) is disabled until " +
            "its connection and group authorization are tenant-safe; re-enabling it needs explicit " +
            $"security approval. Offending files: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void NotificationHub_Guard_Scans_The_Real_Host_File()
    {
        // Guards the guard: if the host moved or the scan root were wrong, the test above would
        // pass vacuously. The composition root must exist where the scan looks for it.
        var program = Path.Combine(FindRepositoryRoot(), "src", "Host", "ERP.Api", "Program.cs");

        Assert.True(File.Exists(program), $"Expected host composition root at {program}");
        Assert.Contains("AddSignalR", StripComments(File.ReadAllText(program)));
    }

    private static string FindRepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ERP.slnx")))
                return dir.FullName;
        }

        throw new InvalidOperationException("ERP.slnx not found above " + AppContext.BaseDirectory);
    }

    private static bool IsBuildOutput(string path)
    {
        var sep = Path.DirectorySeparatorChar;
        return path.Contains($"{sep}bin{sep}") || path.Contains($"{sep}obj{sep}");
    }

    /// <summary>Removes /* block */ comments and whole-line // and /// comments.</summary>
    private static string StripComments(string source)
    {
        var withoutBlocks = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        var lines = withoutBlocks.Split('\n')
            .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal));
        return string.Join('\n', lines);
    }
}
