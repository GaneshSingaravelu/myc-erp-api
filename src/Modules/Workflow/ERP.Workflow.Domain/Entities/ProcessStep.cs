using ERP.SharedKernel.Entities;
using ERP.SharedKernel.Guards;
using ERP.Workflow.Domain.Enums;

namespace ERP.Workflow.Domain.Entities;

/// <summary>
/// PROCESS STEP — A single step in a production workflow.
///
/// WHAT IS A PROCESS STEP?
/// ========================
/// Each step represents one department's work in the production flow.
/// For example: "Cutting Department — Step 1 — QA Required"
///
/// HOW THE FLOW ENGINE USES THIS:
/// ================================
/// When a job card completes a step, the engine looks at:
/// 1. What is the NextStepId? → Move job card there
/// 2. Is the next step Parallel? → Create split job cards for each parallel dept
/// 3. Is the next step Merge? → Wait for all parallel paths, then continue
/// 4. Is NextStepId null? → This is the final step → Create FG entry
///
/// STEP ORDERING:
/// ===============
/// Steps are ordered by the "Order" property.
/// Steps with the SAME Order number are parallel (run simultaneously).
///
/// Example:
/// Order 1: Cutting    (Single)    → NextStepId = Step 2
/// Order 2: Bending    (Single)    → NextStepId = Step 3
/// Order 3: Painting   (Parallel)  → NextStepId = Step 5 (merge point)
/// Order 3: Coating    (Parallel)  → NextStepId = Step 5 (merge point)
/// Order 4: (skipped — parallel steps share Order 3)
/// Order 5: Assembly   (Merge)     → NextStepId = null (final step)
/// </summary>
public class ProcessStep : AuditableEntity
{
    private ProcessStep() { } // EF Core

    internal ProcessStep(
        Guid templateId,
        string departmentName,
        int order,
        StepType stepType,
        bool qaRequired,
        string? instructions)
    {
        Guard.AgainstNullOrEmpty(departmentName, nameof(departmentName));
        Guard.AgainstZeroOrNegative(order, nameof(order));

        TemplateId = templateId;
        DepartmentName = departmentName;
        Order = order;
        StepType = stepType;
        QaRequired = qaRequired;
        Instructions = instructions;
    }

    /// <summary>FK to ProcessTemplate</summary>
    public Guid TemplateId { get; private set; }

    /// <summary>Navigation property to parent template</summary>
    public ProcessTemplate Template { get; private set; } = null!;

    /// <summary>Department responsible for this step (e.g., "Cutting", "QA", "Assembly")</summary>
    public string DepartmentName { get; private set; } = null!;

    /// <summary>
    /// Execution order within the template.
    /// Steps with the same Order run in parallel.
    /// </summary>
    public int Order { get; private set; }

    /// <summary>How this step behaves: Single (sequential), Parallel (split), or Merge (join)</summary>
    public StepType StepType { get; private set; }

    /// <summary>Whether QA inspection is required after this step completes</summary>
    public bool QaRequired { get; private set; }

    /// <summary>Optional work instructions or notes for the operator</summary>
    public string? Instructions { get; private set; }

    /// <summary>
    /// ID of the next step in the flow. Null = final step.
    /// For parallel steps, all point to the same merge step.
    /// </summary>
    public Guid? NextStepId { get; private set; }

    /// <summary>Navigation property to the next step</summary>
    public ProcessStep? NextStep { get; private set; }

    /// <summary>
    /// Estimated duration in minutes for this step.
    /// Used for capacity planning and scheduling.
    /// </summary>
    public int? EstimatedDurationMinutes { get; private set; }

    // ── DOMAIN METHODS ──────────────────────────────────────────

    /// <summary>Link this step to the next step in the flow</summary>
    public void SetNextStep(Guid? nextStepId)
    {
        NextStepId = nextStepId;
    }

    /// <summary>Update step details</summary>
    public void Update(
        string departmentName,
        int order,
        StepType stepType,
        bool qaRequired,
        string? instructions,
        int? estimatedDurationMinutes)
    {
        Guard.AgainstNullOrEmpty(departmentName, nameof(departmentName));
        Guard.AgainstZeroOrNegative(order, nameof(order));

        DepartmentName = departmentName;
        Order = order;
        StepType = stepType;
        QaRequired = qaRequired;
        Instructions = instructions;
        EstimatedDurationMinutes = estimatedDurationMinutes;
    }
}
