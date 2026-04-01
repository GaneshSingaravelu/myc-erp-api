using ERP.Identity.DTOs;
using ERP.Identity.Entities;
using ERP.Persistence.Context;
using ERP.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace ERP.Identity.Services;

public class UserService : IUserService
{
    private readonly AppDbContext _context;

    public UserService(AppDbContext context)
    {
        _context = context;
    }

    public async Task<Result<UserDto>> GetByIdAsync(Guid id)
    {
        var user = await _context.Set<AppUser>()
            .Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(u => u.Id == id);

        if (user is null)
            return Result.Failure<UserDto>(Error.NotFound("User", id));

        return Result.Success(MapToDto(user));
    }

    public async Task<Result<IReadOnlyList<UserDto>>> GetAllAsync()
    {
        var users = await _context.Set<AppUser>()
            .Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
            .OrderBy(u => u.UserName)
            .ToListAsync();

        return Result.Success<IReadOnlyList<UserDto>>(users.Select(MapToDto).ToList());
    }

    public async Task<Result<UserDto>> CreateAsync(CreateUserRequest request)
    {
        if (await _context.Set<AppUser>().AnyAsync(u => u.UserName == request.UserName))
            return Result.Failure<UserDto>(Error.Conflict("User.UserNameExists", $"Username '{request.UserName}' already exists."));

        if (await _context.Set<AppUser>().AnyAsync(u => u.Email == request.Email))
            return Result.Failure<UserDto>(Error.Conflict("User.EmailExists", $"Email '{request.Email}' already exists."));

        var user = new AppUser
        {
            UserName = request.UserName,
            Email = request.Email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            FirstName = request.FirstName,
            LastName = request.LastName,
            IsActive = true
        };

        await _context.Set<AppUser>().AddAsync(user);
        await _context.SaveChangesAsync();

        return Result.Success(MapToDto(user));
    }

    public async Task<Result<UserDto>> UpdateAsync(Guid id, UpdateUserRequest request)
    {
        var user = await _context.Set<AppUser>()
            .Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(u => u.Id == id);

        if (user is null)
            return Result.Failure<UserDto>(Error.NotFound("User", id));

        user.Email = request.Email;
        user.FirstName = request.FirstName;
        user.LastName = request.LastName;
        user.IsActive = request.IsActive;

        await _context.SaveChangesAsync();

        return Result.Success(MapToDto(user));
    }

    public async Task<Result> DeleteAsync(Guid id)
    {
        var user = await _context.Set<AppUser>().FindAsync(id);
        if (user is null)
            return Result.Failure(Error.NotFound("User", id));

        _context.Set<AppUser>().Remove(user); // Soft delete via interceptor
        await _context.SaveChangesAsync();

        return Result.Success();
    }

    public async Task<Result> AssignRolesAsync(Guid userId, AssignRolesRequest request)
    {
        var user = await _context.Set<AppUser>()
            .Include(u => u.UserRoles)
            .FirstOrDefaultAsync(u => u.Id == userId);

        if (user is null)
            return Result.Failure(Error.NotFound("User", userId));

        // Remove existing role assignments
        _context.Set<AppUserRole>().RemoveRange(user.UserRoles);

        // Add new role assignments
        foreach (var roleId in request.RoleIds)
        {
            var roleExists = await _context.Set<AppRole>().AnyAsync(r => r.Id == roleId);
            if (!roleExists)
                return Result.Failure(Error.NotFound("Role", roleId));

            user.UserRoles.Add(new AppUserRole { UserId = userId, RoleId = roleId });
        }

        await _context.SaveChangesAsync();
        return Result.Success();
    }

    private static UserDto MapToDto(AppUser user) => new(
        Id: user.Id,
        UserName: user.UserName,
        Email: user.Email,
        FirstName: user.FirstName,
        LastName: user.LastName,
        FullName: user.FullName,
        IsActive: user.IsActive,
        LastLoginAt: user.LastLoginAt,
        Roles: user.UserRoles.Select(ur => ur.Role.Name).ToList());
}
