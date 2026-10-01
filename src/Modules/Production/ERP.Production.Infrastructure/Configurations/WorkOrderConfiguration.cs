using ERP.Persistence.Configurations;
using ERP.Production.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ERP.Production.Infrastructure.Configurations;

/// <summary>
/// EF Core configuration for WorkOrder entity.
/// Inherits AuditableEntityConfiguration which configures PK, audit fields, soft delete.
/// </summary>
public class WorkOrderConfiguration : AuditableEntityConfiguration<WorkOrder>
{
    protected override void ConfigureEntity(EntityTypeBuilder<WorkOrder> builder)
    {
        builder.ToTable("WorkOrders");

        builder.Property(wo => wo.OrderNumber)
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(wo => wo.ProductName)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(wo => wo.Quantity)
            .IsRequired();

        builder.Property(wo => wo.ProcessTemplateId)
            .IsRequired();

        builder.Property(wo => wo.PlantId)
            .IsRequired();

        builder.HasIndex(wo => wo.PlantId)
            .HasDatabaseName("IX_WorkOrders_PlantId");

        builder.Property(wo => wo.Status)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(20);

        // DueDate is a business calendar date (DateOnly?), not an instant. "date" is stated
        // explicitly rather than left to EF's default for DateOnly: it makes the intent
        // reviewable at the mapping site, and a future convention change cannot silently
        // re-widen the column back to a timestamp type. "date" is valid on SQL Server and
        // PostgreSQL alike, so no provider-specific mapping is needed. Nullable is preserved
        // by the property's own type — this call must not make it required.
        builder.Property(wo => wo.DueDate)
            .HasColumnType("date");

        builder.Property(wo => wo.Notes)
            .HasMaxLength(2000);

        builder.Property(wo => wo.ReleasedAt);
        builder.Property(wo => wo.CompletedAt);

        // Unique index on OrderNumber
        builder.HasIndex(wo => wo.OrderNumber)
            .IsUnique()
            .HasDatabaseName("IX_WorkOrders_OrderNumber");

        // Index for filtering by status
        builder.HasIndex(wo => wo.Status)
            .HasDatabaseName("IX_WorkOrders_Status");

        // One work order has many job cards
        builder.HasMany(wo => wo.JobCards)
            .WithOne(jc => jc.WorkOrder)
            .HasForeignKey(jc => jc.WorkOrderId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
