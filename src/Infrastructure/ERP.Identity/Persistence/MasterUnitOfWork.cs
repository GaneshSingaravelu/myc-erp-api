using ERP.Identity.Abstractions.Persistence;
using ERP.Persistence.Context;

namespace ERP.Identity.Persistence;

/// <summary>
/// Thin wrapper over MasterDbContext.SaveChangesAsync() — implements <see cref="IMasterUnitOfWork"/>.
///
/// Why so small? Because EF Core's DbContext IS already a Unit of Work; we only need
/// the interface seam so services can depend on the abstraction (testability + intent).
/// </summary>
public sealed class MasterUnitOfWork : IMasterUnitOfWork
{
    private readonly MasterDbContext _context;

    public MasterUnitOfWork(MasterDbContext context) => _context = context;

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        => _context.SaveChangesAsync(cancellationToken);
}
