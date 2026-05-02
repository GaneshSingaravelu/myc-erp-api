using ERP.Application.Abstractions.CQRS;
using ERP.Application.Abstractions.Persistence;
using ERP.Production.Domain.Entities;
using ERP.SharedKernel.Results;

namespace ERP.Production.Application.Commands;

// ── Create ─────────────────────────────────────────────────────────────────

public record CreatePlantCommand(string Code, string Name, string? Location) : ICommand<Guid>;

public class CreatePlantHandler : ICommandHandler<CreatePlantCommand, Guid>
{
    private readonly IRepository<Plant> _repo;
    private readonly IUnitOfWork _unitOfWork;

    public CreatePlantHandler(IRepository<Plant> repo, IUnitOfWork unitOfWork)
    {
        _repo = repo;
        _unitOfWork = unitOfWork;
    }

    public async Task<Result<Guid>> Handle(CreatePlantCommand request, CancellationToken cancellationToken)
    {
        var exists = await _repo.AnyAsync(p => p.Code == request.Code, cancellationToken);
        if (exists)
            return Result.Failure<Guid>(
                Error.Conflict("Plant.Duplicate", $"A plant with code '{request.Code}' already exists."));

        var plant = new Plant
        {
            Code = request.Code.ToUpperInvariant(),
            Name = request.Name,
            Location = request.Location,
            CreatedAt = DateTime.UtcNow
        };

        await _repo.AddAsync(plant, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return plant.Id;
    }
}

// ── Update ─────────────────────────────────────────────────────────────────

public record UpdatePlantCommand(Guid Id, string Name, string? Location, bool IsActive) : ICommand;

public class UpdatePlantHandler : ICommandHandler<UpdatePlantCommand>
{
    private readonly IRepository<Plant> _repo;
    private readonly IUnitOfWork _unitOfWork;

    public UpdatePlantHandler(IRepository<Plant> repo, IUnitOfWork unitOfWork)
    {
        _repo = repo;
        _unitOfWork = unitOfWork;
    }

    public async Task<Result> Handle(UpdatePlantCommand request, CancellationToken cancellationToken)
    {
        var plant = await _repo.GetByIdAsync(request.Id, cancellationToken);
        if (plant is null)
            return Result.Failure(Error.NotFound("Plant", request.Id));

        plant.Name = request.Name;
        plant.Location = request.Location;
        plant.IsActive = request.IsActive;

        _repo.Update(plant);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}

// ── Delete ─────────────────────────────────────────────────────────────────

public record DeletePlantCommand(Guid Id) : ICommand;

public class DeletePlantHandler : ICommandHandler<DeletePlantCommand>
{
    private readonly IRepository<Plant> _repo;
    private readonly IUnitOfWork _unitOfWork;

    public DeletePlantHandler(IRepository<Plant> repo, IUnitOfWork unitOfWork)
    {
        _repo = repo;
        _unitOfWork = unitOfWork;
    }

    public async Task<Result> Handle(DeletePlantCommand request, CancellationToken cancellationToken)
    {
        var plant = await _repo.GetByIdAsync(request.Id, cancellationToken);
        if (plant is null)
            return Result.Failure(Error.NotFound("Plant", request.Id));

        _repo.Remove(plant);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
