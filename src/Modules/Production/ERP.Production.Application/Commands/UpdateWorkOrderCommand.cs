using ERP.Application.Abstractions.CQRS;
using ERP.Application.Abstractions.Persistence;
using ERP.Production.Domain.Entities;
using ERP.SharedKernel.Results;

namespace ERP.Production.Application.Commands;

/// <summary>COMMAND: Update work order details (only while in Draft status).</summary>
public record UpdateWorkOrderCommand(
    Guid Id,
    string ProductName,
    int Quantity,
    DateTime? DueDate,
    string? Notes) : ICommand;

public class UpdateWorkOrderHandler : ICommandHandler<UpdateWorkOrderCommand>
{
    private readonly IRepository<WorkOrder> _workOrderRepo;
    private readonly IUnitOfWork _unitOfWork;

    public UpdateWorkOrderHandler(IRepository<WorkOrder> workOrderRepo, IUnitOfWork unitOfWork)
    {
        _workOrderRepo = workOrderRepo;
        _unitOfWork = unitOfWork;
    }

    public async Task<Result> Handle(UpdateWorkOrderCommand request, CancellationToken cancellationToken)
    {
        var workOrder = await _workOrderRepo.GetByIdAsync(request.Id, cancellationToken);
        if (workOrder is null)
            return Result.Failure(Error.NotFound("WorkOrder", request.Id));

        try
        {
            workOrder.Update(request.ProductName, request.Quantity, request.DueDate, request.Notes);
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure(Error.Validation("WorkOrder.InvalidState", ex.Message));
        }

        _workOrderRepo.Update(workOrder);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
