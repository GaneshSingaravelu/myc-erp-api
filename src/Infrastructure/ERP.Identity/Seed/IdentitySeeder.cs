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
/// - Tenant records (from configuration)
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
        var context = scope.ServiceProvider.GetRequiredService<MasterDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<MasterDbContext>>();
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
            // ── SEED TENANTS (from configuration) ───────────────
            await SeedTenantsAsync(context, configuration, logger);

            // Only seed identity if no users exist (first run)
            if (await context.Set<AppUser>().AnyAsync())
            {
                logger.LogDebug("Identity data already seeded. Skipping.");
                return;
            }

            logger.LogInformation("Seeding identity data...");

            // Get the first tenant for admin role assignment
            var firstTenant = await context.Set<Tenant>().FirstOrDefaultAsync();
            var defaultTenantId = firstTenant?.Id ?? Guid.Empty;

            // ── PERMISSIONS ──────────────────────────────────────
            var permissions = CreateDefaultPermissions();
            await context.Set<Permission>().AddRangeAsync(permissions);

            // ── ROLES ────────────────────────────────────────────
            var adminRole = new AppRole
            {
                Id = Guid.Parse("00000000-0000-0000-0000-000000000001"),
                Name = "Admin",
                NormalizedName = "ADMIN",
                Description = "Full system access",
                IsSystemRole = true
            };

            var operatorRole = new AppRole
            {
                Id = Guid.Parse("00000000-0000-0000-0000-000000000002"),
                Name = "Production Operator",
                NormalizedName = "PRODUCTION OPERATOR",
                Description = "Can manage job cards and production",
                IsSystemRole = true
            };

            var qaRole = new AppRole
            {
                Id = Guid.Parse("00000000-0000-0000-0000-000000000003"),
                Name = "QA Inspector",
                NormalizedName = "QA INSPECTOR",
                Description = "Can inspect and approve/reject quality",
                IsSystemRole = true
            };

            var viewerRole = new AppRole
            {
                Id = Guid.Parse("00000000-0000-0000-0000-000000000004"),
                Name = "Viewer",
                NormalizedName = "VIEWER",
                Description = "Read-only access",
                IsSystemRole = true
            };

            await context.Set<AppRole>().AddRangeAsync(adminRole, operatorRole, qaRole, viewerRole);

            // ── ROLE-PERMISSION ASSIGNMENTS ──────────────────────
            // Admin gets ALL permissions
            foreach (var p in permissions)
                adminRole.RolePermissions.Add(new AppRolePermission { RoleId = adminRole.Id, PermissionId = p.Id });

            // Operator gets production permissions
            foreach (var p in permissions.Where(p => p.Category is "WorkOrder" or "JobCard" or "Production"))
                operatorRole.RolePermissions.Add(new AppRolePermission { RoleId = operatorRole.Id, PermissionId = p.Id });

            // QA gets QA permissions + view permissions
            foreach (var p in permissions.Where(p => p.Category == "QA" || p.Code.EndsWith(".view")))
                qaRole.RolePermissions.Add(new AppRolePermission { RoleId = qaRole.Id, PermissionId = p.Id });

            // Viewer gets only view permissions
            foreach (var p in permissions.Where(p => p.Code.EndsWith(".view")))
                viewerRole.RolePermissions.Add(new AppRolePermission { RoleId = viewerRole.Id, PermissionId = p.Id });

            // ── ADMIN USER ───────────────────────────────────────
            var adminUser = new AppUser
            {
                Id = Guid.Parse("00000000-0000-0000-0000-000000000099"),
                UserName = "admin",
                NormalizedUserName = "ADMIN",
                Email = "admin@erp.local",
                NormalizedEmail = "ADMIN@ERP.LOCAL",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(adminPassword),
                FirstName = "System",
                LastName = "Administrator",
                IsActive = true
            };

            await context.Set<AppUser>().AddAsync(adminUser);

            // Assign admin role (with tenant context)
            adminUser.UserRoles.Add(new AppUserRole
            {
                UserId = adminUser.Id,
                RoleId = adminRole.Id,
                TenantId = defaultTenantId,
                AssignedBy = null
            });

            // Add admin to the default tenant
            if (firstTenant is not null)
            {
                context.Set<UserTenant>().Add(new UserTenant
                {
                    UserId = adminUser.Id,
                    TenantId = firstTenant.Id,
                    IsDefault = true,
                    HasGlobalPlantAccess = true,
                    IsActive = true,
                    JoinedAt = DateTime.UtcNow
                });
            }

            // ── MODULES & MENUS ──────────────────────────────────
            SeedModulesAndMenus(context, permissions);

            await context.SaveChangesAsync();
            logger.LogInformation("Identity data seeded: {PermCount} permissions, 4 roles, 1 admin user (username: admin), modules & menus. Change the default password immediately.",
                permissions.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error seeding identity data");
        }
    }

    /// <summary>
    /// Seeds tenant records from configuration into the Master DB.
    /// Skips tenants that already exist (idempotent).
    /// </summary>
    private static async Task SeedTenantsAsync(
        MasterDbContext context,
        IConfiguration configuration,
        ILogger logger)
    {
        var tenantConfigs = configuration.GetSection("Tenants").GetChildren();

        foreach (var tenantConfig in tenantConfigs)
        {
            var tenantId = tenantConfig["Id"];
            if (string.IsNullOrWhiteSpace(tenantId) || !Guid.TryParse(tenantId, out var id))
                continue;

            // Skip if tenant already exists
            if (await context.Set<Tenant>().AnyAsync(t => t.Id == id))
                continue;

            var tenant = new Tenant
            {
                Id = id,
                Code = tenantConfig["Identifier"] ?? "unknown",
                Name = tenantConfig["Name"] ?? "Unknown Tenant",
                ConnectionString = tenantConfig["ConnectionString"] ?? string.Empty,
                DbProvider = tenantConfig["DbProvider"] ?? "SqlServer",
                IsActive = bool.TryParse(tenantConfig["IsActive"], out var isActive) && isActive,
                CreatedAt = DateTime.UtcNow
            };

            await context.Set<Tenant>().AddAsync(tenant);
            logger.LogInformation("Seeded tenant: {TenantName} ({TenantCode})", tenant.Name, tenant.Code);
        }

        await context.SaveChangesAsync();
    }

    private static List<Permission> CreateDefaultPermissions()
    {
        var id = 1;
        Guid NextId() => Guid.Parse($"10000000-0000-0000-0000-{id++:D12}");

        return
        [
            // User Management
            new() { Id = NextId(), Code = "user.view", Name = "View Users", Category = "User" },
            new() { Id = NextId(), Code = "user.create", Name = "Create Users", Category = "User" },
            new() { Id = NextId(), Code = "user.update", Name = "Update Users", Category = "User" },
            new() { Id = NextId(), Code = "user.delete", Name = "Delete Users", Category = "User" },
            new() { Id = NextId(), Code = "user.assignroles", Name = "Assign Roles", Category = "User" },

            // Role Management
            new() { Id = NextId(), Code = "role.view", Name = "View Roles", Category = "Role" },
            new() { Id = NextId(), Code = "role.create", Name = "Create Roles", Category = "Role" },
            new() { Id = NextId(), Code = "role.update", Name = "Update Roles", Category = "Role" },
            new() { Id = NextId(), Code = "role.delete", Name = "Delete Roles", Category = "Role" },
            new() { Id = NextId(), Code = "role.assignpermissions", Name = "Assign Permissions", Category = "Role" },

            // Work Order
            new() { Id = NextId(), Code = "workorder.view", Name = "View Work Orders", Category = "WorkOrder" },
            new() { Id = NextId(), Code = "workorder.create", Name = "Create Work Orders", Category = "WorkOrder" },
            new() { Id = NextId(), Code = "workorder.update", Name = "Update Work Orders", Category = "WorkOrder" },
            new() { Id = NextId(), Code = "workorder.close", Name = "Close Work Orders", Category = "WorkOrder" },

            // Job Card
            new() { Id = NextId(), Code = "jobcard.view", Name = "View Job Cards", Category = "JobCard" },
            new() { Id = NextId(), Code = "jobcard.create", Name = "Create Job Cards", Category = "JobCard" },
            new() { Id = NextId(), Code = "jobcard.split", Name = "Split Job Cards", Category = "JobCard" },

            // Production
            new() { Id = NextId(), Code = "production.start", Name = "Start Production", Category = "Production" },
            new() { Id = NextId(), Code = "production.complete", Name = "Complete Production", Category = "Production" },
            new() { Id = NextId(), Code = "production.view", Name = "View Production", Category = "Production" },

            // QA
            new() { Id = NextId(), Code = "qa.inspect", Name = "Perform QA Inspection", Category = "QA" },
            new() { Id = NextId(), Code = "qa.approve", Name = "Approve QA", Category = "QA" },
            new() { Id = NextId(), Code = "qa.reject", Name = "Reject QA", Category = "QA" },
            new() { Id = NextId(), Code = "qa.view", Name = "View QA Results", Category = "QA" },

            // Workflow
            new() { Id = NextId(), Code = "workflow.view", Name = "View Process Templates", Category = "Workflow" },
            new() { Id = NextId(), Code = "workflow.create", Name = "Create Process Templates", Category = "Workflow" },
            new() { Id = NextId(), Code = "workflow.update", Name = "Update Process Templates", Category = "Workflow" },
            new() { Id = NextId(), Code = "workflow.delete", Name = "Delete Process Templates", Category = "Workflow" },
        ];
    }

    /// <summary>
    /// Seeds modules (sidebar sections) and menus (navigation items) with permission links.
    /// These define the Angular sidebar structure — filtered per user at runtime by MenuService.
    /// </summary>
    private static void SeedModulesAndMenus(MasterDbContext context, List<Permission> permissions)
    {
        var permLookup = permissions.ToDictionary(p => p.Code, p => p.Id);

        var now = DateTime.UtcNow;
        var menuId = 1;
        Guid NextMenuId() => Guid.Parse($"20000000-0000-0000-0000-{menuId++:D12}");

        // ── MODULE: Production ──
        var prodModule = new Module
        {
            Id = Guid.Parse("30000000-0000-0000-0000-000000000001"),
            Code = "PRODUCTION",
            Name = "Production",
            Icon = "lucide-factory",
            DisplayOrder = 1,
            IsActive = true
        };
        context.Set<Module>().Add(prodModule);

        var woMenu = new Menu { Id = NextMenuId(), ModuleId = prodModule.Id, Code = "MENU_WO", Name = "Work Orders", Icon = "lucide-clipboard-list", Route = "/production/work-orders", DisplayOrder = 1, CreatedAt = now };
        var jcMenu = new Menu { Id = NextMenuId(), ModuleId = prodModule.Id, Code = "MENU_JC", Name = "Job Cards", Icon = "lucide-file-text", Route = "/production/job-cards", DisplayOrder = 2, CreatedAt = now };
        var prodDash = new Menu { Id = NextMenuId(), ModuleId = prodModule.Id, Code = "MENU_PROD_DASH", Name = "Production Dashboard", Icon = "lucide-bar-chart-3", Route = "/production/dashboard", DisplayOrder = 3, CreatedAt = now };

        context.Set<Menu>().AddRange(woMenu, jcMenu, prodDash);

        // Link menus to permissions
        context.Set<MenuPermission>().AddRange(
            new MenuPermission { Id = NextMenuId(), MenuId = woMenu.Id, PermissionId = permLookup["workorder.view"], IsRequired = true },
            new MenuPermission { Id = NextMenuId(), MenuId = jcMenu.Id, PermissionId = permLookup["jobcard.view"], IsRequired = true },
            new MenuPermission { Id = NextMenuId(), MenuId = prodDash.Id, PermissionId = permLookup["production.view"], IsRequired = true });

        // ── MODULE: Quality ──
        var qaModule = new Module
        {
            Id = Guid.Parse("30000000-0000-0000-0000-000000000002"),
            Code = "QUALITY",
            Name = "Quality Assurance",
            Icon = "lucide-shield-check",
            DisplayOrder = 2,
            IsActive = true
        };
        context.Set<Module>().Add(qaModule);

        var qaInspect = new Menu { Id = NextMenuId(), ModuleId = qaModule.Id, Code = "MENU_QA_INSPECT", Name = "QA Inspections", Icon = "lucide-search", Route = "/quality/inspections", DisplayOrder = 1, CreatedAt = now };
        var qaResults = new Menu { Id = NextMenuId(), ModuleId = qaModule.Id, Code = "MENU_QA_RESULTS", Name = "QA Results", Icon = "lucide-check-circle", Route = "/quality/results", DisplayOrder = 2, CreatedAt = now };

        context.Set<Menu>().AddRange(qaInspect, qaResults);
        context.Set<MenuPermission>().AddRange(
            new MenuPermission { Id = NextMenuId(), MenuId = qaInspect.Id, PermissionId = permLookup["qa.inspect"], IsRequired = true },
            new MenuPermission { Id = NextMenuId(), MenuId = qaResults.Id, PermissionId = permLookup["qa.view"], IsRequired = true });

        // ── MODULE: Workflow ──
        var wfModule = new Module
        {
            Id = Guid.Parse("30000000-0000-0000-0000-000000000003"),
            Code = "WORKFLOW",
            Name = "Workflow Engine",
            Icon = "lucide-git-branch",
            DisplayOrder = 3,
            IsActive = true
        };
        context.Set<Module>().Add(wfModule);

        var wfTemplates = new Menu { Id = NextMenuId(), ModuleId = wfModule.Id, Code = "MENU_WF_TEMPLATES", Name = "Process Templates", Icon = "lucide-workflow", Route = "/workflow/templates", DisplayOrder = 1, CreatedAt = now };

        context.Set<Menu>().Add(wfTemplates);
        context.Set<MenuPermission>().Add(
            new MenuPermission { Id = NextMenuId(), MenuId = wfTemplates.Id, PermissionId = permLookup["workflow.view"], IsRequired = true });

        // ── MODULE: Administration ──
        var adminModule = new Module
        {
            Id = Guid.Parse("30000000-0000-0000-0000-000000000004"),
            Code = "ADMIN",
            Name = "Administration",
            Icon = "lucide-settings",
            DisplayOrder = 99,
            IsActive = true
        };
        context.Set<Module>().Add(adminModule);

        var usersMenu = new Menu { Id = NextMenuId(), ModuleId = adminModule.Id, Code = "MENU_USERS", Name = "User Management", Icon = "lucide-users", Route = "/admin/users", DisplayOrder = 1, CreatedAt = now };
        var rolesMenu = new Menu { Id = NextMenuId(), ModuleId = adminModule.Id, Code = "MENU_ROLES", Name = "Role Management", Icon = "lucide-shield", Route = "/admin/roles", DisplayOrder = 2, CreatedAt = now };
        var tenantsMenu = new Menu { Id = NextMenuId(), ModuleId = adminModule.Id, Code = "MENU_TENANTS", Name = "Tenant Management", Icon = "lucide-building-2", Route = "/admin/tenants", DisplayOrder = 3, CreatedAt = now };

        context.Set<Menu>().AddRange(usersMenu, rolesMenu, tenantsMenu);
        context.Set<MenuPermission>().AddRange(
            new MenuPermission { Id = NextMenuId(), MenuId = usersMenu.Id, PermissionId = permLookup["user.view"], IsRequired = true },
            new MenuPermission { Id = NextMenuId(), MenuId = rolesMenu.Id, PermissionId = permLookup["role.view"], IsRequired = true });
        // tenantsMenu: no permission link — visible to all admins via [Authorize(Roles = "Admin")]
    }
}
