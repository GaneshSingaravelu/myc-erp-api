using ERP.Application.Abstractions.CQRS;
using ERP.Application.Abstractions.Persistence;
using ERP.Production.Application.DTOs;
using ERP.Production.Domain.Entities;
using ERP.SharedKernel.Results;

namespace ERP.Production.Application.Queries;

/// <summary>QUERY: Get a single work order with all its job cards.</summary>
public record GetWorkOrderByIdQuery(Guid Id) : IQuery<WorkOrderResponse>;

public class GetWorkOrderByIdHandler : IQueryHandler<GetWorkOrderByIdQuery, WorkOrderResponse>
{
    private readonly IRepository<WorkOrder> _workOrderRepo;

    public GetWorkOrderByIdHandler(IRepository<WorkOrder> workOrderRepo)
    {
        _workOrderRepo = workOrderRepo;
    }

    public async Task<Result<WorkOrderResponse>> Handle(
        GetWorkOrderByIdQuery request, CancellationToken cancellationToken)
    {
        var workOrder = await _workOrderRepo.GetByIdAsync(request.Id, cancellationToken);
        if (workOrder is null)
            return Result.Failure<WorkOrderResponse>(Error.NotFound("WorkOrder", request.Id));

        var response = new WorkOrderResponse(
            workOrder.Id,
            workOrder.OrderNumber,
            workOrder.ProductName,
            workOrder.Quantity,
            workOrder.ProcessTemplateId,
            workOrder.PlantId,
            workOrder.Status,
            workOrder.DueDate,
            workOrder.Notes,
            workOrder.ReleasedAt,
            workOrder.CompletedAt,
            workOrder.CreatedAt,
            workOrder.CreatedBy,
            workOrder.JobCards
                .OrderBy(jc => jc.JobCardNumber.Value)
                .Select(jc => new JobCardResponse(
                    jc.Id,
                    jc.JobCardNumber.Value,
                    jc.ProcessStepId,
                    jc.DepartmentName,
                    jc.Quantity,
                    jc.Status,
                    jc.Instructions,
                    jc.StartedAt,
                    jc.CompletedAt,
                    jc.Notes,
                    jc.ParentJobCardId))
                .ToList());

        return response;
    }
}
