using ERP.Application.Abstractions.CQRS;
using ERP.Application.Abstractions.Persistence;
using ERP.SharedKernel.Results;
using ERP.Workflow.Application.DTOs;
using ERP.Workflow.Domain.Entities;

namespace ERP.Workflow.Application.Queries;

/// <summary>QUERY: Get a single process template with all its steps.</summary>
public record GetProcessTemplateByIdQuery(Guid Id) : IQuery<ProcessTemplateResponse>;

public class GetProcessTemplateByIdHandler
    : IQueryHandler<GetProcessTemplateByIdQuery, ProcessTemplateResponse>
{
    private readonly IRepository<ProcessTemplate> _templateRepo;

    public GetProcessTemplateByIdHandler(IRepository<ProcessTemplate> templateRepo)
    {
        _templateRepo = templateRepo;
    }

    public async ValueTask<Result<ProcessTemplateResponse>> Handle(
        GetProcessTemplateByIdQuery request, CancellationToken cancellationToken)
    {
        var template = await _templateRepo.GetByIdAsync(request.Id, cancellationToken);
        if (template is null)
            return Result.Failure<ProcessTemplateResponse>(
                Error.NotFound("ProcessTemplate", request.Id));

        var response = new ProcessTemplateResponse(
            template.Id,
            template.Name,
            template.Description,
            template.Version,
            template.IsActive,
            template.CreatedAt,
            template.CreatedBy,
            template.Steps
                .OrderBy(s => s.Order)
                .Select(s => new ProcessStepResponse(
                    s.Id,
                    s.DepartmentName,
                    s.Order,
                    s.StepType,
                    s.QaRequired,
                    s.Instructions,
                    s.NextStepId,
                    s.EstimatedDurationMinutes))
                .ToList());

        return response;
    }
}
