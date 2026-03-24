using System.Linq.Expressions;
using ERP.SharedKernel.Entities;

namespace ERP.Application.Abstractions.Persistence;

/// <summary>
/// REPOSITORY PATTERN — Abstracts database access behind an interface.
///
/// WHAT IS THE REPOSITORY PATTERN? (Very Common Interview Question!)
/// ==================================================================
/// A Repository acts as a "collection" of domain objects.
/// Your business logic works with the repository as if it were a list in memory.
/// The repository hides HOW data is stored (SQL Server, PostgreSQL, etc.).
///
/// WHY USE A REPOSITORY?
/// ======================
/// 1. TESTABILITY: In unit tests, you can mock IRepository. No real database needed.
/// 2. SEPARATION: Business logic doesn't know about EF Core, SQL, or connection strings.
/// 3. SINGLE RESPONSIBILITY: Repository handles data access. Handler handles business logic.
/// 4. SWAPPABLE: Switch from SQL Server to PostgreSQL without changing business logic.
///
/// EXAMPLE:
/// ========
/// // In your command handler (Application layer):
/// public class CreateJobCardHandler : ICommandHandler&lt;CreateJobCardCommand, Guid&gt;
/// {
///     private readonly IRepository&lt;JobCard&gt; _repo;  // ← Interface, not implementation
///
///     public async Task&lt;Result&lt;Guid&gt;&gt; Handle(CreateJobCardCommand cmd, CancellationToken ct)
///     {
///         var jobCard = new JobCard(cmd.WorkOrderId, cmd.Quantity);
///         await _repo.AddAsync(jobCard, ct);    // ← Doesn't know about EF Core
///         return jobCard.Id;
///     }
/// }
///
/// // The actual implementation (in ERP.Persistence) uses EF Core:
/// public class Repository&lt;T&gt; : IRepository&lt;T&gt;
/// {
///     private readonly AppDbContext _context;    // ← EF Core is HERE, not in the handler
///     public async Task AddAsync(T entity, CancellationToken ct)
///         => await _context.Set&lt;T&gt;().AddAsync(entity, ct);
/// }
///
/// DESIGN PATTERN: Repository Pattern (Martin Fowler, Domain-Driven Design)
///
/// INTERVIEW TIP: "We use the Repository pattern to abstract data access.
/// Our business logic depends on IRepository&lt;T&gt; (interface), not on EF Core directly.
/// This follows the Dependency Inversion Principle — depend on abstractions, not implementations."
/// </summary>
public interface IRepository<T> where T : BaseEntity
{
    /// <summary>Get an entity by its unique Id</summary>
    Task<T?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Get all entities (use with caution on large tables — prefer pagination)</summary>
    Task<IReadOnlyList<T>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Find entities matching a condition (e.g., x => x.Status == "Active")</summary>
    Task<IReadOnlyList<T>> FindAsync(Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default);

    /// <summary>Check if any entity matches the condition</summary>
    Task<bool> AnyAsync(Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default);

    /// <summary>Count entities matching the condition</summary>
    Task<int> CountAsync(Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default);

    /// <summary>Add a new entity to the database</summary>
    Task AddAsync(T entity, CancellationToken cancellationToken = default);

    /// <summary>Add multiple entities at once (batch insert)</summary>
    Task AddRangeAsync(IEnumerable<T> entities, CancellationToken cancellationToken = default);

    /// <summary>Mark an entity as modified</summary>
    void Update(T entity);

    /// <summary>Mark an entity for deletion</summary>
    void Remove(T entity);
}
