using ERP.Production.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ERP.Production.Infrastructure.Configurations;

/// <summary>
/// EF Core configuration for Plant entity (Tenant DB).
/// No TenantId column — the table is already in a tenant-specific database.
/// </summary>
public class PlantConfiguration : IEntityTypeConfiguration<Plant>
{
    public void Configure(EntityTypeBuilder<Plant> builder)
    {
        builder.ToTable("Plants");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();

        builder.Property(p => p.Code).HasMaxLength(50).IsRequired();
        builder.Property(p => p.Name).HasMaxLength(200).IsRequired();
        builder.Property(p => p.Location).HasMaxLength(200);
        builder.Property(p => p.CreatedAt).IsRequired();

        builder.HasIndex(p => p.Code).IsUnique();
    }
}
