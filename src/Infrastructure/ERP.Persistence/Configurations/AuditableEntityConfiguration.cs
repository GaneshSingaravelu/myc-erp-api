using ERP.SharedKernel.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ERP.Persistence.Configurations;

/// <summary>
/// ENTITY CONFIGURATION — Defines how AuditableEntity properties map to database columns.
///
/// WHAT IS IEntityTypeConfiguration? (Interview Question!)
/// =========================================================
/// Instead of using Data Annotations ([Table], [Column], [Required]) on your entities,
/// we use Fluent API in separate configuration classes.
///
/// WHY FLUENT API OVER ANNOTATIONS?
/// ==================================
/// 1. Keeps domain entities CLEAN — no database attributes polluting business classes
/// 2. More powerful — can do things annotations can't (owned types, complex indexes)
/// 3. One place for all DB mapping — easy to review and maintain
/// 4. Follows Clean Architecture — domain layer has ZERO database knowledge
///
/// HOW IT'S APPLIED:
/// ==================
/// In AppDbContext.OnModelCreating():
///   modelBuilder.ApplyConfigurationsFromAssembly(...)
/// This automatically discovers ALL IEntityTypeConfiguration classes and applies them.
/// </summary>
public abstract class AuditableEntityConfiguration<T> : IEntityTypeConfiguration<T>
    where T : AuditableEntity
{
    public void Configure(EntityTypeBuilder<T> builder)
    {
        // Base entity properties
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever(); // We generate GUIDs in code, not DB

        // Audit columns
        builder.Property(e => e.CreatedAt).IsRequired();
        builder.Property(e => e.CreatedBy).HasMaxLength(100);
        builder.Property(e => e.LastModifiedBy).HasMaxLength(100);
        builder.Property(e => e.DeletedBy).HasMaxLength(100);

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
