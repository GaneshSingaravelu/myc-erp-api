using ERP.Application.Abstractions.CQRS;
using ERP.Application.Abstractions.Persistence;
using ERP.SharedKernel.Results;
using ERP.Workflow.Domain.Entities;

namespace ERP.Workflow.Application.Commands;

/// <summary>COMMAND: Remove a step from a process template.</summary>
public record RemoveProcessStepCommand(Guid TemplateId, Guid StepId) : ICommand;

public class RemoveProcessStepHandler : ICommandHandler<RemoveProcessStepCommand>
{
    private readonly IRepository<ProcessStep> _stepRepo;
    private readonly IUnitOfWork _unitOfWork;

    public RemoveProcessStepHandler(IRepository<ProcessStep> stepRepo, IUnitOfWork unitOfWork)
    {
        _stepRepo = stepRepo;
        _unitOfWork = unitOfWork;
    }

    public async Task<Result> Handle(RemoveProcessStepCommand request, CancellationToken cancellationToken)
    {
        var step = await _stepRepo.GetByIdAsync(request.StepId, cancellationToken);
        if (step is null)
            return Result.Failure(Error.NotFound("ProcessStep", request.StepId));

        _stepRepo.Remove(step);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
