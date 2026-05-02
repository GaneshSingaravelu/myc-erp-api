namespace ERP.Application.Abstractions.CQRS;

/// <summary>
/// Marker interface for commands that target a specific plant.
/// PlantAuthorizationBehavior intercepts these and verifies the current user
/// has at least OPERATE-level access to the requested plant.
/// </summary>
public interface IPlantScopedRequest
{
    Guid PlantId { get; }
}
