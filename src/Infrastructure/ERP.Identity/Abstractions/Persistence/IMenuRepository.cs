using ERP.Identity.Entities;

namespace ERP.Identity.Abstractions.Persistence;

/// <summary>
/// Read-only repository for the Module/Menu catalog (Master DB).
///
/// The menu catalog is admin-seeded and rarely changes at runtime. MenuService
/// queries this repository to build the permission-filtered sidebar tree after
/// login — so the single method here encapsulates the full eager-load chain
/// (Module → Menus (visible) → MenuPermissions → Permission) and keeps EF Core
/// Include() noise out of the service layer.
/// </summary>
public interface IMenuRepository
{
    /// <summary>
    /// List all active modules with their visible menus + menu permissions eager-loaded,
    /// ordered by <see cref="Module.DisplayOrder"/>. Used to build the sidebar tree.
    /// </summary>
    Task<IReadOnlyList<Module>> GetActiveModulesWithVisibleMenusAsync(CancellationToken cancellationToken = default);
}
