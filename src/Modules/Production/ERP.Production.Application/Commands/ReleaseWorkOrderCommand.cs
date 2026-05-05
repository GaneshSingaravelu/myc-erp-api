using ERP.Application.Abstractions.CQRS;
using ERP.Application.Abstractions.Persistence;
using ERP.Production.Domain.Entities;
using ERP.Production.Domain.ValueObjects;
using ERP.SharedKernel.Results;
using ERP.Workflow.Domain.Entities;

namespace ERP.Production.Application.Commands;

/// <summary>
/// COMMAND: Release a work order for production.
/// This transitions Draft → Released and generates job cards for the first step
/// of the associated process template.
/// </summary>
public record ReleaseWorkOrderCommand(Guid Id) : ICommand;

public class ReleaseWorkOrderHandler : ICommandHandler<ReleaseWorkOrderCommand>
{
    private readonly IRepository<WorkOrder> _workOrderRepo;
    private readonly IRepository<JobCard> _jobCardRepo;
    private readonly IRepository<ProcessTemplate> _templateRepo;
    private readonly IUnitOfWork _unitOfWork;

    public ReleaseWorkOrderHandler(
        IRepository<WorkOrder> workOrderRepo,
        IRepository<JobCard> jobCardRepo,
        IRepository<ProcessTemplate> templateRepo,
        IUnitOfWork unitOfWork)
    {
        _workOrderRepo = workOrderRepo;
        _jobCardRepo = jobCardRepo;
        _templateRepo = templateRepo;
        _unitOfWork = unitOfWork;
    }

    public async ValueTask<Result> Handle(ReleaseWorkOrderCommand request, CancellationToken cancellationToken)
    {
        var workOrder = await _workOrderRepo.GetByIdAsync(request.Id, cancellationToken);
        if (workOrder is null)
            return Result.Failure(Error.NotFound("WorkOrder", request.Id));

        // Load the process template with steps
        var template = await _templateRepo.GetByIdAsync(workOrder.ProcessTemplateId, cancellationToken);
        if (template is null)
            return Result.Failure(Error.NotFound("ProcessTemplate", workOrder.ProcessTemplateId));

        if (!template.IsActive)
            return Result.Failure(Error.Validation("ProcessTemplate.Inactive",
                "Cannot release work order — the associated process template is inactive."));

        var firstSteps = template.Steps.Where(s => s.Order == template.Steps.Min(x => x.Order)).ToList();
        if (firstSteps.Count == 0)
            return Result.Failure(Error.Validation("ProcessTemplate.NoSteps",
                "Cannot release work order — the process template has no steps."));

        try
        {
            workOrder.Release();
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure(Error.Validation("WorkOrder.InvalidState", ex.Message));
        }

        // Determine running number: count today's existing job cards + 1
        var today = DateTime.UtcNow.Date;
        var todayCards = await _jobCardRepo.FindAsync(
            jc => jc.CreatedAt >= today && jc.CreatedAt < today.AddDays(1),
            cancellationToken);
        var runningNumber = todayCards.Count + 1;

        // Generate a job card for each first step
        foreach (var step in firstSteps)
        {
            var jobCardNumber = JobCardNumber.Create(DateTime.UtcNow, runningNumber);
            var jobCard = new JobCard(
                workOrder.Id,
                jobCardNumber,
                step.Id,
                step.DepartmentName,
                workOrder.Quantity,
                step.Instructions);

            await _jobCardRepo.AddAsync(jobCard, cancellationToken);
            runningNumber++;
        }

        _workOrderRepo.Update(workOrder);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
