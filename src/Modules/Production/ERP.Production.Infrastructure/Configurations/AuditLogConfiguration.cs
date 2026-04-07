using ERP.Production.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ERP.Production.Infrastructure.Configurations;

/// <summary>
/// EF Core configuration for AuditLog entity (Tenant DB — business audit trail).
/// No FK for UserId (cross-database reference to Master DB).
/// </summary>
public class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.ToTable("AuditLogs");

        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();

        builder.Property(a => a.Action).HasMaxLength(50).IsRequired();
        builder.Property(a => a.EntityType).HasMaxLength(200).IsRequired();
        builder.Property(a => a.EntityId).HasMaxLength(100);
        builder.Property(a => a.IpAddress).HasMaxLength(100);
        builder.Property(a => a.UserAgent).HasMaxLength(500);
        builder.Property(a => a.CreatedAt).IsRequired();

        builder.HasIndex(a => new { a.EntityType, a.EntityId });
        builder.HasIndex(a => a.UserId);
        builder.HasIndex(a => a.CreatedAt);
    }
}
