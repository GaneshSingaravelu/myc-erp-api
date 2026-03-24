using ERP.Identity.Entities;
using ERP.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ERP.Identity.Configuration;

/// <summary>
/// EF CORE ENTITY CONFIGURATIONS for Identity entities.
///
/// These define HOW C# classes map to database tables:
/// - Table names
/// - Column sizes and constraints
/// - Indexes (for query performance)
/// - Relationships (foreign keys)
///
/// These configurations are automatically discovered by AppDbContext.OnModelCreating()
/// because we use: modelBuilder.ApplyConfigurationsFromAssembly(...)
///
/// NOTE: These configurations are in the Identity project, not in Persistence.
/// AppDbContext discovers them via assembly scanning. We'll register the Identity
/// assembly in Program.cs.
/// </summary>

public class AppUserConfiguration : AuditableEntityConfiguration<AppUser>
{
    protected override void ConfigureEntity(EntityTypeBuilder<AppUser> builder)
    {
        builder.ToTable("Users");

        builder.Property(u => u.UserName).HasMaxLength(50).IsRequired();
        builder.Property(u => u.Email).HasMaxLength(200).IsRequired();
        builder.Property(u => u.PasswordHash).HasMaxLength(500).IsRequired();
        builder.Property(u => u.FirstName).HasMaxLength(100).IsRequired();
        builder.Property(u => u.LastName).HasMaxLength(100).IsRequired();
        builder.Property(u => u.RefreshToken).HasMaxLength(500);

        // Unique indexes: no two users can have the same username or email
        builder.HasIndex(u => u.UserName).IsUnique();
        builder.HasIndex(u => u.Email).IsUnique();
    }
}

public class AppRoleConfiguration : AuditableEntityConfiguration<AppRole>
{
    protected override void ConfigureEntity(EntityTypeBuilder<AppRole> builder)
    {
        builder.ToTable("Roles");

        builder.Property(r => r.Name).HasMaxLength(100).IsRequired();
        builder.Property(r => r.Description).HasMaxLength(500);

        builder.HasIndex(r => r.Name).IsUnique();
    }
}

public class PermissionConfiguration : IEntityTypeConfiguration<Permission>
{
    public void Configure(EntityTypeBuilder<Permission> builder)
    {
        builder.ToTable("Permissions");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.Property(p => p.Code).HasMaxLength(100).IsRequired();
        builder.Property(p => p.Name).HasMaxLength(200).IsRequired();
        builder.Property(p => p.Module).HasMaxLength(100).IsRequired();

        builder.HasIndex(p => p.Code).IsUnique();
    }
}

/// <summary>
/// JUNCTION TABLE CONFIGURATION — Composite primary key (UserId + RoleId).
/// A composite key means BOTH columns together form the primary key.
/// This prevents duplicate assignments (same user + same role can only exist once).
/// </summary>
public class AppUserRoleConfiguration : IEntityTypeConfiguration<AppUserRole>
{
    public void Configure(EntityTypeBuilder<AppUserRole> builder)
    {
        builder.ToTable("UserRoles");

        // Composite primary key: UserId + RoleId
        builder.HasKey(ur => new { ur.UserId, ur.RoleId });

        // Relationships
        builder.HasOne(ur => ur.User)
            .WithMany(u => u.UserRoles)
            .HasForeignKey(ur => ur.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(ur => ur.Role)
            .WithMany(r => r.UserRoles)
            .HasForeignKey(ur => ur.RoleId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(ur => ur.AssignedBy).HasMaxLength(100);
    }
}

public class AppRolePermissionConfiguration : IEntityTypeConfiguration<AppRolePermission>
{
    public void Configure(EntityTypeBuilder<AppRolePermission> builder)
    {
        builder.ToTable("RolePermissions");

        builder.HasKey(rp => new { rp.RoleId, rp.PermissionId });

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
