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

        builder.Property(wo => wo.Status)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.Property(wo => wo.DueDate);

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
