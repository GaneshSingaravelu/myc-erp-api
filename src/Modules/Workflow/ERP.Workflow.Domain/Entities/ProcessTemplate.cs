using ERP.SharedKernel.Entities;
using ERP.SharedKernel.Guards;

namespace ERP.Workflow.Domain.Entities;

/// <summary>
/// PROCESS TEMPLATE — Defines a reusable production workflow.
///
/// WHAT IS A PROCESS TEMPLATE?
/// ============================
/// In manufacturing, different products follow different production flows.
/// A "Steel Rod" might go: Cutting → Bending → Painting → QA → Assembly
/// A "Circuit Board" might go: Etching → Soldering → Testing → QA → Packaging
///
/// Instead of hardcoding these flows, we store them as data in the database.
/// Each ProcessTemplate has a name, version, and a collection of ProcessSteps.
///
/// WHY VERSIONING?
/// ================
/// When a client modifies their production flow, we don't delete the old one.
/// We create a new version. This way:
/// - In-progress work orders continue using the old version
/// - New work orders use the latest version
/// - Audit trail of how processes evolved over time
///
/// DYNAMIC WORKFLOW:
/// ==================
/// Clients modify the flow via CRUD operations on ProcessTemplate/ProcessStep.
/// No code deployment needed. Just add/remove/reorder steps in the admin UI.
/// </summary>
public class ProcessTemplate : AuditableEntity
{
    private readonly List<ProcessStep> _steps = [];

    private ProcessTemplate() { } // EF Core needs parameterless constructor

    public ProcessTemplate(string name, string? description = null)
    {
        Guard.AgainstNullOrEmpty(name, nameof(name));
        Name = name;
        Description = description;
        Version = 1;
        IsActive = true;
    }

    /// <summary>Template name (e.g., "Steel Rod Production Flow")</summary>
    public string Name { get; private set; } = null!;

    /// <summary>Description of what this template is for</summary>
    public string? Description { get; private set; }

    /// <summary>Version number — incremented when the flow is modified</summary>
    public int Version { get; private set; }

    /// <summary>Only one version can be active at a time per template name</summary>
    public bool IsActive { get; private set; }

    /// <summary>The ordered list of steps in this process</summary>
    public IReadOnlyCollection<ProcessStep> Steps => _steps.AsReadOnly();

    // ── DOMAIN METHODS (Business Logic) ─────────────────────────

    /// <summary>Add a step to this template</summary>
    public ProcessStep AddStep(
        string departmentName,
        int order,
        Workflow.Domain.Enums.StepType stepType = Enums.StepType.Single,
        bool qaRequired = true,
        string? instructions = null)
    {
        Guard.AgainstNullOrEmpty(departmentName, nameof(departmentName));
        Guard.AgainstZeroOrNegative(order, nameof(order));

        var step = new ProcessStep(
            templateId: Id,
            departmentName: departmentName,
            order: order,
            stepType: stepType,
            qaRequired: qaRequired,
            instructions: instructions);

        _steps.Add(step);
        return step;
    }

    /// <summary>Remove a step from this template</summary>
    public void RemoveStep(Guid stepId)
    {
        var step = _steps.FirstOrDefault(s => s.Id == stepId);
        if (step is not null)
            _steps.Remove(step);
    }

    /// <summary>Update template details</summary>
    public void Update(string name, string? description)
    {
        Guard.AgainstNullOrEmpty(name, nameof(name));
        Name = name;
        Description = description;
    }

    /// <summary>Deactivate this template version</summary>
    public void Deactivate() => IsActive = false;

    /// <summary>Activate this template version</summary>
    public void Activate() => IsActive = true;

    /// <summary>Create a new version of this template (copies steps)</summary>
    public ProcessTemplate CreateNewVersion()
    {
        var newVersion = new ProcessTemplate(Name, Description)
        {
            Version = Version + 1,
            IsActive = true
        };

        // Copy all steps to the new version
        foreach (var step in _steps.OrderBy(s => s.Order))
        {
            newVersion.AddStep(
                step.DepartmentName,
                step.Order,
                step.StepType,
                step.QaRequired,
                step.Instructions);
        }

        // Deactivate this version
        Deactivate();

        return newVersion;
    }
}
