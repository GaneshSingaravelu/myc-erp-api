using ERP.Application.Abstractions.CQRS;
using ERP.Application.Abstractions.Persistence;
using ERP.SharedKernel.Results;
using ERP.Workflow.Domain.Entities;

namespace ERP.Workflow.Application.Commands;

/// <summary>
/// COMMAND: Soft-delete a process template.
/// Since we use the soft delete pattern (via AuditableEntityInterceptor),
/// calling Remove() sets IsDeleted = true instead of actually deleting the record.
/// </summary>
public record DeleteProcessTemplateCommand(Guid Id) : ICommand;

public class DeleteProcessTemplateHandler : ICommandHandler<DeleteProcessTemplateCommand>
{
    private readonly IRepository<ProcessTemplate> _templateRepo;
    private readonly IUnitOfWork _unitOfWork;

    public DeleteProcessTemplateHandler(IRepository<ProcessTemplate> templateRepo, IUnitOfWork unitOfWork)
    {
        _templateRepo = templateRepo;
        _unitOfWork = unitOfWork;
    }

    public async ValueTask<Result> Handle(DeleteProcessTemplateCommand request, CancellationToken cancellationToken)
    {
        var template = await _templateRepo.GetByIdAsync(request.Id, cancellationToken);
        if (template is null)
            return Result.Failure(Error.NotFound("ProcessTemplate", request.Id));

        _templateRepo.Remove(template);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
