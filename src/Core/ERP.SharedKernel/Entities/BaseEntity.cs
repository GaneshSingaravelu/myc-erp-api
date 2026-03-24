using ERP.SharedKernel.DomainEvents;

namespace ERP.SharedKernel.Entities;

/// <summary>
/// BASE ENTITY — The foundation for ALL database entities in the system.
///
/// WHAT IS AN ENTITY? (Interview Question!)
/// =========================================
/// An Entity is an object that has a unique identity (Id) that persists over time.
/// Two entities are equal if they have the same Id, even if all other properties differ.
///
/// Example: Two employees named "John Smith" are DIFFERENT entities if they have
/// different Ids (Employee #101 vs Employee #102).
///
/// Compare with VALUE OBJECT: Two "Address" objects with the same street, city, zip
/// are considered EQUAL regardless of identity. See ValueObject.cs.
///
/// DESIGN PATTERN: Layer Supertype
/// ================================
/// Instead of repeating Id, CreatedAt, UpdatedAt in every entity,
/// we define them once in a base class. All entities inherit from this.
///
/// WHY Guid INSTEAD OF int FOR Id?
/// ================================
/// - Guid: Can be generated anywhere (client, server, database) without conflicts
/// - int: Requires database to generate (auto-increment), causes issues in distributed systems
/// - For ERP with multi-tenant, multi-plant: Guid prevents Id collisions across databases
/// </summary>
public abstract class BaseEntity
{
    /// <summary>
    /// Unique identifier. Generated as Guid.NewGuid() by default.
    /// This is the PRIMARY KEY in the database table.
    /// </summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>
    /// Domain events that occurred on this entity.
    /// These are dispatched AFTER the entity is saved to the database.
    /// See DomainEvents/IDomainEvent.cs for explanation.
    /// </summary>
    private readonly List<IDomainEvent> _domainEvents = [];
    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    public void AddDomainEvent(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);
    public void RemoveDomainEvent(IDomainEvent domainEvent) => _domainEvents.Remove(domainEvent);
    public void ClearDomainEvents() => _domainEvents.Clear();
}
