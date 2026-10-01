using ERP.Production.Domain.Enums;

namespace ERP.Production.Application.DTOs;

// ── RESPONSE DTOs ────────────────────────────────────────────

// DueDate is DateOnly? — a business calendar date, serialized as "2026-10-01" with no time
// and no offset. ReleasedAt, CompletedAt, StartedAt and CreatedAt are genuine instants and
// remain DateTime. See WorkOrder.DueDate for why the two are modelled differently.

public record WorkOrderResponse(
    Guid Id,
    string OrderNumber,
    string ProductName,
    int Quantity,
    Guid ProcessTemplateId,
    Guid PlantId,
    WorkOrderStatus Status,
    DateOnly? DueDate,
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
    DateOnly? DueDate,
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

// Breaking contract change: DueDate now accepts a date only. A timestamp-shaped value such as
// "2026-10-01T14:30:00" is REJECTED by System.Text.Json rather than silently truncated to the
// date, because truncating would have discarded a value the caller clearly meant.

public record CreateWorkOrderRequest(
    string OrderNumber,
    string ProductName,
    int Quantity,
    Guid ProcessTemplateId,
    Guid PlantId,
    DateOnly? DueDate = null,
    string? Notes = null);

public record UpdateWorkOrderRequest(
    string ProductName,
    int Quantity,
    DateOnly? DueDate,
    string? Notes);

public record UpdateJobCardStatusRequest(
    string Action,
    string? Notes = null);
