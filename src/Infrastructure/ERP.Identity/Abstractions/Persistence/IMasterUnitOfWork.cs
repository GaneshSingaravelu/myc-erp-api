namespace ERP.Identity.Abstractions.Persistence;

/// <summary>
/// Unit of Work for the Master DB (identity, tenants, sessions, audit).
///
/// Why a SEPARATE UoW (not the existing IUnitOfWork)?
/// ===================================================
/// IUnitOfWork in ERP.Application.Abstractions wraps AppDbContext (per-tenant DB).
/// MasterDbContext is a different DbContext managing identity / tenant metadata.
/// A distinct interface keeps the two save scopes from being accidentally swapped
/// at the DI seam and makes call sites self-documenting about WHICH DB they target.
///
/// Use it from any Master-DB service (Auth, User, Role, Tenant management) to
/// commit changes staged through the corresponding repositories in a single
/// transactional Save.
/// </summary>
public interface IMasterUnitOfWork
{
    /// <summary>
    /// Persists all pending changes tracked by MasterDbContext as a single transaction.
    /// Returns the number of rows affected.
    /// </summary>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
