using ERP.Identity.Entities;
using ERP.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ERP.Identity.Configuration;

// ============================================================================
// MASTER DB ENTITY CONFIGURATIONS
// ============================================================================
// These define how Identity entities map to database tables in the Master DB.
// Discovered by MasterDbContext via assembly scanning.
// ============================================================================

// ── Tenant ──────────────────────────────────────────────────────────────────

public class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        builder.ToTable("Tenants");

        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();

        builder.Property(t => t.Code).HasMaxLength(50).IsRequired();
        builder.Property(t => t.Name).HasMaxLength(200).IsRequired();
        builder.Property(t => t.ConnectionString).HasMaxLength(500).IsRequired();
        builder.Property(t => t.DbProvider).HasMaxLength(50).IsRequired();
        builder.Property(t => t.CreatedAt).IsRequired();

        builder.HasIndex(t => t.Code).IsUnique();
    }
}

// ── Users ───────────────────────────────────────────────────────────────────

public class AppUserConfiguration : AuditableEntityConfiguration<AppUser>
{
    protected override void ConfigureEntity(EntityTypeBuilder<AppUser> builder)
    {
        builder.ToTable("Users");

        builder.Property(u => u.UserName).HasMaxLength(100).IsRequired();
        builder.Property(u => u.NormalizedUserName).HasMaxLength(100).IsRequired();
        builder.Property(u => u.Email).HasMaxLength(200).IsRequired();
        builder.Property(u => u.NormalizedEmail).HasMaxLength(200).IsRequired();
        builder.Property(u => u.PasswordHash).HasMaxLength(500).IsRequired();
        builder.Property(u => u.SecurityStamp).HasMaxLength(100).IsRequired();
        builder.Property(u => u.ConcurrencyStamp).HasMaxLength(100).IsRequired()
            .IsConcurrencyToken(); // EF Core optimistic concurrency
        builder.Property(u => u.FirstName).HasMaxLength(100).IsRequired();
        builder.Property(u => u.LastName).HasMaxLength(100).IsRequired();
        builder.Property(u => u.DisplayName).HasMaxLength(200);
        builder.Property(u => u.PhoneNumber).HasMaxLength(20);

        // Computed property — not stored in DB
        builder.Ignore(u => u.FullName);

        // Unique indexes for case-insensitive lookups
        builder.HasIndex(u => u.NormalizedUserName).IsUnique();
        builder.HasIndex(u => u.NormalizedEmail).IsUnique();
    }
}

// ── UserTenants ─────────────────────────────────────────────────────────────

public class UserTenantConfiguration : IEntityTypeConfiguration<UserTenant>
{
    public void Configure(EntityTypeBuilder<UserTenant> builder)
    {
        builder.ToTable("UserTenants");

        builder.HasKey(ut => ut.Id);
        builder.Property(ut => ut.Id).ValueGeneratedNever();

        builder.Property(ut => ut.JoinedAt).IsRequired();

        // Unique: a user can only be linked to a tenant once
        builder.HasIndex(ut => new { ut.UserId, ut.TenantId }).IsUnique();

        builder.HasOne(ut => ut.User)
            .WithMany(u => u.UserTenants)
            .HasForeignKey(ut => ut.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(ut => ut.Tenant)
            .WithMany(t => t.UserTenants)
            .HasForeignKey(ut => ut.TenantId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

// ── UserSessions ────────────────────────────────────────────────────────────

public class UserSessionConfiguration : IEntityTypeConfiguration<UserSession>
{
    public void Configure(EntityTypeBuilder<UserSession> builder)
    {
        builder.ToTable("UserSessions");

        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();

        // R2-DEV-03A: keyed digest only (64 hex chars); plaintext refresh tokens are never stored.
        builder.Property(s => s.RefreshTokenHash).HasMaxLength(64).IsRequired();
        // Optimistic concurrency: every rotation/revocation increments it, and EF adds it to
        // each UPDATE's WHERE clause, so a stale write affects 0 rows and fails.
        builder.Property(s => s.RotationVersion).IsRequired().IsConcurrencyToken();
        builder.Property(s => s.AccessTokenJti).HasMaxLength(100).IsRequired();
        builder.Property(s => s.DeviceInfo).HasMaxLength(500);
        builder.Property(s => s.IpAddress).HasMaxLength(100);
        builder.Property(s => s.Location).HasMaxLength(200);
        builder.Property(s => s.RevokedReason).HasMaxLength(200);
        builder.Property(s => s.CreatedAt).IsRequired();
        builder.Property(s => s.RefreshTokenExpiry).IsRequired();
        builder.Property(s => s.ExpiresAt).IsRequired();

        builder.HasIndex(s => s.RefreshTokenHash).IsUnique();
        builder.HasIndex(s => s.AccessTokenJti);
        builder.HasIndex(s => s.UserId);

        builder.HasOne(s => s.User)
            .WithMany(u => u.Sessions)
            .HasForeignKey(s => s.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(s => s.Tenant)
            .WithMany(t => t.Sessions)
            .HasForeignKey(s => s.TenantId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

// ── ConsumedRefreshTokens (R2-DEV-03A) ──────────────────────────────────────

public class ConsumedRefreshTokenConfiguration : IEntityTypeConfiguration<ConsumedRefreshToken>
{
    public void Configure(EntityTypeBuilder<ConsumedRefreshToken> builder)
    {
        builder.ToTable("ConsumedRefreshTokens");

        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();

        builder.Property(c => c.TokenHash).HasMaxLength(64).IsRequired();
        builder.Property(c => c.AccessTokenJti).HasMaxLength(100).IsRequired();
        builder.Property(c => c.RotationVersion).IsRequired();
        builder.Property(c => c.ConsumedAt).IsRequired();
        builder.Property(c => c.RetainUntil).IsRequired();

        // Replay lookup is by digest; the unique index is also a second line of defence: a
        // token can be recorded as consumed only once.
        builder.HasIndex(c => c.TokenHash).IsUnique();
        builder.HasIndex(c => c.SessionId);
        builder.HasIndex(c => c.RetainUntil); // bounded purge scans

        builder.HasOne(c => c.Session)
            .WithMany(s => s.ConsumedTokens)
            .HasForeignKey(c => c.SessionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

// ── UserPasswordHistory ─────────────────────────────────────────────────────

public class UserPasswordHistoryConfiguration : IEntityTypeConfiguration<UserPasswordHistory>
{
    public void Configure(EntityTypeBuilder<UserPasswordHistory> builder)
    {
        builder.ToTable("UserPasswordHistory");

        builder.HasKey(ph => ph.Id);
        builder.Property(ph => ph.Id).ValueGeneratedNever();

        builder.Property(ph => ph.PasswordHash).HasMaxLength(500).IsRequired();
        builder.Property(ph => ph.ChangedAt).IsRequired();

        builder.HasIndex(ph => ph.UserId);

        builder.HasOne(ph => ph.User)
            .WithMany(u => u.PasswordHistory)
            .HasForeignKey(ph => ph.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

// ── Roles ───────────────────────────────────────────────────────────────────

public class AppRoleConfiguration : AuditableEntityConfiguration<AppRole>
{
    protected override void ConfigureEntity(EntityTypeBuilder<AppRole> builder)
    {
        builder.ToTable("Roles");

        builder.Property(r => r.Name).HasMaxLength(100).IsRequired();
        builder.Property(r => r.NormalizedName).HasMaxLength(100).IsRequired();
        builder.Property(r => r.Description).HasMaxLength(500);

        // Same name allowed across tenants, unique within a tenant
        // NULL TenantId = system role (globally unique name)
        builder.HasIndex(r => new { r.NormalizedName, r.TenantId }).IsUnique();

        builder.HasOne(r => r.Tenant)
            .WithMany(t => t.Roles)
            .HasForeignKey(r => r.TenantId)
            .IsRequired(false) // NULL = system role
            .OnDelete(DeleteBehavior.Restrict);
    }
}

// ── Permissions ─────────────────────────────────────────────────────────────

public class PermissionConfiguration : IEntityTypeConfiguration<Permission>
{
    public void Configure(EntityTypeBuilder<Permission> builder)
    {
        builder.ToTable("Permissions");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();

        builder.Property(p => p.Code).HasMaxLength(100).IsRequired();
        builder.Property(p => p.Name).HasMaxLength(200).IsRequired();
        builder.Property(p => p.Category).HasMaxLength(100).IsRequired();
        builder.Property(p => p.Description).HasMaxLength(500);

        builder.HasIndex(p => p.Code).IsUnique();
    }
}

// ── UserRoles ───────────────────────────────────────────────────────────────

public class AppUserRoleConfiguration : IEntityTypeConfiguration<AppUserRole>
{
    public void Configure(EntityTypeBuilder<AppUserRole> builder)
    {
        builder.ToTable("UserRoles");

        builder.HasKey(ur => ur.Id);
        builder.Property(ur => ur.Id).ValueGeneratedNever();

        builder.Property(ur => ur.AssignedAt).IsRequired();

        // A user can have a role in a tenant only once
        builder.HasIndex(ur => new { ur.UserId, ur.RoleId, ur.TenantId }).IsUnique();

        builder.HasOne(ur => ur.User)
            .WithMany(u => u.UserRoles)
            .HasForeignKey(ur => ur.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(ur => ur.Role)
            .WithMany(r => r.UserRoles)
            .HasForeignKey(ur => ur.RoleId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(ur => ur.Tenant)
            .WithMany(t => t.UserRoles)
            .HasForeignKey(ur => ur.TenantId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

// ── RolePermissions ─────────────────────────────────────────────────────────

public class AppRolePermissionConfiguration : IEntityTypeConfiguration<AppRolePermission>
{
    public void Configure(EntityTypeBuilder<AppRolePermission> builder)
    {
        builder.ToTable("RolePermissions");

        builder.HasKey(rp => rp.Id);
        builder.Property(rp => rp.Id).ValueGeneratedNever();

        builder.Property(rp => rp.CreatedAt).IsRequired();

        // A role can have a permission only once
        builder.HasIndex(rp => new { rp.RoleId, rp.PermissionId }).IsUnique();

        builder.HasOne(rp => rp.Role)
            .WithMany(r => r.RolePermissions)
            .HasForeignKey(rp => rp.RoleId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(rp => rp.Permission)
            .WithMany(p => p.RolePermissions)
            .HasForeignKey(rp => rp.PermissionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

// ── Modules ─────────────────────────────────────────────────────────────────

public class ModuleConfiguration : IEntityTypeConfiguration<Module>
{
    public void Configure(EntityTypeBuilder<Module> builder)
    {
        builder.ToTable("Modules");

        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();

        builder.Property(m => m.Code).HasMaxLength(100).IsRequired();
        builder.Property(m => m.Name).HasMaxLength(200).IsRequired();
        builder.Property(m => m.Icon).HasMaxLength(100);

        builder.HasIndex(m => m.Code).IsUnique();
    }
}

// ── Menus ───────────────────────────────────────────────────────────────────

public class MenuConfiguration : IEntityTypeConfiguration<Menu>
{
    public void Configure(EntityTypeBuilder<Menu> builder)
    {
        builder.ToTable("Menus");

        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();

        builder.Property(m => m.Code).HasMaxLength(100).IsRequired();
        builder.Property(m => m.Name).HasMaxLength(200).IsRequired();
        builder.Property(m => m.Route).HasMaxLength(300);
        builder.Property(m => m.ComponentName).HasMaxLength(200);
        builder.Property(m => m.Icon).HasMaxLength(100);
        builder.Property(m => m.FeatureFlag).HasMaxLength(100);
        builder.Property(m => m.CreatedAt).IsRequired();

        builder.HasIndex(m => m.Code).IsUnique();
        builder.HasIndex(m => m.ModuleId);
        builder.HasIndex(m => m.ParentMenuId);

        builder.HasOne(m => m.Module)
            .WithMany(mod => mod.Menus)
            .HasForeignKey(m => m.ModuleId)
            .OnDelete(DeleteBehavior.Cascade);

        // Self-referencing: parent-child menu hierarchy
        builder.HasOne(m => m.ParentMenu)
            .WithMany(m => m.ChildMenus)
            .HasForeignKey(m => m.ParentMenuId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

// ── MenuPermissions ─────────────────────────────────────────────────────────

public class MenuPermissionConfiguration : IEntityTypeConfiguration<MenuPermission>
{
    public void Configure(EntityTypeBuilder<MenuPermission> builder)
    {
        builder.ToTable("MenuPermissions");

        builder.HasKey(mp => mp.Id);
        builder.Property(mp => mp.Id).ValueGeneratedNever();

        builder.HasIndex(mp => new { mp.MenuId, mp.PermissionId }).IsUnique();

        builder.HasOne(mp => mp.Menu)
            .WithMany(m => m.MenuPermissions)
            .HasForeignKey(mp => mp.MenuId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(mp => mp.Permission)
            .WithMany(p => p.MenuPermissions)
            .HasForeignKey(mp => mp.PermissionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

// ── FeatureFlags ────────────────────────────────────────────────────────────

public class FeatureFlagConfiguration : IEntityTypeConfiguration<FeatureFlag>
{
    public void Configure(EntityTypeBuilder<FeatureFlag> builder)
    {
        builder.ToTable("FeatureFlags");

        builder.HasKey(f => f.Id);
        builder.Property(f => f.Id).ValueGeneratedNever();

        builder.Property(f => f.Code).HasMaxLength(100).IsRequired();
        builder.Property(f => f.Name).HasMaxLength(200).IsRequired();
        builder.Property(f => f.Description).HasMaxLength(500);
        builder.Property(f => f.CreatedAt).IsRequired();

        builder.HasIndex(f => f.Code).IsUnique();
    }
}

// ── TenantFeatureFlags ──────────────────────────────────────────────────────

public class TenantFeatureFlagConfiguration : IEntityTypeConfiguration<TenantFeatureFlag>
{
    public void Configure(EntityTypeBuilder<TenantFeatureFlag> builder)
    {
        builder.ToTable("TenantFeatureFlags");

        builder.HasKey(tf => tf.Id);
        builder.Property(tf => tf.Id).ValueGeneratedNever();

        builder.Property(tf => tf.EnabledAt).IsRequired();

        builder.HasIndex(tf => new { tf.TenantId, tf.FeatureFlagId }).IsUnique();

        builder.HasOne(tf => tf.Tenant)
            .WithMany(t => t.FeatureFlags)
            .HasForeignKey(tf => tf.TenantId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(tf => tf.FeatureFlag)
            .WithMany(f => f.TenantOverrides)
            .HasForeignKey(tf => tf.FeatureFlagId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

// ── IdentityAuditLogs ───────────────────────────────────────────────────────

public class IdentityAuditLogConfiguration : IEntityTypeConfiguration<IdentityAuditLog>
{
    public void Configure(EntityTypeBuilder<IdentityAuditLog> builder)
    {
        builder.ToTable("IdentityAuditLogs");

        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();

        builder.Property(a => a.Action).HasMaxLength(50).IsRequired();
        builder.Property(a => a.EntityType).HasMaxLength(200);
        builder.Property(a => a.EntityId).HasMaxLength(100);
        builder.Property(a => a.IpAddress).HasMaxLength(100);
        builder.Property(a => a.UserAgent).HasMaxLength(500);
        builder.Property(a => a.CreatedAt).IsRequired();

        builder.HasIndex(a => a.UserId);
        builder.HasIndex(a => a.Action);
        builder.HasIndex(a => a.CreatedAt);
        builder.HasIndex(a => new { a.EntityType, a.EntityId });
    }
}
