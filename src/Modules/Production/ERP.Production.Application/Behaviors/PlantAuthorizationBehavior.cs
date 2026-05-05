using ERP.Application.Abstractions.CQRS;
using ERP.Application.Abstractions.Identity;
using ERP.Application.Abstractions.Persistence;
using ERP.Production.Domain.Entities;
using ERP.SharedKernel.Results;
using Mediator;

namespace ERP.Production.Application.Behaviors;

/// <summary>
/// MediatR pipeline behavior that enforces plant-level authorization.
/// Only fires for commands implementing IPlantScopedRequest.
/// Requires the current user to have at least OPERATE access to the target plant.
/// </summary>
public sealed class PlantAuthorizationBehavior<TRequest, TResponse>
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull, IMessage
    where TResponse : Result
{
    private readonly ICurrentUserService _currentUser;
    private readonly IRepository<UserPlantAccess> _plantAccessRepo;

    public PlantAuthorizationBehavior(
        ICurrentUserService currentUser,
        IRepository<UserPlantAccess> plantAccessRepo)
    {
        _currentUser = currentUser;
        _plantAccessRepo = plantAccessRepo;
    }

    public async ValueTask<TResponse> Handle(
        TRequest request,
        MessageHandlerDelegate<TRequest, TResponse> next,
        CancellationToken cancellationToken)
    {
        if (request is not IPlantScopedRequest plantRequest)
            return await next(request, cancellationToken);

        if (!Guid.TryParse(_currentUser.UserId, out var userId))
            return Fail("You must be authenticated to perform this action.");

        var hasAccess = await _plantAccessRepo.AnyAsync(
            upa => upa.UserId == userId
                && upa.PlantId == plantRequest.PlantId
                && upa.IsActive
                && upa.AccessLevel != "VIEW",
            cancellationToken);

        if (!hasAccess)
            return Fail($"You do not have sufficient access to plant '{plantRequest.PlantId}'.");

        return await next(request, cancellationToken);
    }

    // Creates a TResponse (which is Result or Result<T>) carrying the Unauthorized error.
    // Uses the same approach as ValidationBehavior: reflection to call the static Failure factory.
    private static TResponse Fail(string message)
    {
        var error = Error.Unauthorized(message);
        var resultType = typeof(TResponse);

        // Result<T> case
        if (resultType.IsGenericType && resultType.GetGenericTypeDefinition() == typeof(Result<>))
        {
            var method = typeof(Result)
                .GetMethod(nameof(Result.Failure), [typeof(Error)])!
                .MakeGenericMethod(resultType.GetGenericArguments()[0]);
            return (TResponse)method.Invoke(null, [error])!;
        }

        // Result (non-generic) case
        return (TResponse)(object)Result.Failure(error);
    }
}
