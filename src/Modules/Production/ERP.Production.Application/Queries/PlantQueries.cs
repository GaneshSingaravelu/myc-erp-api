using ERP.Application.Abstractions.CQRS;
using ERP.Application.Abstractions.Persistence;
using ERP.Production.Domain.Entities;
using ERP.SharedKernel.Results;

namespace ERP.Production.Application.Queries;

public record PlantResponse(Guid Id, string Code, string Name, string? Location, bool IsActive, DateTime CreatedAt);

// ── List ───────────────────────────────────────────────────────────────────

public record GetPlantsQuery(bool ActiveOnly = true) : IQuery<List<PlantResponse>>;

public class GetPlantsHandler : IQueryHandler<GetPlantsQuery, List<PlantResponse>>
{
    private readonly IRepository<Plant> _repo;

    public GetPlantsHandler(IRepository<Plant> repo) => _repo = repo;

    public async Task<Result<List<PlantResponse>>> Handle(
        GetPlantsQuery request, CancellationToken cancellationToken)
    {
        var plants = request.ActiveOnly
            ? await _repo.FindAsync(p => p.IsActive, cancellationToken)
            : await _repo.GetAllAsync(cancellationToken);

        return plants
            .OrderBy(p => p.Code)
            .Select(p => new PlantResponse(p.Id, p.Code, p.Name, p.Location, p.IsActive, p.CreatedAt))
            .ToList();
    }
}

// ── Get by ID ──────────────────────────────────────────────────────────────

public record GetPlantByIdQuery(Guid Id) : IQuery<PlantResponse>;

public class GetPlantByIdHandler : IQueryHandler<GetPlantByIdQuery, PlantResponse>
{
    private readonly IRepository<Plant> _repo;

    public GetPlantByIdHandler(IRepository<Plant> repo) => _repo = repo;

    public async Task<Result<PlantResponse>> Handle(
        GetPlantByIdQuery request, CancellationToken cancellationToken)
    {
        var plant = await _repo.GetByIdAsync(request.Id, cancellationToken);
        if (plant is null)
            return Result.Failure<PlantResponse>(Error.NotFound("Plant", request.Id));

        return new PlantResponse(plant.Id, plant.Code, plant.Name, plant.Location, plant.IsActive, plant.CreatedAt);
    }
}
