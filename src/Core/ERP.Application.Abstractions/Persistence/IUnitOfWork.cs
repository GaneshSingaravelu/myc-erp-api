namespace ERP.Application.Abstractions.Persistence;

/// <summary>
/// UNIT OF WORK — Coordinates saving multiple changes as a single database transaction.
///
/// WHAT IS UNIT OF WORK? (Interview Question!)
/// =============================================
/// Imagine creating a Job Card requires:
/// 1. Insert a JobCard record
/// 2. Update the WorkOrder's remaining quantity
/// 3. Insert an audit log entry
///
/// If step 2 fails, you want step 1 to be ROLLED BACK too.
/// Unit of Work ensures ALL changes succeed together, or NONE of them are saved.
///
/// HOW IT WORKS WITH EF CORE:
/// ===========================
/// EF Core's DbContext IS a Unit of Work internally.
/// When you call _context.SaveChangesAsync(), it saves ALL tracked changes in a single transaction.
///
/// Our IUnitOfWork interface wraps this:
///
///   // In a command handler:
///   await _jobCardRepo.AddAsync(newJobCard);          // tracked, not yet saved
///   _workOrderRepo.Update(workOrder);                  // tracked, not yet saved
///   await _unitOfWork.SaveChangesAsync(ct);            // NOW all changes are saved atomically
///
/// WHY A SEPARATE INTERFACE?
/// ==========================
/// 1. The handler doesn't need to know about DbContext
/// 2. In tests, you can mock IUnitOfWork (it just returns Task.CompletedTask)
/// 3. We can add cross-cutting logic (dispatch domain events, audit logging) in SaveChanges
///
/// DESIGN PATTERN: Unit of Work (Martin Fowler)
///
/// INTERVIEW TIP: "We use Unit of Work to ensure transactional consistency.
/// Multiple repository operations are batched and committed together.
/// Domain events are dispatched after successful commit."
/// </summary>
public interface IUnitOfWork : IDisposable
{
    /// <summary>
    /// Saves all changes made through repositories as a single transaction.
    /// Also dispatches domain events after successful save.
    /// Returns the number of entities affected.
    /// </summary>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Begins an explicit database transaction for complex operations.
    /// Use this when you need to coordinate across multiple SaveChanges calls.
    /// Usually SaveChangesAsync() alone is sufficient.
    /// </summary>
    Task BeginTransactionAsync(CancellationToken cancellationToken = default);

    /// <summary>Commits the current transaction</summary>
    Task CommitTransactionAsync(CancellationToken cancellationToken = default);

    /// <summary>Rolls back the current transaction</summary>
    Task RollbackTransactionAsync(CancellationToken cancellationToken = default);
}
