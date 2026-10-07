using System.Runtime.CompilerServices;
using ERP.Identity.Entities;
using ERP.Persistence.Context;

namespace ERP.UnitTests;

/// <summary>
/// Registers every entity-configuration assembly into the DbContexts' shared static lists ONCE,
/// before any test runs.
///
/// WHY THIS EXISTS
/// ===============
/// <c>MasterDbContext.ConfigurationAssemblies</c> and <c>AppDbContext.ConfigurationAssemblies</c>
/// are shared static <c>List&lt;Assembly&gt;</c> fields that <c>OnModelCreating</c> enumerates.
/// Test classes have historically registered what they need from their own static constructors,
/// each guarded by a <c>Contains</c> check.
///
/// That is safe only while model building is rare. <c>List&lt;T&gt;</c> is not thread-safe, and
/// once several classes build models across two contexts and two providers in parallel, one
/// thread can be enumerating a list while another is adding to it. The symptom is not a clean
/// error either: a model gets built from a partially populated list, EF caches it per context
/// type, and later assertions fail because an entity type or a converter is simply absent — a
/// failure with nothing to do with the code under test.
///
/// Measured while adding the P1-h model assertions: the full suite failed 5 runs out of 12, in
/// four different tests including one merged with P1-g, while <c>origin/main</c> failed 0 out of
/// 10. The new tests did not reveal a product defect; they widened an existing race until it
/// began firing.
///
/// A <c>[ModuleInitializer]</c> runs once, before any test, on a single thread. Registering
/// everything here means every later <c>Contains</c> check short-circuits, so the lists are
/// effectively immutable for the rest of the run and nothing can enumerate a list mid-mutation.
/// Individual test classes keep their own static-constructor registration: it is now redundant
/// but harmless, and leaving it means no existing fixture depends on this file having run.
/// </summary>
internal static class TestEntityConfigurationRegistration
{
    [ModuleInitializer]
    internal static void RegisterAll()
    {
        // Exactly the two assemblies the existing fixtures register — nothing more. Adding an
        // assembly no test registers today would change the model other tests observe, which is
        // not this file's job: it only moves WHEN registration happens, never WHAT is registered.
        var identity = typeof(AppUser).Assembly;
        var production = typeof(ERP.Production.Infrastructure.Configurations.WorkOrderConfiguration).Assembly;

        // Master holds identity/access-control entities.
        Register(MasterDbContext.ConfigurationAssemblies, identity);

        // Tenant databases hold the Production module's entities.
        Register(AppDbContext.ConfigurationAssemblies, production);
    }

    private static void Register(List<System.Reflection.Assembly> target, System.Reflection.Assembly assembly)
    {
        if (!target.Contains(assembly))
            target.Add(assembly);
    }
}
