using ERP.Application.Abstractions.CQRS;
using ERP.Application.Abstractions.Persistence;
using ERP.Production.Domain.Entities;
using ERP.SharedKernel.Results;

namespace ERP.Production.Application.Commands;

/// <summary>COMMAND: Soft-delete a work order.</summary>
public record DeleteWorkOrderCommand(Guid Id) : ICommand;

public class DeleteWorkOrderHandler : ICommandHandler<DeleteWorkOrderCommand>
{
    private readonly IRepository<WorkOrder> _workOrderRepo;
    private readonly IUnitOfWork _unitOfWork;

    public DeleteWorkOrderHandler(IRepository<WorkOrder> workOrderRepo, IUnitOfWork unitOfWork)
    {
        _workOrderRepo = workOrderRepo;
        _unitOfWork = unitOfWork;
    }

    public async ValueTask<Result> Handle(DeleteWorkOrderCommand request, CancellationToken cancellationToken)
    {
        var workOrder = await _workOrderRepo.GetByIdAsync(request.Id, cancellationToken);
        if (workOrder is null)
            return Result.Failure(Error.NotFound("WorkOrder", request.Id));

        _workOrderRepo.Remove(workOrder);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
