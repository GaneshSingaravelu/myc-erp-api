using Mediator;

namespace ERP.SharedKernel.DomainEvents;

/// <summary>
/// DOMAIN EVENT — Something that happened in the domain that other parts care about.
///
/// WHAT IS A DOMAIN EVENT? (Interview Question!)
/// ===============================================
/// When something important happens in your business logic, you raise an event.
/// Other parts of the system can REACT to that event without the original code knowing about them.
///
/// REAL-WORLD EXAMPLE FROM YOUR WORKFLOW:
/// =======================================
/// When QA approves a job card:
///   1. JobCard raises "JobCardApprovedEvent"
///   2. Listener 1: Creates the next department's job card
///   3. Listener 2: Sends SignalR notification to QA dashboard
///   4. Listener 3: Updates work order fulfilled quantity
///   5. Listener 4: Logs the approval for audit trail
///
/// The JobCard class doesn't know about any of these listeners!
/// This is the OPEN/CLOSED PRINCIPLE — open for extension, closed for modification.
///
/// WHY INotification (MediatR)?
/// =============================
/// MediatR provides the "publish/subscribe" mechanism.
/// We publish an event, and all registered handlers are called automatically.
///
/// DESIGN PATTERN: Observer Pattern (via MediatR)
/// </summary>
public interface IDomainEvent : INotification
{
    /// <summary>When this event occurred (UTC)</summary>
    DateTime OccurredAt { get; }
}
