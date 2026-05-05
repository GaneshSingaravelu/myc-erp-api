using ERP.Application.Abstractions.CQRS;
using ERP.Application.Abstractions.Persistence;
using ERP.Production.Application.DTOs;
using ERP.Production.Domain.Entities;
using ERP.Production.Domain.Enums;
using ERP.SharedKernel.Results;

namespace ERP.Production.Application.Queries;

/// <summary>QUERY: Get job cards, optionally filtered by work order or status.</summary>
public record GetJobCardsQuery(
    Guid? WorkOrderId = null,
    JobCardStatus? Status = null) : IQuery<List<JobCardResponse>>;

public class GetJobCardsHandler : IQueryHandler<GetJobCardsQuery, List<JobCardResponse>>
{
    private readonly IRepository<JobCard> _jobCardRepo;

    public GetJobCardsHandler(IRepository<JobCard> jobCardRepo)
    {
        _jobCardRepo = jobCardRepo;
    }

    public async ValueTask<Result<List<JobCardResponse>>> Handle(
        GetJobCardsQuery request, CancellationToken cancellationToken)
    {
        IReadOnlyList<JobCard> jobCards;

        if (request.WorkOrderId.HasValue && request.Status.HasValue)
        {
            jobCards = await _jobCardRepo.FindAsync(
                jc => jc.WorkOrderId == request.WorkOrderId.Value && jc.Status == request.Status.Value,
                cancellationToken);
        }
        else if (request.WorkOrderId.HasValue)
        {
            jobCards = await _jobCardRepo.FindAsync(
                jc => jc.WorkOrderId == request.WorkOrderId.Value, cancellationToken);
        }
        else if (request.Status.HasValue)
        {
            jobCards = await _jobCardRepo.FindAsync(
                jc => jc.Status == request.Status.Value, cancellationToken);
        }
        else
        {
            jobCards = await _jobCardRepo.GetAllAsync(cancellationToken);
        }

        var result = jobCards
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
            .ToList();

        return result;
    }
}
