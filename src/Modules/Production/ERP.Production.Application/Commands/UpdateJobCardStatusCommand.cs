using ERP.Application.Abstractions.CQRS;
using ERP.Application.Abstractions.Persistence;
using ERP.Production.Domain.Entities;
using ERP.Production.Domain.ValueObjects;
using ERP.SharedKernel.Results;
using ERP.Workflow.Domain.Entities;

namespace ERP.Production.Application.Commands;

/// <summary>
/// COMMAND: Update a job card's status (start, complete-step, pass-qa, fail-qa, hold, resume, cancel).
///
/// ACTIONS:
/// ========
/// "start"         → Pending → InProgress
/// "complete-step" → InProgress → AwaitingQa (if QA required) or Completed
/// "pass-qa"       → AwaitingQa → QaPassed → moves to next step or Completed
/// "fail-qa"       → AwaitingQa → QaFailed (creates rework job card)
/// "hold"          → Any active → OnHold
/// "resume"        → OnHold → Pending
/// "cancel"        → Any active → Cancelled
/// </summary>
public record UpdateJobCardStatusCommand(
    Guid JobCardId,
    string Action,
    string? Notes) : ICommand;

public class UpdateJobCardStatusHandler : ICommandHandler<UpdateJobCardStatusCommand>
{
    private readonly IRepository<JobCard> _jobCardRepo;
    private readonly IRepository<WorkOrder> _workOrderRepo;
    private readonly IRepository<ProcessStep> _stepRepo;
    private readonly IUnitOfWork _unitOfWork;

    public UpdateJobCardStatusHandler(
        IRepository<JobCard> jobCardRepo,
        IRepository<WorkOrder> workOrderRepo,
        IRepository<ProcessStep> stepRepo,
        IUnitOfWork unitOfWork)
    {
        _jobCardRepo = jobCardRepo;
        _workOrderRepo = workOrderRepo;
        _stepRepo = stepRepo;
        _unitOfWork = unitOfWork;
    }

    public async Task<Result> Handle(UpdateJobCardStatusCommand request, CancellationToken cancellationToken)
    {
        var jobCard = await _jobCardRepo.GetByIdAsync(request.JobCardId, cancellationToken);
        if (jobCard is null)
            return Result.Failure(Error.NotFound("JobCard", request.JobCardId));

        try
        {
            switch (request.Action.ToLowerInvariant())
            {
                case "start":
                    jobCard.Start();
                    // Transition work order to InProgress if still Released
                    await TryStartWorkOrder(jobCard.WorkOrderId, cancellationToken);
                    break;

                case "complete-step":
                    var step = await _stepRepo.GetByIdAsync(jobCard.ProcessStepId, cancellationToken);
                    if (step is null)
                        return Result.Failure(Error.NotFound("ProcessStep", jobCard.ProcessStepId));
                    jobCard.CompleteStep(step.QaRequired);
                    break;

                case "pass-qa":
                    jobCard.PassQa(request.Notes);
                    // Move to next step or complete
                    await MoveToNextStepOrComplete(jobCard, cancellationToken);
                    break;

                case "fail-qa":
                    jobCard.FailQa(request.Notes);
                    await CreateReworkJobCard(jobCard, cancellationToken);
                    break;

                case "hold":
                    jobCard.PutOnHold(request.Notes);
                    break;

                case "resume":
                    jobCard.Resume();
                    break;

                case "cancel":
                    jobCard.Cancel();
                    break;

                default:
                    return Result.Failure(Error.Validation("JobCard.InvalidAction",
                        $"Unknown action '{request.Action}'. Valid actions: start, complete-step, pass-qa, fail-qa, hold, resume, cancel."));
            }
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure(Error.Validation("JobCard.InvalidState", ex.Message));
        }

        _jobCardRepo.Update(jobCard);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }

    private async Task TryStartWorkOrder(Guid workOrderId, CancellationToken ct)
    {
        var workOrder = await _workOrderRepo.GetByIdAsync(workOrderId, ct);
        if (workOrder is not null && workOrder.Status == Domain.Enums.WorkOrderStatus.Released)
        {
            workOrder.StartProduction();
            _workOrderRepo.Update(workOrder);
        }
    }

    private async Task MoveToNextStepOrComplete(JobCard jobCard, CancellationToken ct)
    {
        var currentStep = await _stepRepo.GetByIdAsync(jobCard.ProcessStepId, ct);
        if (currentStep?.NextStepId is null)
        {
            // Final step — mark job card as completed
            jobCard.Complete();
            await TryCompleteWorkOrder(jobCard.WorkOrderId, ct);
            return;
        }

        // Create job card for the next step
        var nextStep = await _stepRepo.GetByIdAsync(currentStep.NextStepId.Value, ct);
        if (nextStep is null) return;

        var today = DateTime.UtcNow.Date;
        var todayCards = await _jobCardRepo.FindAsync(
            jc => jc.CreatedAt >= today && jc.CreatedAt < today.AddDays(1), ct);
        var runningNumber = todayCards.Count + 1;

        var nextJobCardNumber = JobCardNumber.Create(DateTime.UtcNow, runningNumber);
        var nextJobCard = new JobCard(
            jobCard.WorkOrderId,
            nextJobCardNumber,
            nextStep.Id,
            nextStep.DepartmentName,
            jobCard.Quantity,
            nextStep.Instructions);

        nextJobCard.SetParent(jobCard.Id);
        await _jobCardRepo.AddAsync(nextJobCard, ct);

        // Mark current job card as completed
        jobCard.Complete();
    }

    private async Task TryCompleteWorkOrder(Guid workOrderId, CancellationToken ct)
    {
        var workOrder = await _workOrderRepo.GetByIdAsync(workOrderId, ct);
        if (workOrder is null) return;

        // Check if all job cards for this work order are completed or cancelled
        var activeCards = await _jobCardRepo.FindAsync(
            jc => jc.WorkOrderId == workOrderId
                  && jc.Status != Domain.Enums.JobCardStatus.Completed
                  && jc.Status != Domain.Enums.JobCardStatus.Cancelled, ct);

        if (activeCards.Count == 0)
        {
            workOrder.Complete();
            _workOrderRepo.Update(workOrder);
        }
    }

    private async Task CreateReworkJobCard(JobCard failedCard, CancellationToken ct)
    {
        var reworkNumber = failedCard.JobCardNumber.GetReworkNumber() + 1;
        var reworkJobCardNumber = failedCard.JobCardNumber.CreateRework(reworkNumber);

        var reworkCard = new JobCard(
            failedCard.WorkOrderId,
            reworkJobCardNumber,
            failedCard.ProcessStepId,
            failedCard.DepartmentName,
            failedCard.Quantity,
            failedCard.Instructions);

        reworkCard.SetParent(failedCard.Id);
        await _jobCardRepo.AddAsync(reworkCard, ct);
    }
}
