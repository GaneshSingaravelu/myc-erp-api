using ERP.Identity.Entities;
using ERP.Persistence.Context;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ERP.Identity.Seed;

/// <summary>
/// IDENTITY SEEDER — Creates default admin user, roles, and permissions on first run.
///
/// WHAT IS SEEDING? (Interview Concept)
/// ======================================
/// Seeding is inserting initial data that the application needs to function.
/// For an ERP, you need at least:
/// - An admin user (so someone can log in and create other users)
/// - Default roles (Admin, Operator, QA Inspector, etc.)
/// - Permission definitions (what actions exist in the system)
///
/// WHEN DOES IT RUN?
/// ==================
/// Called from Program.cs on app startup:
///   await IdentitySeeder.SeedAsync(scope.ServiceProvider);
///
/// It checks if data already exists before inserting (idempotent — safe to run multiple times).
/// </summary>
public static class IdentitySeeder
{
    public static async Task SeedAsync(IServiceProvider serviceProvider)
    {
        using var scope = serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<AppDbContext>>();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();

        var environment = scope.ServiceProvider.GetRequiredService<IHostEnvironment>();

        // Admin password MUST be provided via configuration.
        // In Development: set in appsettings.Development.json
        // In Production:  set via environment variable (ERP_SEED__DefaultAdminPassword)
        //                 or user-secrets, or a vault — NEVER in tracked config files.
        var adminPassword = configuration["SeedSettings:DefaultAdminPassword"];

        if (string.IsNullOrWhiteSpace(adminPassword))
        {
            if (environment.IsDevelopment())
            {
                logger.LogWarning("SeedSettings:DefaultAdminPassword is not configured. Skipping identity seed.");
                return;
            }

            throw new InvalidOperationException(
                "SeedSettings:DefaultAdminPassword is required for first-run seeding. " +
                "Set it via environment variable, user-secrets, or a secure vault.");
        }

        // Block well-known defaults in non-Development environments
        if (!environment.IsDevelopment() && adminPassword == "Admin@123")
        {
            throw new InvalidOperationException(
                "Default admin password 'Admin@123' cannot be used outside Development. " +
                "Set a strong password via SeedSettings:DefaultAdminPassword.");
        }

        try
        {
            // Only seed if no users exist (first run)
            if (await context.Set<AppUser>().AnyAsync())
            {
                logger.LogDebug("Identity data already seeded. Skipping.");
                return;
            }

            logger.LogInformation("Seeding identity data...");

            // ── PERMISSIONS ──────────────────────────────────────
            var permissions = CreateDefaultPermissions();
            await context.Set<Permission>().AddRangeAsync(permissions);

            // ── ROLES ────────────────────────────────────────────
            var adminRole = new AppRole
            {
                Id = Guid.Parse("00000000-0000-0000-0000-000000000001"),
                Name = "Admin",
                Description = "Full system access",
                IsSystemRole = true
            };

            var operatorRole = new AppRole
            {
                Id = Guid.Parse("00000000-0000-0000-0000-000000000002"),
                Name = "Production Operator",
                Description = "Can manage job cards and production",
                IsSystemRole = true
            };

            var qaRole = new AppRole
            {
                Id = Guid.Parse("00000000-0000-0000-0000-000000000003"),
                Name = "QA Inspector",
                Description = "Can inspect and approve/reject quality",
                IsSystemRole = true
            };

            var viewerRole = new AppRole
            {
                Id = Guid.Parse("00000000-0000-0000-0000-000000000004"),
                Name = "Viewer",
                Description = "Read-only access",
                IsSystemRole = true
            };

            await context.Set<AppRole>().AddRangeAsync(adminRole, operatorRole, qaRole, viewerRole);

            // ── ROLE-PERMISSION ASSIGNMENTS ──────────────────────
            // Admin gets ALL permissions
            foreach (var p in permissions)
                adminRole.RolePermissions.Add(new AppRolePermission { RoleId = adminRole.Id, PermissionId = p.Id });

            // Operator gets production permissions
            foreach (var p in permissions.Where(p => p.Module is "WorkOrder" or "JobCard" or "Production"))
                operatorRole.RolePermissions.Add(new AppRolePermission { RoleId = operatorRole.Id, PermissionId = p.Id });

            // QA gets QA permissions + view permissions
            foreach (var p in permissions.Where(p => p.Module == "QA" || p.Code.EndsWith(".view")))
                qaRole.RolePermissions.Add(new AppRolePermission { RoleId = qaRole.Id, PermissionId = p.Id });

            // Viewer gets only view permissions
            foreach (var p in permissions.Where(p => p.Code.EndsWith(".view")))
                viewerRole.RolePermissions.Add(new AppRolePermission { RoleId = viewerRole.Id, PermissionId = p.Id });

            // ── ADMIN USER ───────────────────────────────────────
            var adminUser = new AppUser
            {
                Id = Guid.Parse("00000000-0000-0000-0000-000000000099"),
                UserName = "admin",
                Email = "admin@erp.local",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(adminPassword),
                FirstName = "System",
                LastName = "Administrator",
                IsActive = true
            };

            await context.Set<AppUser>().AddAsync(adminUser);

            // Assign admin role
            adminUser.UserRoles.Add(new AppUserRole
            {
                UserId = adminUser.Id,
                RoleId = adminRole.Id,
                AssignedBy = "system"
            });

            await context.SaveChangesAsync();
            logger.LogInformation("Identity data seeded: {PermCount} permissions, 4 roles, 1 admin user (username: admin). Change the default password immediately.",
                permissions.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error seeding identity data");
        }
    }

    private static List<Permission> CreateDefaultPermissions()
    {
        var id = 1;
        Guid NextId() => Guid.Parse($"10000000-0000-0000-0000-{id++:D12}");

        return
        [
            // User Management
            new() { Id = NextId(), Code = "user.view", Name = "View Users", Module = "User" },
            new() { Id = NextId(), Code = "user.create", Name = "Create Users", Module = "User" },
            new() { Id = NextId(), Code = "user.update", Name = "Update Users", Module = "User" },
            new() { Id = NextId(), Code = "user.delete", Name = "Delete Users", Module = "User" },
            new() { Id = NextId(), Code = "user.assignroles", Name = "Assign Roles", Module = "User" },

            // Role Management
            new() { Id = NextId(), Code = "role.view", Name = "View Roles", Module = "Role" },
            new() { Id = NextId(), Code = "role.create", Name = "Create Roles", Module = "Role" },
            new() { Id = NextId(), Code = "role.update", Name = "Update Roles", Module = "Role" },
            new() { Id = NextId(), Code = "role.delete", Name = "Delete Roles", Module = "Role" },
            new() { Id = NextId(), Code = "role.assignpermissions", Name = "Assign Permissions", Module = "Role" },

            // Work Order
            new() { Id = NextId(), Code = "workorder.view", Name = "View Work Orders", Module = "WorkOrder" },
            new() { Id = NextId(), Code = "workorder.create", Name = "Create Work Orders", Module = "WorkOrder" },
            new() { Id = NextId(), Code = "workorder.update", Name = "Update Work Orders", Module = "WorkOrder" },
            new() { Id = NextId(), Code = "workorder.close", Name = "Close Work Orders", Module = "WorkOrder" },

            // Job Card
            new() { Id = NextId(), Code = "jobcard.view", Name = "View Job Cards", Module = "JobCard" },
            new() { Id = NextId(), Code = "jobcard.create", Name = "Create Job Cards", Module = "JobCard" },
            new() { Id = NextId(), Code = "jobcard.split", Name = "Split Job Cards", Module = "JobCard" },

            // Production
            new() { Id = NextId(), Code = "production.start", Name = "Start Production", Module = "Production" },
            new() { Id = NextId(), Code = "production.complete", Name = "Complete Production", Module = "Production" },
            new() { Id = NextId(), Code = "production.view", Name = "View Production", Module = "Production" },

            // QA
            new() { Id = NextId(), Code = "qa.inspect", Name = "Perform QA Inspection", Module = "QA" },
            new() { Id = NextId(), Code = "qa.approve", Name = "Approve QA", Module = "QA" },
            new() { Id = NextId(), Code = "qa.reject", Name = "Reject QA", Module = "QA" },
            new() { Id = NextId(), Code = "qa.view", Name = "View QA Results", Module = "QA" },

            // Workflow
            new() { Id = NextId(), Code = "workflow.view", Name = "View Process Templates", Module = "Workflow" },
            new() { Id = NextId(), Code = "workflow.create", Name = "Create Process Templates", Module = "Workflow" },
            new() { Id = NextId(), Code = "workflow.update", Name = "Update Process Templates", Module = "Workflow" },
            new() { Id = NextId(), Code = "workflow.delete", Name = "Delete Process Templates", Module = "Workflow" },
        ];
    }
}
