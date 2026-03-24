using ERP.Application.Abstractions.CQRS;
using ERP.Application.Abstractions.Persistence;
using ERP.SharedKernel.Results;
using ERP.Workflow.Application.DTOs;
using ERP.Workflow.Domain.Entities;

namespace ERP.Workflow.Application.Commands;

/// <summary>
/// COMMAND: Create a new process template with optional initial steps.
///
/// CQRS FLOW (recap):
/// Controller receives HTTP POST → creates this Command → sends via MediatR
/// → MediatR finds CreateProcessTemplateHandler → handler executes → returns Result
/// </summary>
public record CreateProcessTemplateCommand(
    string Name,
    string? Description,
    List<CreateProcessStepRequest>? Steps) : ICommand<Guid>;

public class CreateProcessTemplateHandler : ICommandHandler<CreateProcessTemplateCommand, Guid>
{
    private readonly IRepository<ProcessTemplate> _templateRepo;
    private readonly IUnitOfWork _unitOfWork;

    public CreateProcessTemplateHandler(IRepository<ProcessTemplate> templateRepo, IUnitOfWork unitOfWork)
    {
        _templateRepo = templateRepo;
        _unitOfWork = unitOfWork;
    }

    public async Task<Result<Guid>> Handle(CreateProcessTemplateCommand request, CancellationToken cancellationToken)
    {
        // Check for duplicate active template name
        var exists = await _templateRepo.AnyAsync(
            t => t.Name == request.Name && t.IsActive, cancellationToken);

        if (exists)
            return Result.Failure<Guid>(
                Error.Conflict("Template.Duplicate", $"An active template named '{request.Name}' already exists."));

        // Create the template
        var template = new ProcessTemplate(request.Name, request.Description);

        // Add steps if provided
        if (request.Steps?.Count > 0)
        {
            foreach (var stepDto in request.Steps.OrderBy(s => s.Order))
            {
                var step = template.AddStep(
                    stepDto.DepartmentName,
                    stepDto.Order,
                    stepDto.StepType,
                    stepDto.QaRequired,
                    stepDto.Instructions);

                if (stepDto.EstimatedDurationMinutes.HasValue)
                    step.Update(stepDto.DepartmentName, stepDto.Order, stepDto.StepType,
                        stepDto.QaRequired, stepDto.Instructions, stepDto.EstimatedDurationMinutes);

                if (stepDto.NextStepId.HasValue)
                    step.SetNextStep(stepDto.NextStepId);
            }
        }

        await _templateRepo.AddAsync(template, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return template.Id;
    }
}
