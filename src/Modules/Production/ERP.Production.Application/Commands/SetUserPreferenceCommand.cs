using ERP.Application.Abstractions.CQRS;
using ERP.Application.Abstractions.Identity;
using ERP.Application.Abstractions.Persistence;
using ERP.Production.Domain.Entities;
using ERP.SharedKernel.Results;

namespace ERP.Production.Application.Commands;

/// <summary>COMMAND: Create or update a user preference key-value pair.</summary>
public record SetUserPreferenceCommand(string Key, string Value) : ICommand;

public class SetUserPreferenceHandler : ICommandHandler<SetUserPreferenceCommand>
{
    private readonly IRepository<UserPreference> _repo;
    private readonly ICurrentUserService _currentUser;
    private readonly IUnitOfWork _unitOfWork;

    public SetUserPreferenceHandler(
        IRepository<UserPreference> repo,
        ICurrentUserService currentUser,
        IUnitOfWork unitOfWork)
    {
        _repo = repo;
        _currentUser = currentUser;
        _unitOfWork = unitOfWork;
    }

    public async Task<Result> Handle(SetUserPreferenceCommand request, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(_currentUser.UserId, out var userId))
            return Result.Failure(Error.Unauthorized("User identity is missing."));

        var existing = (await _repo.FindAsync(
            p => p.UserId == userId && p.Key == request.Key,
            cancellationToken)).FirstOrDefault();

        if (existing is not null)
        {
            existing.Value = request.Value;
            _repo.Update(existing);
        }
        else
        {
            await _repo.AddAsync(new UserPreference
            {
                UserId = userId,
                Key = request.Key,
                Value = request.Value
            }, cancellationToken);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
