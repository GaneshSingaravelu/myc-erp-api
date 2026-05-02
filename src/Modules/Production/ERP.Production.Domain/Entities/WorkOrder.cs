using ERP.Production.Domain.Enums;
using ERP.SharedKernel.Entities;
using ERP.SharedKernel.Guards;

namespace ERP.Production.Domain.Entities;

/// <summary>
/// WORK ORDER — A production order to manufacture a specific quantity of a product.
///
/// WHAT IS A WORK ORDER?
/// ======================
/// A work order tells the factory: "Make 500 units of Steel Rod Type A using
/// the Steel Rod Production process template."
///
/// It links a ProcessTemplate (from the Workflow module) to actual production.
/// Once released, job cards are generated for each step in the process template.
///
/// RELATIONSHIPS:
/// ==============
/// WorkOrder → ProcessTemplate (via TemplateId — which workflow to follow)
/// WorkOrder → JobCard[] (one-to-many — the actual work items on the shop floor)
///
/// LIFECYCLE:
/// ==========
/// 1. Planner creates WorkOrder (Draft)
/// 2. Supervisor releases it (Released) → job cards generated
/// 3. Shop floor starts working (InProgress)
/// 4. All job cards complete (Completed) → finished goods recorded
/// </summary>
public class WorkOrder : AuditableEntity
{
    private readonly List<JobCard> _jobCards = [];

    private WorkOrder() { } // EF Core

    public WorkOrder(
        string orderNumber,
        string productName,
        int quantity,
        Guid processTemplateId,
        Guid plantId,
        DateTime? dueDate = null,
        string? notes = null)
    {
        Guard.AgainstNullOrEmpty(orderNumber, nameof(orderNumber));
        Guard.AgainstNullOrEmpty(productName, nameof(productName));
        Guard.AgainstZeroOrNegative(quantity, nameof(quantity));

        OrderNumber = orderNumber;
        ProductName = productName;
        Quantity = quantity;
        ProcessTemplateId = processTemplateId;
        PlantId = plantId;
        DueDate = dueDate;
        Notes = notes;
        Status = WorkOrderStatus.Draft;
    }

    /// <summary>Unique order number (e.g., "WO-2026-0001")</summary>
    public string OrderNumber { get; private set; } = null!;

    /// <summary>Product being manufactured (e.g., "Steel Rod Type A")</summary>
    public string ProductName { get; private set; } = null!;

    /// <summary>Quantity to produce</summary>
    public int Quantity { get; private set; }

    /// <summary>FK to the process template (workflow) this order follows</summary>
    public Guid ProcessTemplateId { get; private set; }

    /// <summary>FK to the Plant where this work order will be executed</summary>
    public Guid PlantId { get; private set; }

    /// <summary>Current status in the lifecycle</summary>
    public WorkOrderStatus Status { get; private set; }

    /// <summary>Target completion date</summary>
    public DateTime? DueDate { get; private set; }

    /// <summary>Optional notes or special instructions</summary>
    public string? Notes { get; private set; }

    /// <summary>When the order was released to production</summary>
    public DateTime? ReleasedAt { get; private set; }

    /// <summary>When all job cards completed</summary>
    public DateTime? CompletedAt { get; private set; }

    /// <summary>Job cards generated from this work order</summary>
    public IReadOnlyCollection<JobCard> JobCards => _jobCards.AsReadOnly();

    // ── DOMAIN METHODS ──────────────────────────────────────────

    /// <summary>Update work order details (only while in Draft status).</summary>
    public void Update(string productName, int quantity, DateTime? dueDate, string? notes)
    {
        if (Status != WorkOrderStatus.Draft)
            throw new InvalidOperationException("Cannot update a work order that is not in Draft status.");

        Guard.AgainstNullOrEmpty(productName, nameof(productName));
        Guard.AgainstZeroOrNegative(quantity, nameof(quantity));

        ProductName = productName;
        Quantity = quantity;
        DueDate = dueDate;
        Notes = notes;
    }

    /// <summary>Release the work order for production. Job cards should be generated after this.</summary>
    public void Release()
    {
        if (Status != WorkOrderStatus.Draft)
            throw new InvalidOperationException("Only Draft work orders can be released.");

        Status = WorkOrderStatus.Released;
        ReleasedAt = DateTime.UtcNow;
    }

    /// <summary>Mark the work order as in-progress (first job card started).</summary>
    public void StartProduction()
    {
        if (Status != WorkOrderStatus.Released)
            throw new InvalidOperationException("Only Released work orders can start production.");

        Status = WorkOrderStatus.InProgress;
    }

    /// <summary>Mark the work order as completed (all job cards finished).</summary>
    public void Complete()
    {
        if (Status != WorkOrderStatus.InProgress)
            throw new InvalidOperationException("Only InProgress work orders can be completed.");

        Status = WorkOrderStatus.Completed;
        CompletedAt = DateTime.UtcNow;
    }

    /// <summary>Cancel the work order.</summary>
    public void Cancel()
    {
        if (Status is WorkOrderStatus.Completed or WorkOrderStatus.Cancelled)
            throw new InvalidOperationException("Cannot cancel a completed or already cancelled work order.");

        Status = WorkOrderStatus.Cancelled;
    }

    /// <summary>Add a job card to this work order.</summary>
    internal void AddJobCard(JobCard jobCard)
    {
        _jobCards.Add(jobCard);
    }
}
