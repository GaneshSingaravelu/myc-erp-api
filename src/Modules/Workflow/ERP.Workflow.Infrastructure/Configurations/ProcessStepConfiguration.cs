using ERP.Persistence.Configurations;
using ERP.Workflow.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ERP.Workflow.Infrastructure.Configurations;

/// <summary>
/// EF Core configuration for ProcessStep entity.
/// </summary>
public class ProcessStepConfiguration : AuditableEntityConfiguration<ProcessStep>
{
    protected override void ConfigureEntity(EntityTypeBuilder<ProcessStep> builder)
    {
        builder.ToTable("ProcessSteps");

        builder.Property(s => s.DepartmentName)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(s => s.Order)
            .IsRequired();

        builder.Property(s => s.StepType)
            .IsRequired()
            .HasConversion<string>()   // Store enum as string in DB for readability
            .HasMaxLength(20);

        builder.Property(s => s.QaRequired)
            .IsRequired()
            .HasDefaultValue(true);

        builder.Property(s => s.Instructions)
            .HasMaxLength(2000);

        builder.Property(s => s.EstimatedDurationMinutes);

        // Self-referencing FK: step → next step
        builder.HasOne(s => s.NextStep)
            .WithMany()
            .HasForeignKey(s => s.NextStepId)
            .OnDelete(DeleteBehavior.Restrict)  // Don't cascade — deleting a step shouldn't delete the next
            .IsRequired(false);

        // Index for querying steps by template
        builder.HasIndex(s => new { s.TemplateId, s.Order })
            .HasDatabaseName("IX_ProcessSteps_Template_Order");
    }
}
