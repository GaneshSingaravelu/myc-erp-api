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

            // ── MODULES & MENUS (only if no modules exist yet) ───
            if (!await context.Set<Module>().AnyAsync())
            {
                SeedModulesAndMenus(context, permissions);
                logger.LogInformation("Seeded modules and menus.");
            }

            await context.SaveChangesAsync();
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
