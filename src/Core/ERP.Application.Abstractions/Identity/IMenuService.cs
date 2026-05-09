using ERP.Application.Abstractions.Identity.Contracts;
using ERP.SharedKernel.Results;

namespace ERP.Application.Abstractions.Identity;

/// <summary>
/// MENU SERVICE — Returns permission-filtered, feature-flag-aware menu trees.
/// </summary>
public interface IMenuService
{
    /// <summary>
    /// Get the menu tree for the current user in the given tenant.
    /// Filters by: user permissions + tenant feature flags.
    /// </summary>
    Task<Result<IReadOnlyList<ModuleMenuResponse>>> GetMyMenusAsync(Guid userId, Guid? tenantId);

    /// <summary>
    /// Get all permissions structured as a tree matching the sidebar menu hierarchy.
    /// Module → Menu → Permissions. Permissions not linked to any menu are grouped
    /// under an "Other" root node. Used by the admin permission-assignment UI.
    /// </summary>
    Task<Result<IReadOnlyList<PermissionTreeNode>>> GetPermissionTreeAsync();
}
