namespace ERP.Production.Domain.Enums;

/// <summary>
/// Status of a Work Order through its lifecycle.
///
/// WORK ORDER LIFECYCLE:
/// =====================
/// Draft → Released → InProgress → Completed
///                  ↘ Cancelled
///
/// Draft:      Created but not yet approved for production
/// Released:   Approved and ready to generate job cards
/// InProgress: At least one job card has started processing
/// Completed:  All job cards finished, finished goods recorded
/// Cancelled:  Order cancelled before or during production
/// </summary>
public enum WorkOrderStatus
{
    Draft = 0,
    Released = 1,
    InProgress = 2,
    Completed = 3,
    Cancelled = 4
}
