using ERP.SharedKernel.Entities;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ERP.Persistence.Interceptors;

/// <summary>
/// DOMAIN EVENT DISPATCHER — Publishes domain events AFTER SaveChanges succeeds.
///
/// WHY AFTER SAVE?
/// ================
/// Domain events should only be published after the data is committed.
/// If we published before save and the save failed, event handlers would
/// have acted on data that was never persisted — causing inconsistency.
///
/// FLOW:
/// 1. Handler modifies entities and adds domain events
/// 2. SaveChangesAsync() is called
/// 3. Data is saved to database
/// 4. THIS interceptor collects all domain events from saved entities
/// 5. Publishes each event via MediatR
/// 6. Event handlers react (send notifications, update related data, etc.)
/// </summary>
public sealed class DomainEventDispatcherInterceptor : SaveChangesInterceptor
{
    private readonly IPublisher _publisher;

    public DomainEventDispatcherInterceptor(IPublisher publisher)
    {
        _publisher = publisher;
    }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        var context = eventData.Context;
        if (context is null) return result;

        // Collect all domain events from entities that were just saved
        var domainEvents = context.ChangeTracker
            .Entries<BaseEntity>()
            .SelectMany(entry =>
            {
                var events = entry.Entity.DomainEvents.ToList();
                entry.Entity.ClearDomainEvents();
                return events;
            })
            .ToList();

        // Publish each event — handlers are called via MediatR
        foreach (var domainEvent in domainEvents)
        {
            await _publisher.Publish(domainEvent, cancellationToken);
        }

        return result;
    }
}
