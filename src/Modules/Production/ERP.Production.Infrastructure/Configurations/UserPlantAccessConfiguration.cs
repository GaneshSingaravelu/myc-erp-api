using ERP.Production.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ERP.Production.Infrastructure.Configurations;

/// <summary>
/// EF Core configuration for UserPlantAccess entity (Tenant DB).
/// UserId references Master DB Users — no FK constraint (cross-database).
/// </summary>
public class UserPlantAccessConfiguration : IEntityTypeConfiguration<UserPlantAccess>
{
    public void Configure(EntityTypeBuilder<UserPlantAccess> builder)
    {
        builder.ToTable("UserPlantAccess");

        builder.HasKey(upa => upa.Id);
        builder.Property(upa => upa.Id).ValueGeneratedNever();

        builder.Property(upa => upa.UserId).IsRequired();
        builder.Property(upa => upa.AccessLevel).HasMaxLength(50).IsRequired();
        builder.Property(upa => upa.GrantedAt).IsRequired();

        // Unique: user can have access to a plant only once
        builder.HasIndex(upa => new { upa.UserId, upa.PlantId }).IsUnique();
        builder.HasIndex(upa => upa.UserId);

        // FK to Plants (same database)
        builder.HasOne(upa => upa.Plant)
            .WithMany(p => p.UserPlantAccess)
            .HasForeignKey(upa => upa.PlantId)
            .OnDelete(DeleteBehavior.Cascade);

        // NOTE: No FK for UserId — cross-database reference to Master DB Users
        // Validated in application code
    }
}
