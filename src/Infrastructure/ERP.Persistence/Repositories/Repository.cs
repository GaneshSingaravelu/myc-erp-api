using System.Linq.Expressions;
using ERP.Application.Abstractions.Persistence;
using ERP.SharedKernel.Entities;
using Microsoft.EntityFrameworkCore;
using ERP.Persistence.Context;

namespace ERP.Persistence.Repositories;

/// <summary>
/// GENERIC REPOSITORY IMPLEMENTATION — Uses EF Core to talk to the database.
///
/// THIS IS WHERE THE ABSTRACTION MEETS REALITY:
/// =============================================
/// - IRepository&lt;T&gt; (in Application.Abstractions) → defines WHAT operations are available
/// - Repository&lt;T&gt; (HERE, in Persistence)          → defines HOW they work using EF Core
///
/// The Application layer only sees IRepository&lt;T&gt;.
/// It never knows about EF Core, DbContext, or SQL.
///
/// DESIGN PATTERN: Repository Pattern (implementation)
///
/// IMPORTANT INTERVIEW CONCEPT — DbSet&lt;T&gt;:
/// ==========================================
/// DbSet&lt;T&gt; represents a TABLE in the database.
/// _context.Set&lt;T&gt;() gets the DbSet for entity type T.
///
/// DbSet operations map to SQL:
/// - .AddAsync()              → INSERT
/// - .Update()                → UPDATE (on SaveChanges)
/// - .Remove()                → DELETE (on SaveChanges)
/// - .ToListAsync()           → SELECT * FROM table
/// - .Where(x => ...).ToListAsync() → SELECT * FROM table WHERE ...
/// - .FindAsync(id)           → SELECT * FROM table WHERE Id = @id
/// </summary>
public class Repository<T> : IRepository<T> where T : BaseEntity
{
    protected readonly AppDbContext Context;
    protected readonly DbSet<T> DbSet;

    public Repository(AppDbContext context)
    {
        Context = context;
        DbSet = context.Set<T>();
    }

    public async Task<T?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await DbSet.FindAsync([id], cancellationToken);
    }

    public async Task<IReadOnlyList<T>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await DbSet.ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<T>> FindAsync(
        Expression<Func<T, bool>> predicate,
        CancellationToken cancellationToken = default)
    {
        return await DbSet.Where(predicate).ToListAsync(cancellationToken);
    }

    public async Task<bool> AnyAsync(
        Expression<Func<T, bool>> predicate,
        CancellationToken cancellationToken = default)
    {
        return await DbSet.AnyAsync(predicate, cancellationToken);
    }

    public async Task<int> CountAsync(
        Expression<Func<T, bool>> predicate,
        CancellationToken cancellationToken = default)
    {
        return await DbSet.CountAsync(predicate, cancellationToken);
    }

    public async Task AddAsync(T entity, CancellationToken cancellationToken = default)
    {
        await DbSet.AddAsync(entity, cancellationToken);
    }

    public async Task AddRangeAsync(IEnumerable<T> entities, CancellationToken cancellationToken = default)
    {
        await DbSet.AddRangeAsync(entities, cancellationToken);
    }

    public void Update(T entity)
    {
        DbSet.Update(entity);
    }

    public void Remove(T entity)
    {
        DbSet.Remove(entity);
    }
}
