using ERP.Persistence.Configurations;
using ERP.Production.Domain.Entities;
using ERP.Production.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ERP.Production.Infrastructure.Configurations;

/// <summary>
/// EF Core configuration for JobCard entity.
/// Includes value object conversion for JobCardNumber.
/// </summary>
public class JobCardConfiguration : AuditableEntityConfiguration<JobCard>
{
    protected override void ConfigureEntity(EntityTypeBuilder<JobCard> builder)
    {
        builder.ToTable("JobCards");

        // Value object conversion: store as string in DB, convert to/from JobCardNumber
        builder.Property(jc => jc.JobCardNumber)
            .HasConversion(
                number => number.Value,                     // To DB: JobCardNumber → string
                value => JobCardNumber.FromString(value))   // From DB: string → JobCardNumber
            .HasMaxLength(17)
            .IsRequired()
            .HasColumnName("JobCardNumber");

        builder.Property(jc => jc.WorkOrderId)
            .IsRequired();

        builder.Property(jc => jc.ProcessStepId)
            .IsRequired();

        builder.Property(jc => jc.DepartmentName)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(jc => jc.Quantity)
            .IsRequired();

        builder.Property(jc => jc.Status)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.Property(jc => jc.Instructions)
            .HasMaxLength(2000);

        builder.Property(jc => jc.Notes)
            .HasMaxLength(2000);

        builder.Property(jc => jc.StartedAt);
        builder.Property(jc => jc.CompletedAt);
        builder.Property(jc => jc.ParentJobCardId);

        // Index on JobCardNumber for barcode lookups
        builder.HasIndex(jc => jc.JobCardNumber)
            .IsUnique()
            .HasDatabaseName("IX_JobCards_JobCardNumber");

        // Index for filtering by work order
        builder.HasIndex(jc => jc.WorkOrderId)
            .HasDatabaseName("IX_JobCards_WorkOrderId");

        // Index for filtering by status (shop floor dashboards)
        builder.HasIndex(jc => jc.Status)
            .HasDatabaseName("IX_JobCards_Status");

        // Index for filtering by department (department queues)
        builder.HasIndex(jc => jc.DepartmentName)
            .HasDatabaseName("IX_JobCards_DepartmentName");

        // Self-referencing FK for parent job card (splits/reworks)
        builder.HasOne(jc => jc.ParentJobCard)
            .WithMany()
            .HasForeignKey(jc => jc.ParentJobCardId)
            .OnDelete(DeleteBehavior.Restrict)
            .IsRequired(false);
    }
}
