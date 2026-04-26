using ERP.Identity.Abstractions.Persistence;
using ERP.Identity.Entities;
using ERP.Persistence.Context;
using Microsoft.EntityFrameworkCore;

namespace ERP.Identity.Persistence;

/// <summary>
/// EF Core implementation of <see cref="IUserRepository"/> over MasterDbContext.
/// Centralizes the UserRoles → Role → RolePermissions → Permission Include chain
/// that auth flows depend on.
/// </summary>
public sealed class UserRepository : IUserRepository
{
    private readonly MasterDbContext _context;

    public UserRepository(MasterDbContext context) => _context = context;

    public Task<AppUser?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => _context.Set<AppUser>().FirstOrDefaultAsync(u => u.Id == id, cancellationToken);

    public Task<AppUser?> GetByIdWithRolesAsync(Guid id, CancellationToken cancellationToken = default)
        => _context.Set<AppUser>()
            .Include(u => u.UserRoles)
                .ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(u => u.Id == id, cancellationToken);

    public Task<AppUser?> GetByIdWithRolesAndPermissionsAsync(Guid id, CancellationToken cancellationToken = default)
        => _context.Set<AppUser>()
            .Include(u => u.UserRoles)
                .ThenInclude(ur => ur.Role)
                    .ThenInclude(r => r.RolePermissions)
                        .ThenInclude(rp => rp.Permission)
            .FirstOrDefaultAsync(u => u.Id == id, cancellationToken);

    public Task<AppUser?> GetByIdForRoleAssignmentAsync(Guid id, CancellationToken cancellationToken = default)
        => _context.Set<AppUser>()
            .Include(u => u.UserRoles)
            .FirstOrDefaultAsync(u => u.Id == id, cancellationToken);

    public Task<AppUser?> GetByNormalizedUserNameWithRolesAndPermissionsAsync(
        string normalizedUserName,
        CancellationToken cancellationToken = default)
        => _context.Set<AppUser>()
            .Include(u => u.UserRoles)
                .ThenInclude(ur => ur.Role)
                    .ThenInclude(r => r.RolePermissions)
                        .ThenInclude(rp => rp.Permission)
            .FirstOrDefaultAsync(u => u.NormalizedUserName == normalizedUserName, cancellationToken);

    public async Task<IReadOnlyList<AppUser>> GetAllWithRolesAsync(CancellationToken cancellationToken = default)
    {
        var users = await _context.Set<AppUser>()
            .Include(u => u.UserRoles)
                .ThenInclude(ur => ur.Role)
            .OrderBy(u => u.UserName)
            .ToListAsync(cancellationToken);

        return users;
    }

    public async Task<IReadOnlyList<string>> GetPermissionCodesForUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var codes = await _context.Set<AppUserRole>()
            .Where(ur => ur.UserId == userId && ur.IsActive)
            .SelectMany(ur => ur.Role.RolePermissions)
            .Select(rp => rp.Permission.Code)
            .Distinct()
            .ToListAsync(cancellationToken);

        return codes;
    }

    public Task<bool> ExistsByNormalizedUserNameAsync(string normalizedUserName, CancellationToken cancellationToken = default)
        => _context.Set<AppUser>().AnyAsync(u => u.NormalizedUserName == normalizedUserName, cancellationToken);

    public Task<bool> ExistsByNormalizedEmailAsync(string normalizedEmail, CancellationToken cancellationToken = default)
        => _context.Set<AppUser>().AnyAsync(u => u.NormalizedEmail == normalizedEmail, cancellationToken);

    public async Task AddAsync(AppUser user, CancellationToken cancellationToken = default)
        => await _context.Set<AppUser>().AddAsync(user, cancellationToken);

    public void Remove(AppUser user) => _context.Set<AppUser>().Remove(user);

    public void RemoveUserRoles(IEnumerable<AppUserRole> userRoles)
        => _context.Set<AppUserRole>().RemoveRange(userRoles);
}
