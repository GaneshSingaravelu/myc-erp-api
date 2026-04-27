using ERP.Identity.Abstractions.Persistence;
using ERP.Identity.Entities;
using ERP.Persistence.Context;
using Microsoft.EntityFrameworkCore;

namespace ERP.Identity.Persistence;

/// <summary>
/// EF Core implementation of <see cref="IMenuRepository"/> over MasterDbContext.
/// Encapsulates the Module → Menus (IsVisible) → MenuPermissions → Permission
/// Include chain used by MenuService.
/// </summary>
public sealed class MenuRepository : IMenuRepository
{
    private readonly MasterDbContext _context;

    public MenuRepository(MasterDbContext context) => _context = context;

    public async Task<IReadOnlyList<Module>> GetActiveModulesWithVisibleMenusAsync(CancellationToken cancellationToken = default)
    {
        var modules = await _context.Set<Module>()
            .Where(m => m.IsActive)
            .Include(m => m.Menus.Where(menu => menu.IsVisible))
                .ThenInclude(menu => menu.MenuPermissions)
                    .ThenInclude(mp => mp.Permission)
            .OrderBy(m => m.DisplayOrder)
            .ToListAsync(cancellationToken);

        return modules;
    }
}
