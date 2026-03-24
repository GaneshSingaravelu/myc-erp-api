namespace ERP.SharedKernel.DomainEvents;

/// <summary>
/// Base implementation of IDomainEvent with automatic timestamp.
/// All concrete events inherit from this.
///
/// EXAMPLE USAGE:
/// ==============
/// public record JobCardCompletedEvent(Guid JobCardId, int ApprovedQty) : DomainEvent;
/// public record WorkOrderClosedEvent(Guid WorkOrderId) : DomainEvent;
/// </summary>
public abstract record DomainEvent : IDomainEvent
{
    public DateTime OccurredAt { get; } = DateTime.UtcNow;
}
