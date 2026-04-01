namespace ERP.Production.Domain.Enums;

/// <summary>
/// Status of a Job Card as it moves through production steps.
///
/// JOB CARD LIFECYCLE:
/// ====================
/// Pending → InProgress → AwaitingQa → QaPassed → Completed
///                       ↘ QaFailed → Rework (creates new job card with R## incremented)
///
/// Pending:     Created but not yet started at a department
/// InProgress:  Currently being worked on at a department
/// AwaitingQa:  Step done, waiting for QA inspection (if QaRequired)
/// QaPassed:    QA approved, ready to move to next step
/// QaFailed:    QA rejected, needs rework
/// Completed:   Final step done, job card finished
/// OnHold:      Temporarily paused (material shortage, machine breakdown, etc.)
/// Cancelled:   Cancelled (parent work order cancelled)
/// </summary>
public enum JobCardStatus
{
    Pending = 0,
    InProgress = 1,
    AwaitingQa = 2,
    QaPassed = 3,
    QaFailed = 4,
    Completed = 5,
    OnHold = 6,
    Cancelled = 7
}
