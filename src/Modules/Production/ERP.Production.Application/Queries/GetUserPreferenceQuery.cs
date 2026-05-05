using ERP.Application.Abstractions.CQRS;
using ERP.Application.Abstractions.Identity;
using ERP.Application.Abstractions.Persistence;
using ERP.Production.Domain.Entities;
using ERP.SharedKernel.Results;

namespace ERP.Production.Application.Queries;

/// <summary>QUERY: Get a single user preference by key.</summary>
public record GetUserPreferenceQuery(string Key) : IQuery<UserPreferenceResponse>;

/// <summary>QUERY: Get all preferences for the current user.</summary>
public record GetAllUserPreferencesQuery : IQuery<List<UserPreferenceResponse>>;

public record UserPreferenceResponse(string Key, string Value);

public class GetUserPreferenceHandler : IQueryHandler<GetUserPreferenceQuery, UserPreferenceResponse>
{
    private readonly IRepository<UserPreference> _repo;
    private readonly ICurrentUserService _currentUser;

    public GetUserPreferenceHandler(IRepository<UserPreference> repo, ICurrentUserService currentUser)
    {
        _repo = repo;
        _currentUser = currentUser;
    }

    public async ValueTask<Result<UserPreferenceResponse>> Handle(
        GetUserPreferenceQuery request, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(_currentUser.UserId, out var userId))
            return Result.Failure<UserPreferenceResponse>(Error.Unauthorized());

        var pref = (await _repo.FindAsync(
            p => p.UserId == userId && p.Key == request.Key,
            cancellationToken)).FirstOrDefault();

        if (pref is null)
            return Result.Failure<UserPreferenceResponse>(
                Error.NotFound("UserPreference", request.Key));

        return new UserPreferenceResponse(pref.Key, pref.Value);
    }
}

public class GetAllUserPreferencesHandler : IQueryHandler<GetAllUserPreferencesQuery, List<UserPreferenceResponse>>
{
    private readonly IRepository<UserPreference> _repo;
    private readonly ICurrentUserService _currentUser;

    public GetAllUserPreferencesHandler(IRepository<UserPreference> repo, ICurrentUserService currentUser)
    {
        _repo = repo;
        _currentUser = currentUser;
    }

    public async ValueTask<Result<List<UserPreferenceResponse>>> Handle(
        GetAllUserPreferencesQuery request, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(_currentUser.UserId, out var userId))
            return Result.Failure<List<UserPreferenceResponse>>(Error.Unauthorized());

        var prefs = await _repo.FindAsync(p => p.UserId == userId, cancellationToken);

        return prefs.Select(p => new UserPreferenceResponse(p.Key, p.Value))
                    .OrderBy(p => p.Key)
                    .ToList();
    }
}
