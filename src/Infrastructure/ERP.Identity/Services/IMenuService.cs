using ERP.Identity.DTOs;
using ERP.SharedKernel.Results;

namespace ERP.Identity.Services;

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
}
