using ERP.Application.Abstractions.CQRS;
using ERP.Application.Abstractions.Persistence;
using ERP.Production.Application.DTOs;
using ERP.Production.Domain.Entities;
using ERP.Production.Domain.Enums;
using ERP.SharedKernel.Results;

namespace ERP.Production.Application.Queries;

/// <summary>QUERY: Get all work orders, optionally filtered by status.</summary>
public record GetWorkOrdersQuery(WorkOrderStatus? Status = null) : IQuery<List<WorkOrderListResponse>>;

public class GetWorkOrdersHandler : IQueryHandler<GetWorkOrdersQuery, List<WorkOrderListResponse>>
{
    private readonly IRepository<WorkOrder> _workOrderRepo;

    public GetWorkOrdersHandler(IRepository<WorkOrder> workOrderRepo)
    {
        _workOrderRepo = workOrderRepo;
    }

    public async ValueTask<Result<List<WorkOrderListResponse>>> Handle(
        GetWorkOrdersQuery request, CancellationToken cancellationToken)
    {
        var workOrders = request.Status.HasValue
            ? await _workOrderRepo.FindAsync(wo => wo.Status == request.Status.Value, cancellationToken)
            : await _workOrderRepo.GetAllAsync(cancellationToken);

        var result = workOrders
            .Select(wo => new WorkOrderListResponse(
                wo.Id,
                wo.OrderNumber,
                wo.ProductName,
                wo.Quantity,
                wo.Status,
                wo.DueDate,
                wo.ReleasedAt,
                wo.JobCards.Count,
                wo.CreatedAt))
            .OrderByDescending(wo => wo.CreatedAt)
            .ToList();

        return result;
    }
}
