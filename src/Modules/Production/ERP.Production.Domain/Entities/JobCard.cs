using ERP.Production.Domain.Enums;
using ERP.Production.Domain.ValueObjects;
using ERP.SharedKernel.Entities;
using ERP.SharedKernel.Guards;

namespace ERP.Production.Domain.Entities;

/// <summary>
/// JOB CARD — A physical production ticket that travels with material on the shop floor.
///
/// WHAT IS A JOB CARD?
/// ====================
/// When a work order is released, a job card is generated for each step in the
/// process template. The job card tells a department: "Process this material
/// for this quantity, following these instructions."
///
/// In a physical factory, this is often a printed card (with barcode) that
/// travels with the material from one department to another.
///
/// NUMBERING:
/// ==========
/// Format: YYMMDDN###S###R## (see JobCardNumber value object)
/// - N### links back to the work order's running number
/// - S### tracks splits (parallel processing)
/// - R## tracks rework cycles
///
/// LIFECYCLE:
/// ==========
/// 1. Generated when work order is released (Pending)
/// 2. Department starts work (InProgress)
/// 3. Department completes work → if QaRequired → AwaitingQa
/// 4. QA inspects → QaPassed or QaFailed
/// 5. If QaFailed → Rework job card created (R## incremented)
/// 6. If final step + QaPassed → Completed
///
/// SPLITS:
/// =======
/// When a process step is Parallel, the job card splits into multiple cards,
/// one for each parallel department. Each split gets S### incremented.
/// </summary>
public class JobCard : AuditableEntity
{
    private JobCard() { } // EF Core

    public JobCard(
        Guid workOrderId,
        JobCardNumber jobCardNumber,
        Guid processStepId,
        string departmentName,
        int quantity,
        string? instructions = null)
    {
        Guard.AgainstNullOrEmpty(departmentName, nameof(departmentName));
        Guard.AgainstZeroOrNegative(quantity, nameof(quantity));

        WorkOrderId = workOrderId;
        JobCardNumber = jobCardNumber ?? throw new ArgumentNullException(nameof(jobCardNumber));
        ProcessStepId = processStepId;
        DepartmentName = departmentName;
        Quantity = quantity;
        Instructions = instructions;
        Status = JobCardStatus.Pending;
    }

    /// <summary>FK to the parent work order</summary>
    public Guid WorkOrderId { get; private set; }

    /// <summary>Navigation to parent work order</summary>
    public WorkOrder WorkOrder { get; private set; } = null!;

    /// <summary>Structured job card number (value object)</summary>
    public JobCardNumber JobCardNumber { get; private set; } = null!;

    /// <summary>FK to the process step this card is executing</summary>
    public Guid ProcessStepId { get; private set; }

    /// <summary>Department responsible (denormalized from ProcessStep for quick display)</summary>
    public string DepartmentName { get; private set; } = null!;

    /// <summary>Quantity to process at this step</summary>
    public int Quantity { get; private set; }

    /// <summary>Current status</summary>
    public JobCardStatus Status { get; private set; }

    /// <summary>Work instructions for the operator</summary>
    public string? Instructions { get; private set; }

    /// <summary>When the department started working on this card</summary>
    public DateTime? StartedAt { get; private set; }

    /// <summary>When the department finished working on this card</summary>
    public DateTime? CompletedAt { get; private set; }

    /// <summary>Optional notes from the operator or QA inspector</summary>
    public string? Notes { get; private set; }

    /// <summary>ID of the parent job card if this is a split or rework</summary>
    public Guid? ParentJobCardId { get; private set; }

    /// <summary>Navigation to parent job card (for splits and reworks)</summary>
    public JobCard? ParentJobCard { get; private set; }

    // ── DOMAIN METHODS ──────────────────────────────────────────

    /// <summary>Start working on this job card.</summary>
    public void Start()
    {
        if (Status != JobCardStatus.Pending)
            throw new InvalidOperationException("Only Pending job cards can be started.");

        Status = JobCardStatus.InProgress;
        StartedAt = DateTime.UtcNow;
    }

    /// <summary>Mark step work as complete. Moves to AwaitingQa if QA is required, else Completed.</summary>
    public void CompleteStep(bool qaRequired)
    {
        if (Status != JobCardStatus.InProgress)
            throw new InvalidOperationException("Only InProgress job cards can complete a step.");

        Status = qaRequired ? JobCardStatus.AwaitingQa : JobCardStatus.Completed;
        CompletedAt = DateTime.UtcNow;
    }

    /// <summary>QA passes inspection.</summary>
    public void PassQa(string? notes = null)
    {
        if (Status != JobCardStatus.AwaitingQa)
            throw new InvalidOperationException("Only job cards awaiting QA can be passed.");

        Status = JobCardStatus.QaPassed;
        Notes = notes;
    }

    /// <summary>QA fails inspection. A rework job card should be created.</summary>
    public void FailQa(string? notes = null)
    {
        if (Status != JobCardStatus.AwaitingQa)
            throw new InvalidOperationException("Only job cards awaiting QA can be failed.");

        Status = JobCardStatus.QaFailed;
        Notes = notes;
    }

    /// <summary>Mark the job card as fully completed (final step or after QA pass on last step).</summary>
    public void Complete()
    {
        if (Status is not (JobCardStatus.QaPassed or JobCardStatus.Completed))
            throw new InvalidOperationException("Only QaPassed or already Completed job cards can be marked as completed.");

        Status = JobCardStatus.Completed;
        CompletedAt ??= DateTime.UtcNow;
    }

    /// <summary>Put the job card on hold.</summary>
    public void PutOnHold(string? notes = null)
    {
        if (Status is JobCardStatus.Completed or JobCardStatus.Cancelled)
            throw new InvalidOperationException("Cannot put a completed or cancelled job card on hold.");

        Status = JobCardStatus.OnHold;
        Notes = notes;
    }

    /// <summary>Resume a job card that was on hold (returns to Pending).</summary>
    public void Resume()
    {
        if (Status != JobCardStatus.OnHold)
            throw new InvalidOperationException("Only OnHold job cards can be resumed.");

        Status = JobCardStatus.Pending;
    }

    /// <summary>Cancel this job card.</summary>
    public void Cancel()
    {
        if (Status is JobCardStatus.Completed or JobCardStatus.Cancelled)
            throw new InvalidOperationException("Cannot cancel a completed or already cancelled job card.");

        Status = JobCardStatus.Cancelled;
    }

    /// <summary>Set the parent job card ID (for splits and reworks).</summary>
    public void SetParent(Guid parentJobCardId)
    {
        ParentJobCardId = parentJobCardId;
    }
}
