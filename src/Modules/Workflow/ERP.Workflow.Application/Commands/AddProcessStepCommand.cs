using ERP.Application.Abstractions.CQRS;
using ERP.Application.Abstractions.Persistence;
using ERP.SharedKernel.Results;
using ERP.Workflow.Domain.Entities;
using ERP.Workflow.Domain.Enums;

namespace ERP.Workflow.Application.Commands;

/// <summary>COMMAND: Add a step to an existing process template.</summary>
public record AddProcessStepCommand(
    Guid TemplateId,
    string DepartmentName,
    int Order,
    StepType StepType = StepType.Single,
    bool QaRequired = true,
    string? Instructions = null,
    Guid? NextStepId = null,
    int? EstimatedDurationMinutes = null) : ICommand<Guid>;

public class AddProcessStepHandler : ICommandHandler<AddProcessStepCommand, Guid>
{
    private readonly IRepository<ProcessTemplate> _templateRepo;
    private readonly IUnitOfWork _unitOfWork;

    public AddProcessStepHandler(IRepository<ProcessTemplate> templateRepo, IUnitOfWork unitOfWork)
    {
        _templateRepo = templateRepo;
        _unitOfWork = unitOfWork;
    }

    public async Task<Result<Guid>> Handle(AddProcessStepCommand request, CancellationToken cancellationToken)
    {
        var template = await _templateRepo.GetByIdAsync(request.TemplateId, cancellationToken);
        if (template is null)
            return Result.Failure<Guid>(Error.NotFound("ProcessTemplate", request.TemplateId));

        var step = template.AddStep(
            request.DepartmentName,
            request.Order,
            request.StepType,
            request.QaRequired,
            request.Instructions);

        if (request.NextStepId.HasValue)
            step.SetNextStep(request.NextStepId);

        if (request.EstimatedDurationMinutes.HasValue)
            step.Update(request.DepartmentName, request.Order, request.StepType,
                request.QaRequired, request.Instructions, request.EstimatedDurationMinutes);

        _templateRepo.Update(template);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return step.Id;
    }
}
