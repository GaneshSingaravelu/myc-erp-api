using ERP.Application.Abstractions.CQRS;
using ERP.Application.Abstractions.Persistence;
using ERP.SharedKernel.Results;
using ERP.Workflow.Domain.Entities;

namespace ERP.Workflow.Application.Commands;

/// <summary>COMMAND: Update a process template's name and description.</summary>
public record UpdateProcessTemplateCommand(
    Guid Id,
    string Name,
    string? Description) : ICommand;

public class UpdateProcessTemplateHandler : ICommandHandler<UpdateProcessTemplateCommand>
{
    private readonly IRepository<ProcessTemplate> _templateRepo;
    private readonly IUnitOfWork _unitOfWork;

    public UpdateProcessTemplateHandler(IRepository<ProcessTemplate> templateRepo, IUnitOfWork unitOfWork)
    {
        _templateRepo = templateRepo;
        _unitOfWork = unitOfWork;
    }

    public async ValueTask<Result> Handle(UpdateProcessTemplateCommand request, CancellationToken cancellationToken)
    {
        var template = await _templateRepo.GetByIdAsync(request.Id, cancellationToken);
        if (template is null)
            return Result.Failure(Error.NotFound("ProcessTemplate", request.Id));

        template.Update(request.Name, request.Description);
        _templateRepo.Update(template);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
