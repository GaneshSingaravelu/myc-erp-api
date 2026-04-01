using ERP.Application.Abstractions.CQRS;
using ERP.Application.Abstractions.Persistence;
using ERP.SharedKernel.Results;
using ERP.Workflow.Domain.Entities;
using ERP.Workflow.Domain.Enums;

namespace ERP.Workflow.Application.Commands;

/// <summary>COMMAND: Update an existing process step.</summary>
public record UpdateProcessStepCommand(
    Guid TemplateId,
    Guid StepId,
    string DepartmentName,
    int Order,
    StepType StepType,
    bool QaRequired,
    string? Instructions,
    Guid? NextStepId,
    int? EstimatedDurationMinutes) : ICommand;

public class UpdateProcessStepHandler : ICommandHandler<UpdateProcessStepCommand>
{
    private readonly IRepository<ProcessStep> _stepRepo;
    private readonly IUnitOfWork _unitOfWork;

    public UpdateProcessStepHandler(IRepository<ProcessStep> stepRepo, IUnitOfWork unitOfWork)
    {
        _stepRepo = stepRepo;
        _unitOfWork = unitOfWork;
    }

    public async Task<Result> Handle(UpdateProcessStepCommand request, CancellationToken cancellationToken)
    {
        var step = await _stepRepo.GetByIdAsync(request.StepId, cancellationToken);
        if (step is null)
            return Result.Failure(Error.NotFound("ProcessStep", request.StepId));

        step.Update(
            request.DepartmentName,
            request.Order,
            request.StepType,
            request.QaRequired,
            request.Instructions,
            request.EstimatedDurationMinutes);

        step.SetNextStep(request.NextStepId);

        _stepRepo.Update(step);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
