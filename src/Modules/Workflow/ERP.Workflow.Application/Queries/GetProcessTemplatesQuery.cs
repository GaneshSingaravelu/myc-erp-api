using ERP.Application.Abstractions.CQRS;
using ERP.Application.Abstractions.Persistence;
using ERP.SharedKernel.Results;
using ERP.Workflow.Application.DTOs;
using ERP.Workflow.Domain.Entities;

namespace ERP.Workflow.Application.Queries;

/// <summary>QUERY: Get all active process templates (list view, no steps).</summary>
public record GetProcessTemplatesQuery(bool IncludeInactive = false)
    : IQuery<List<ProcessTemplateListResponse>>;

public class GetProcessTemplatesHandler
    : IQueryHandler<GetProcessTemplatesQuery, List<ProcessTemplateListResponse>>
{
    private readonly IRepository<ProcessTemplate> _templateRepo;

    public GetProcessTemplatesHandler(IRepository<ProcessTemplate> templateRepo)
    {
        _templateRepo = templateRepo;
    }

    public async Task<Result<List<ProcessTemplateListResponse>>> Handle(
        GetProcessTemplatesQuery request, CancellationToken cancellationToken)
    {
        var templates = request.IncludeInactive
            ? await _templateRepo.GetAllAsync(cancellationToken)
            : await _templateRepo.FindAsync(t => t.IsActive, cancellationToken);

        var result = templates
            .Select(t => new ProcessTemplateListResponse(
                t.Id,
                t.Name,
                t.Description,
                t.Version,
                t.IsActive,
                t.Steps.Count,
                t.CreatedAt))
            .OrderBy(t => t.Name)
            .ThenByDescending(t => t.Version)
            .ToList();

        return result;
    }
}
