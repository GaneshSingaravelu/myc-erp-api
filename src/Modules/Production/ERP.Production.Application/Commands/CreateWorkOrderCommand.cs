using ERP.Application.Abstractions.CQRS;
using ERP.Application.Abstractions.Persistence;
using ERP.Production.Domain.Entities;
using ERP.SharedKernel.Results;

namespace ERP.Production.Application.Commands;

/// <summary>COMMAND: Create a new work order in Draft status.</summary>
public record CreateWorkOrderCommand(
    string OrderNumber,
    string ProductName,
    int Quantity,
    Guid ProcessTemplateId,
    DateTime? DueDate,
    string? Notes) : ICommand<Guid>;

public class CreateWorkOrderHandler : ICommandHandler<CreateWorkOrderCommand, Guid>
{
    private readonly IRepository<WorkOrder> _workOrderRepo;
    private readonly IUnitOfWork _unitOfWork;

    public CreateWorkOrderHandler(IRepository<WorkOrder> workOrderRepo, IUnitOfWork unitOfWork)
    {
        _workOrderRepo = workOrderRepo;
        _unitOfWork = unitOfWork;
    }

    public async Task<Result<Guid>> Handle(CreateWorkOrderCommand request, CancellationToken cancellationToken)
    {
        // Check for duplicate order number
        var exists = await _workOrderRepo.AnyAsync(
            wo => wo.OrderNumber == request.OrderNumber, cancellationToken);

        if (exists)
            return Result.Failure<Guid>(
                Error.Conflict("WorkOrder.Duplicate", $"A work order with number '{request.OrderNumber}' already exists."));

        var workOrder = new WorkOrder(
            request.OrderNumber,
            request.ProductName,
            request.Quantity,
            request.ProcessTemplateId,
            request.DueDate,
            request.Notes);

        await _workOrderRepo.AddAsync(workOrder, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return workOrder.Id;
    }
}
