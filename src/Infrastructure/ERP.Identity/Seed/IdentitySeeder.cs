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

            // Get the first tenant for role/user assignment
            var firstTenant = await context.Set<Tenant>().FirstOrDefaultAsync();
            var defaultTenantId = firstTenant?.Id ?? Guid.Empty;

            // ── PERMISSIONS (incremental — skips existing) ──────
            var allPermissions = CreateDefaultPermissions();
            var existingPermCodes = await context.Set<Permission>()
                .Select(p => p.Code).ToListAsync();
            var newPermissions = allPermissions
                .Where(p => !existingPermCodes.Contains(p.Code)).ToList();

            if (newPermissions.Count > 0)
            {
                await context.Set<Permission>().AddRangeAsync(newPermissions);
                await context.SaveChangesAsync();
                logger.LogInformation("Seeded {Count} new permissions.", newPermissions.Count);
            }

            // Reload all permissions from DB (needed for role-permission linking)
            var permissions = await context.Set<Permission>().ToListAsync();

            // ── ROLES (incremental — skips existing) ─────────────
            var adminRole = await SeedRoleIfMissing(context, "00000000-0000-0000-0000-000000000001",
                "Admin", "Full system access", true);
            var operatorRole = await SeedRoleIfMissing(context, "00000000-0000-0000-0000-000000000002",
                "Production Operator", "Can manage job cards and production", true);
            var qaRole = await SeedRoleIfMissing(context, "00000000-0000-0000-0000-000000000003",
                "QA Inspector", "Can inspect and approve/reject quality", true);
            var viewerRole = await SeedRoleIfMissing(context, "00000000-0000-0000-0000-000000000004",
                "Viewer", "Read-only access", true);

            await context.SaveChangesAsync();

            // ── ROLE-PERMISSION ASSIGNMENTS (incremental) ────────
            await SeedRolePermissions(context, adminRole, permissions, _ => true);
            await SeedRolePermissions(context, operatorRole, permissions,
                p => p.Category is "WorkOrder" or "JobCard" or "Production");
            await SeedRolePermissions(context, qaRole, permissions,
                p => p.Category == "QA" || p.Code.EndsWith(".view"));
            await SeedRolePermissions(context, viewerRole, permissions,
                p => p.Code.EndsWith(".view"));

            await context.SaveChangesAsync();

            // ── ADMIN USER (incremental) ─────────────────────────
            await SeedUserIfMissing(context, new AppUser
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
            }, adminRole.Id, defaultTenantId, firstTenant, assignedBy: null, globalPlantAccess: true, logger);

            // ── SAMPLE USERS (Development only, incremental) ─────
            if (environment.IsDevelopment())
            {
                var adminUserId = Guid.Parse("00000000-0000-0000-0000-000000000099");
                var sampleUsers = CreateSampleUsers(adminPassword, defaultTenantId, operatorRole, qaRole, viewerRole);
                foreach (var (user, roleId) in sampleUsers)
                {
                    await SeedUserIfMissing(context, user, roleId, defaultTenantId,
                        firstTenant, assignedBy: adminUserId, globalPlantAccess: false, logger);
                }
            }

            // ── MODULES & MENUS (converge to desired state on every boot) ───
            await SeedModulesAndMenusAsync(context, permissions, logger);
            logger.LogInformation("Identity seed completed. {PermCount} permissions, 4 roles, modules & menus verified.",
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

    // ══════════════════════════════════════════════════════════════════
    // INCREMENTAL SEED HELPERS
    // Each checks if the record exists before inserting — safe to run
    // on every startup without dropping the DB.
    // ══════════════════════════════════════════════════════════════════

    private static async Task<AppRole> SeedRoleIfMissing(
        MasterDbContext context, string guidString, string name, string description, bool isSystem)
    {
        var id = Guid.Parse(guidString);
        var existing = await context.Set<AppRole>().FirstOrDefaultAsync(r => r.Id == id);
        if (existing is not null) return existing;

        var role = new AppRole
        {
            Id = id,
            Name = name,
            NormalizedName = name.ToUpperInvariant(),
            Description = description,
            IsSystemRole = isSystem
        };
        await context.Set<AppRole>().AddAsync(role);
        return role;
    }

    private static async Task SeedRolePermissions(
        MasterDbContext context, AppRole role, List<Permission> allPermissions, Func<Permission, bool> filter)
    {
        var existingPermIds = await context.Set<AppRolePermission>()
            .Where(rp => rp.RoleId == role.Id)
            .Select(rp => rp.PermissionId)
            .ToListAsync();

        var newAssignments = allPermissions
            .Where(filter)
            .Where(p => !existingPermIds.Contains(p.Id))
            .Select(p => new AppRolePermission { RoleId = role.Id, PermissionId = p.Id })
            .ToList();

        if (newAssignments.Count > 0)
            await context.Set<AppRolePermission>().AddRangeAsync(newAssignments);
    }

    private static async Task SeedUserIfMissing(
        MasterDbContext context, AppUser user, Guid roleId, Guid tenantId,
        Tenant? tenant, Guid? assignedBy, bool globalPlantAccess, ILogger logger)
    {
        if (await context.Set<AppUser>().AnyAsync(u => u.Id == user.Id))
            return;

        await context.Set<AppUser>().AddAsync(user);

        user.UserRoles.Add(new AppUserRole
        {
            UserId = user.Id,
            RoleId = roleId,
            TenantId = tenantId,
            AssignedBy = assignedBy
        });

        if (tenant is not null)
        {
            context.Set<UserTenant>().Add(new UserTenant
            {
                UserId = user.Id,
                TenantId = tenant.Id,
                IsDefault = true,
                HasGlobalPlantAccess = globalPlantAccess,
                IsActive = true,
                JoinedAt = DateTime.UtcNow
            });
        }

        logger.LogInformation("Seeded user: {UserName} ({FullName})", user.UserName, $"{user.FirstName} {user.LastName}");
    }

    /// <summary>
    /// Creates sample users for development/testing — one per role.
    /// All use the same password as the admin user for convenience.
    /// Returns a list of (AppUser, RoleId) tuples.
    /// </summary>
    private static List<(AppUser User, Guid RoleId)> CreateSampleUsers(
        string password,
        Guid tenantId,
        AppRole operatorRole,
        AppRole qaRole,
        AppRole viewerRole)
    {
        var hash = BCrypt.Net.BCrypt.HashPassword(password);

        return
        [
            (new AppUser
            {
                Id = Guid.Parse("00000000-0000-0000-0000-000000000101"),
                UserName = "operator",
                NormalizedUserName = "OPERATOR",
                Email = "operator@erp.local",
                NormalizedEmail = "OPERATOR@ERP.LOCAL",
                PasswordHash = hash,
                FirstName = "Raj",
                LastName = "Kumar",
                IsActive = true
            }, operatorRole.Id),

            (new AppUser
            {
                Id = Guid.Parse("00000000-0000-0000-0000-000000000102"),
                UserName = "inspector",
                NormalizedUserName = "INSPECTOR",
                Email = "inspector@erp.local",
                NormalizedEmail = "INSPECTOR@ERP.LOCAL",
                PasswordHash = hash,
                FirstName = "Priya",
                LastName = "Sharma",
                IsActive = true
            }, qaRole.Id),

            (new AppUser
            {
                Id = Guid.Parse("00000000-0000-0000-0000-000000000103"),
                UserName = "viewer",
                NormalizedUserName = "VIEWER",
                Email = "viewer@erp.local",
                NormalizedEmail = "VIEWER@ERP.LOCAL",
                PasswordHash = hash,
                FirstName = "Arun",
                LastName = "Patel",
                IsActive = true
            }, viewerRole.Id),
        ];
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
    // ── Desired-State Records ─────────────────────────────────────────────────

    private sealed record ModuleDef(
        Guid Id, string Code, string Name, string? Icon, int DisplayOrder);

    // ParentCode = null means top-level menu within its module.
    private sealed record MenuDef(
        string Code, string ModuleCode, string Name, string? Icon,
        string? Route, int DisplayOrder, string? ParentCode = null);

    private sealed record MenuPermDef(string MenuCode, string PermissionCode);

    private static IReadOnlyList<ModuleDef> GetDesiredModules() =>
    [
        new(Guid.Parse("30000000-0000-0000-0000-000000000001"), "PRODUCTION",  "Production",       "lucide-factory",      1),
        new(Guid.Parse("30000000-0000-0000-0000-000000000002"), "QUALITY",     "Quality Assurance","lucide-shield-check", 2),
        new(Guid.Parse("30000000-0000-0000-0000-000000000003"), "WORKFLOW",    "Workflow Engine",  "lucide-git-branch",   3),
        new(Guid.Parse("30000000-0000-0000-0000-000000000004"), "ADMIN",       "Administration",   "lucide-settings",    99),
    ];

    private static IReadOnlyList<MenuDef> GetDesiredMenus() =>
    [
        // Production — Work Orders (parent + children)
        new("MENU_WO",           "PRODUCTION", "Work Orders",         "lucide-clipboard-list", null,                                   1),
        new("MENU_WO_LIST",      "PRODUCTION", "Work Order List",     "lucide-list",           "/production/work-orders",              1, "MENU_WO"),
        new("MENU_WO_CREATE",    "PRODUCTION", "Create Work Order",   "lucide-plus-circle",    "/production/work-orders/create",       2, "MENU_WO"),
        new("MENU_WO_APPROVALS", "PRODUCTION", "Approvals",           "lucide-check-square",   "/production/work-orders/approvals",    3, "MENU_WO"),
        // Production — Job Cards (parent + children)
        new("MENU_JC",           "PRODUCTION", "Job Cards",           "lucide-file-text",      null,                                   2),
        new("MENU_JC_LIST",      "PRODUCTION", "Job Card List",       "lucide-list",           "/production/job-cards",                1, "MENU_JC"),
        new("MENU_JC_CREATE",    "PRODUCTION", "Create Job Card",     "lucide-plus-circle",    "/production/job-cards/create",         2, "MENU_JC"),
        // Production — Dashboard (standalone)
        new("MENU_PROD_DASH",    "PRODUCTION", "Production Dashboard","lucide-bar-chart-3",    "/production/dashboard",                3),
        // Quality Assurance
        new("MENU_QA_INSPECT",   "QUALITY",    "QA Inspections",      "lucide-search",         "/quality/inspections",                 1),
        new("MENU_QA_RESULTS",   "QUALITY",    "QA Results",          "lucide-check-circle",   "/quality/results",                     2),
        // Workflow Engine
        new("MENU_WF_TEMPLATES", "WORKFLOW",   "Process Templates",   "lucide-workflow",       "/workflow/templates",                  1),
        // Administration
        new("MENU_USERS",        "ADMIN",      "User Management",     "lucide-users",          "/admin/users",                         1),
        new("MENU_ROLES",        "ADMIN",      "Role Management",     "lucide-shield",         "/admin/roles",                         2),
        new("MENU_TENANTS",      "ADMIN",      "Tenant Management",   "lucide-building-2",     "/admin/tenants",                       3),
    ];

    private static IReadOnlyList<MenuPermDef> GetDesiredMenuPermissions() =>
    [
        new("MENU_WO",           "workorder.view"),
        new("MENU_WO_LIST",      "workorder.view"),
        new("MENU_WO_CREATE",    "workorder.create"),
        new("MENU_WO_APPROVALS", "workorder.view"),
        new("MENU_JC",           "jobcard.view"),
        new("MENU_JC_LIST",      "jobcard.view"),
        new("MENU_JC_CREATE",    "jobcard.create"),
        new("MENU_PROD_DASH",    "production.view"),
        new("MENU_QA_INSPECT",   "qa.inspect"),
        new("MENU_QA_RESULTS",   "qa.view"),
        new("MENU_WF_TEMPLATES", "workflow.view"),
        new("MENU_USERS",        "user.view"),
        new("MENU_ROLES",        "role.view"),
        // MENU_TENANTS: no permission — visible to all admins via [Authorize(Roles="Admin")]
    ];

    // ── Convergence Logic ─────────────────────────────────────────────────────

    /// <summary>
    /// Converges the database to the desired module/menu state on every boot.
    ///
    /// Strategy (similar to Terraform/K8s reconciliation):
    ///   INSERT  — records defined in code but missing in DB.
    ///   UPDATE  — records where any field has changed.
    ///   DEACTIVATE — records in DB but removed from the desired definition
    ///                (soft-delete: IsVisible=false / IsActive=false).
    ///   RE-ACTIVATE — records that were soft-deleted but are back in the definition.
    ///
    /// Uses Code as the stable business key (not DB-generated GUIDs).
    /// </summary>
    private static async Task SeedModulesAndMenusAsync(
        MasterDbContext context,
        List<Permission> permissions,
        ILogger logger)
    {
        var permLookup = permissions.ToDictionary(p => p.Code, p => p.Id);

        // ── MODULES ──────────────────────────────────────────────────────────
        var desiredModules = GetDesiredModules();
        var existingModules = await context.Set<Module>().ToDictionaryAsync(m => m.Code);
        var moduleIdMap = new Dictionary<string, Guid>();

        foreach (var def in desiredModules)
        {
            if (existingModules.TryGetValue(def.Code, out var existing))
            {
                moduleIdMap[def.Code] = existing.Id;
                if (existing.Name != def.Name || existing.Icon != def.Icon ||
                    existing.DisplayOrder != def.DisplayOrder || !existing.IsActive)
                {
                    existing.Name = def.Name;
                    existing.Icon = def.Icon;
                    existing.DisplayOrder = def.DisplayOrder;
                    existing.IsActive = true;
                }
            }
            else
            {
                var mod = new Module
                {
                    Id = def.Id,
                    Code = def.Code,
                    Name = def.Name,
                    Icon = def.Icon,
                    DisplayOrder = def.DisplayOrder,
                    IsActive = true,
                };
                context.Set<Module>().Add(mod);
                existingModules[def.Code] = mod;
                moduleIdMap[def.Code] = def.Id;
            }
        }

        // Deactivate modules removed from the desired definition
        var desiredModuleCodes = desiredModules.Select(d => d.Code).ToHashSet();
        foreach (var mod in existingModules.Values.Where(m => !desiredModuleCodes.Contains(m.Code) && m.IsActive))
            mod.IsActive = false;

        await context.SaveChangesAsync();

        // ── MENUS ─────────────────────────────────────────────────────────────
        var desiredMenus = GetDesiredMenus();
        var existingMenus = await context.Set<Menu>().ToDictionaryAsync(m => m.Code);
        var menuIdMap = new Dictionary<string, Guid>();

        // Top-level first so children can resolve ParentMenuId from menuIdMap
        var ordered = desiredMenus.OrderBy(m => m.ParentCode is null ? 0 : 1).ToList();
        int inserted = 0, updated = 0;

        foreach (var def in ordered)
        {
            var moduleId = moduleIdMap[def.ModuleCode];
            Guid? parentId = def.ParentCode is not null
                ? menuIdMap.GetValueOrDefault(def.ParentCode)
                : null;

            if (existingMenus.TryGetValue(def.Code, out var existing))
            {
                menuIdMap[def.Code] = existing.Id;
                if (existing.Name != def.Name || existing.Icon != def.Icon ||
                    existing.Route != def.Route || existing.DisplayOrder != def.DisplayOrder ||
                    existing.ParentMenuId != parentId || existing.ModuleId != moduleId ||
                    !existing.IsVisible || !existing.IsEnabled)
                {
                    existing.Name = def.Name;
                    existing.Icon = def.Icon;
                    existing.Route = def.Route;
                    existing.DisplayOrder = def.DisplayOrder;
                    existing.ParentMenuId = parentId;
                    existing.ModuleId = moduleId;
                    existing.IsVisible = true;
                    existing.IsEnabled = true;
                    updated++;
                }
            }
            else
            {
                var menu = new Menu
                {
                    Id = Guid.NewGuid(),
                    Code = def.Code,
                    ModuleId = moduleId,
                    ParentMenuId = parentId,
                    Name = def.Name,
                    Icon = def.Icon,
                    Route = def.Route,
                    DisplayOrder = def.DisplayOrder,
                    CreatedAt = DateTime.UtcNow,
                };
                context.Set<Menu>().Add(menu);
                existingMenus[def.Code] = menu;
                menuIdMap[def.Code] = menu.Id;
                inserted++;
            }
        }

        // Soft-delete menus removed from the desired definition
        var desiredMenuCodes = desiredMenus.Select(d => d.Code).ToHashSet();
        int deactivated = 0;
        foreach (var menu in existingMenus.Values.Where(m => !desiredMenuCodes.Contains(m.Code) && m.IsVisible))
        {
            menu.IsVisible = false;
            deactivated++;
        }

        await context.SaveChangesAsync();

        // ── MENU PERMISSIONS ─────────────────────────────────────────────────
        var existingPermPairs = await context.Set<MenuPermission>()
            .Select(mp => new { mp.MenuId, mp.PermissionId })
            .ToListAsync();
        var existingPermSet = existingPermPairs
            .Select(mp => (mp.MenuId, mp.PermissionId))
            .ToHashSet();

        var newPerms = new List<MenuPermission>();
        foreach (var def in GetDesiredMenuPermissions())
        {
            if (!menuIdMap.TryGetValue(def.MenuCode, out var menuId)) continue;
            if (!permLookup.TryGetValue(def.PermissionCode, out var permId)) continue;
            if (!existingPermSet.Contains((menuId, permId)))
            {
                newPerms.Add(new MenuPermission
                {
                    Id = Guid.NewGuid(),
                    MenuId = menuId,
                    PermissionId = permId,
                    IsRequired = true,
                });
            }
        }

        if (newPerms.Count > 0)
        {
            context.Set<MenuPermission>().AddRange(newPerms);
            await context.SaveChangesAsync();
        }

        logger.LogInformation(
            "Menu seed: {Ins} inserted, {Upd} updated, {Del} deactivated, {Perm} permissions added.",
            inserted, updated, deactivated, newPerms.Count);
    }
}
