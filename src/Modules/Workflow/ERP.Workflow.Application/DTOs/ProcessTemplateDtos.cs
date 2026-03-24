using ERP.Workflow.Domain.Enums;

namespace ERP.Workflow.Application.DTOs;

/// <summary>
/// DTOs (Data Transfer Objects) for the Workflow module.
///
/// WHY DTOs? (Interview Question!)
/// =================================
/// DTOs are simple objects used to transfer data between layers.
/// We NEVER return domain entities directly from API endpoints because:
///
/// 1. SECURITY: Entity might have fields we don't want to expose (PasswordHash, internal flags)
/// 2. SHAPE: API response shape may differ from entity shape (flatten nested objects)
/// 3. STABILITY: Entity can change without breaking the API contract
/// 4. PERFORMANCE: DTO can include only the fields the client needs
///
/// NAMING CONVENTION:
/// ===================
/// - {Entity}Response — returned FROM the API to the client
/// - {Action}{Entity}Request — sent TO the API from the client
/// </summary>

// ── RESPONSE DTOs ────────────────────────────────────────────

public record ProcessTemplateResponse(
    Guid Id,
    string Name,
    string? Description,
    int Version,
    bool IsActive,
    DateTime CreatedAt,
    string? CreatedBy,
    List<ProcessStepResponse> Steps);

public record ProcessTemplateListResponse(
    Guid Id,
    string Name,
    string? Description,
    int Version,
    bool IsActive,
    int StepCount,
    DateTime CreatedAt);

public record ProcessStepResponse(
    Guid Id,
    string DepartmentName,
    int Order,
    StepType StepType,
    bool QaRequired,
    string? Instructions,
    Guid? NextStepId,
    int? EstimatedDurationMinutes);

// ── REQUEST DTOs ─────────────────────────────────────────────

public record CreateProcessTemplateRequest(
    string Name,
    string? Description,
    List<CreateProcessStepRequest>? Steps);

public record UpdateProcessTemplateRequest(
    string Name,
    string? Description);

public record CreateProcessStepRequest(
    string DepartmentName,
    int Order,
    StepType StepType = StepType.Single,
    bool QaRequired = true,
    string? Instructions = null,
    Guid? NextStepId = null,
    int? EstimatedDurationMinutes = null);

public record UpdateProcessStepRequest(
    string DepartmentName,
    int Order,
    StepType StepType,
    bool QaRequired,
    string? Instructions,
    Guid? NextStepId,
    int? EstimatedDurationMinutes);
