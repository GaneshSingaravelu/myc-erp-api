using ERP.Application.Abstractions.Identity;
using ERP.SharedKernel.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ERP.Persistence.Interceptors;

/// <summary>
/// AUDITABLE ENTITY INTERCEPTOR — Automatically fills CreatedAt/By, ModifiedAt/By fields.
///
/// WHAT IS AN INTERCEPTOR?
/// ========================
/// An EF Core Interceptor hooks into the SaveChanges pipeline.
/// BEFORE data is saved to the database, this interceptor:
/// - Sets CreatedAt/CreatedBy for NEW entities
/// - Sets LastModifiedAt/LastModifiedBy for MODIFIED entities
/// - Sets DeletedAt/DeletedBy for DELETED entities (soft delete)
///
/// This means you NEVER need to manually set audit fields in your code.
/// They're filled automatically every time you save.
///
/// DESIGN PATTERN: Interceptor / Aspect-Oriented Programming
/// </summary>
public sealed class AuditableEntityInterceptor : SaveChangesInterceptor
{
    private readonly ICurrentUserService _currentUser;

    public AuditableEntityInterceptor(ICurrentUserService currentUser)
    {
        _currentUser = currentUser;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        var context = eventData.Context;
        if (context is null) return base.SavingChangesAsync(eventData, result, cancellationToken);

        var now = DateTime.UtcNow;
        var userId = _currentUser.UserId ?? "system";

        foreach (var entry in context.ChangeTracker.Entries<AuditableEntity>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.Entity.CreatedAt = now;
                    entry.Entity.CreatedBy = userId;
                    break;

                case EntityState.Modified:
                    entry.Entity.LastModifiedAt = now;
                    entry.Entity.LastModifiedBy = userId;
                    break;

                case EntityState.Deleted:
                    // Convert hard delete to soft delete
                    entry.State = EntityState.Modified;
                    entry.Entity.IsDeleted = true;
                    entry.Entity.DeletedAt = now;
                    entry.Entity.DeletedBy = userId;
                    break;
            }
        }

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }
}
