using ERP.Persistence.Configurations;
using ERP.Workflow.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ERP.Workflow.Infrastructure.Configurations;

/// <summary>
/// EF Core configuration for ProcessTemplate entity.
/// Defines how the entity maps to the database table.
///
/// INHERITS FROM AuditableEntityConfiguration which automatically configures:
/// - Primary key (Id as GUID)
/// - Audit fields (CreatedAt, CreatedBy, LastModifiedAt, etc.)
/// - Soft delete index
/// - DomainEvents ignored
/// </summary>
public class ProcessTemplateConfiguration : AuditableEntityConfiguration<ProcessTemplate>
{
    protected override void ConfigureEntity(EntityTypeBuilder<ProcessTemplate> builder)
    {
        builder.ToTable("ProcessTemplates");

        builder.Property(t => t.Name)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(t => t.Description)
            .HasMaxLength(1000);

        builder.Property(t => t.Version)
            .IsRequired();

        builder.Property(t => t.IsActive)
            .IsRequired()
            .HasDefaultValue(true);

        // Index for fast lookup: active templates by name
        builder.HasIndex(t => new { t.Name, t.Version })
            .IsUnique()
            .HasDatabaseName("IX_ProcessTemplates_Name_Version");

        // One template has many steps (cascade delete — when template is deleted, steps go too)
        builder.HasMany(t => t.Steps)
            .WithOne(s => s.Template)
            .HasForeignKey(s => s.TemplateId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
