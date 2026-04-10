using ERP.SharedKernel.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ERP.Persistence.Configurations;

/// <summary>
/// ENTITY CONFIGURATION — Base configuration for all AuditableEntity types.
///
/// Configures: PK, audit columns (CreatedBy/ModifiedBy/DeletedBy as GUID),
/// IsActive, soft delete index, and ignores the DomainEvents collection.
///
/// Each entity overrides ConfigureEntity() to add entity-specific mappings.
/// </summary>
public abstract class AuditableEntityConfiguration<T> : IEntityTypeConfiguration<T>
    where T : AuditableEntity
{
    public void Configure(EntityTypeBuilder<T> builder)
    {
        // Primary key — app-generated GUID
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();

        // Audit columns (CreatedBy/ModifiedBy/DeletedBy are now GUIDs referencing Users)
        builder.Property(e => e.CreatedAt).IsRequired();

        // Soft delete index — queries filtering by IsDeleted will be fast
        builder.HasIndex(e => e.IsDeleted);

        // Ignore the DomainEvents collection — it's not a database column
        builder.Ignore(e => e.DomainEvents);

        // Call the derived class to configure entity-specific properties
        ConfigureEntity(builder);
    }

    /// <summary>Override this to add entity-specific configuration</summary>
    protected abstract void ConfigureEntity(EntityTypeBuilder<T> builder);
}
