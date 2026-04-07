using ERP.Production.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ERP.Production.Infrastructure.Configurations;

/// <summary>
/// EF Core configuration for UserPreference entity (Tenant DB).
/// Key-value store — no FK for UserId (cross-database reference).
/// </summary>
public class UserPreferenceConfiguration : IEntityTypeConfiguration<UserPreference>
{
    public void Configure(EntityTypeBuilder<UserPreference> builder)
    {
        builder.ToTable("UserPreferences");

        builder.HasKey(up => up.Id);
        builder.Property(up => up.Id).ValueGeneratedNever();

        builder.Property(up => up.UserId).IsRequired();
        builder.Property(up => up.Key).HasMaxLength(200).IsRequired();
        builder.Property(up => up.Value).IsRequired();

        builder.HasIndex(up => new { up.UserId, up.Key }).IsUnique();
    }
}
