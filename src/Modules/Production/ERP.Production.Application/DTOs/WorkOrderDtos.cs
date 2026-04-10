using ERP.Production.Domain.Enums;

namespace ERP.Production.Application.DTOs;

// ── RESPONSE DTOs ────────────────────────────────────────────

public record WorkOrderResponse(
    Guid Id,
    string OrderNumber,
    string ProductName,
    int Quantity,
    Guid ProcessTemplateId,
    WorkOrderStatus Status,
    DateTime? DueDate,
    string? Notes,
    DateTime? ReleasedAt,
    DateTime? CompletedAt,
    DateTime CreatedAt,
    Guid? CreatedBy,
    List<JobCardResponse> JobCards);

public record WorkOrderListResponse(
    Guid Id,
    string OrderNumber,
    string ProductName,
    int Quantity,
    WorkOrderStatus Status,
    DateTime? DueDate,
    DateTime? ReleasedAt,
    int JobCardCount,
    DateTime CreatedAt);

public record JobCardResponse(
    Guid Id,
    string JobCardNumber,
    Guid ProcessStepId,
    string DepartmentName,
    int Quantity,
    JobCardStatus Status,
    string? Instructions,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    string? Notes,
    Guid? ParentJobCardId);

// ── REQUEST DTOs ─────────────────────────────────────────────

public record CreateWorkOrderRequest(
    string OrderNumber,
    string ProductName,
    int Quantity,
    Guid ProcessTemplateId,
    DateTime? DueDate = null,
    string? Notes = null);

public record UpdateWorkOrderRequest(
    string ProductName,
    int Quantity,
    DateTime? DueDate,
    string? Notes);

public record UpdateJobCardStatusRequest(
    string Action,
    string? Notes = null);
