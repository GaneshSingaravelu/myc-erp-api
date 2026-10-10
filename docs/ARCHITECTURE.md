# ERP API Architecture Guide

> A comprehensive guide to ASP.NET Core concepts as implemented in this ERP project.
> Written for developers new to .NET who are preparing for technical interviews.
> **.NET 8 | Clean Architecture | Modular Monolith | Multi-Tenant | CQRS**

---

## Table of Contents

1. [Project Structure](#1-project-structure)
2. [Dependency Diagram](#2-dependency-diagram)
3. [HTTP Request Lifecycle](#3-http-request-lifecycle)
4. [Clean Architecture](#4-clean-architecture)
5. [Modular Monolith](#5-modular-monolith)
6. [DbContext](#6-dbcontext)
7. [Entity Framework Core](#7-entity-framework-core)
8. [Repository Pattern](#8-repository-pattern)
9. [Unit of Work](#9-unit-of-work)
10. [Dependency Injection (DI)](#10-dependency-injection-di)
11. [DI Lifetimes: Singleton vs Scoped vs Transient](#11-di-lifetimes-singleton-vs-scoped-vs-transient)
12. [Middleware](#12-middleware)
13. [JWT Authentication](#13-jwt-authentication)
14. [CQRS with MediatR](#14-cqrs-with-mediatr)
15. [Caching](#15-caching)
16. [Multi-Tenancy](#16-multi-tenancy)
17. [SignalR](#17-signalr)
18. [API Versioning](#18-api-versioning)
19. [Health Checks](#19-health-checks)
20. [Serilog / Structured Logging](#20-serilog--structured-logging)
21. [CORS](#21-cors)
22. [Global Exception Handling](#22-global-exception-handling)
23. [Multi-Database Provider](#23-multi-database-provider)
24. [Quick Reference Table](#24-quick-reference-table)

---

## 1. Project Structure

```
erp_api/
|
+-- src/
|   |
|   +-- Core/                                    # Inner layers (no external dependencies)
|   |   +-- ERP.SharedKernel/                    # Base entities, value objects, domain events, Result type
|   |   +-- ERP.Application.Abstractions/        # CQRS interfaces, IRepository, IUnitOfWork, pipeline behaviors
|   |
|   +-- Modules/                                 # Business feature modules (Modular Monolith)
|   |   +-- Workflow/
|   |   |   +-- ERP.Workflow.Domain/             # Workflow entities, value objects, domain events
|   |   |   +-- ERP.Workflow.Application/        # Workflow commands, queries, handlers
|   |   |   +-- ERP.Workflow.Infrastructure/     # Workflow EF configs, module-specific services
|   |   |   +-- ERP.Workflow.Api/                # Workflow controllers
|   |   |
|   |   +-- Production/
|   |       +-- ERP.Production.Domain/           # Production entities (JobCard, WorkOrder)
|   |       +-- ERP.Production.Application/      # Production commands, queries, handlers
|   |       +-- ERP.Production.Infrastructure/   # Production EF configs, module-specific services
|   |       +-- ERP.Production.Api/              # Production controllers
|   |
|   +-- Infrastructure/                          # Outer layer (frameworks, I/O, databases)
|   |   +-- ERP.Persistence/                     # AppDbContext, Repository<T>, UnitOfWork, interceptors
|   |   +-- ERP.Persistence.SqlServer/           # SQL Server provider config + migrations
|   |   +-- ERP.Persistence.PostgreSql/          # PostgreSQL provider config + migrations
|   |   +-- ERP.Infrastructure/                  # Multi-tenancy, caching, current user
|   |   +-- ERP.Identity/                        # JWT token service, authentication config
|   |   +-- ERP.Notifications/                   # SignalR hubs, notification service
|   |
|   +-- Host/
|       +-- ERP.Api/                             # Composition Root: Program.cs, middleware, Swagger
|
+-- tools/
|   +-- ERP.DbMigrator/                          # Standalone tool for running DB migrations
|   +-- ERP.ParityCheck/                         # SQL Server/PostgreSQL parity harness (--self-test offline; --live gated)
|
+-- tests/
    +-- ERP.UnitTests/                           # Unit tests
    +-- ERP.Architecture.Tests/                  # Architecture rule enforcement (NetArchTest)
```

**Key Principle:** Each project has a single responsibility. The `Host/ERP.Api` project is the only one that references all other projects -- it is the **Composition Root**.

---

## 2. Dependency Diagram

Dependencies flow **inward** -- outer layers depend on inner layers, never the reverse.

```
                         +--------------------+
                         |     ERP.Api        |  (Host / Composition Root)
                         | Program.cs         |  References EVERYTHING
                         +--------+-----------+
                                  |
          +-----------+-----------+-----------+-----------+
          |           |           |           |           |
          v           v           v           v           v
   +------------+ +----------+ +----------+ +---------+ +---------------+
   | ERP.       | | ERP.     | | ERP.     | | ERP.    | | ERP.          |
   | Persistence| | Infra-   | | Identity | | Notifi- | | Persistence.  |
   | (Repos,UoW)| | structure| | (JWT)    | | cations | | SqlServer/    |
   +------+-----+ +-----+---+ +----+-----+ | (SignalR)| | PostgreSql    |
          |              |          |        +---------+ +-------+-------+
          |              |          |                             |
          +---------+----+----------+-----------------------------+
                    |
                    v
          +-------------------+
          | ERP.Application.  |  (CQRS interfaces, IRepository, pipeline behaviors)
          | Abstractions      |
          +---------+---------+
                    |
                    v
          +-------------------+
          | ERP.SharedKernel  |  (BaseEntity, AuditableEntity, Result, DomainEvents)
          +-------------------+

   Modules (Workflow, Production) follow the same layering internally:
     Module.Api --> Module.Application --> Module.Domain --> SharedKernel
     Module.Infrastructure --> Module.Application
```

**The Dependency Rule:** Source code dependencies must point inward. Nothing in an inner layer can know about an outer layer. `SharedKernel` knows nothing about EF Core, ASP.NET, or SQL Server.

---

## 3. HTTP Request Lifecycle

Every HTTP request flows through the middleware pipeline from top to bottom, then the response flows back up.

```
  Angular (http://localhost:4200)
     |
     |  POST /api/v1/jobcards  +  Headers: Authorization: Bearer <jwt>, X-Tenant-Id: abc-mfg
     |
     v
+------------------------------------------------------------+
| 1. GlobalExceptionMiddleware                               |  Wraps entire pipeline in try/catch
|    src/Host/ERP.Api/Middleware/GlobalExceptionMiddleware.cs |  Maps exceptions to JSON errors
+------------------------------------------------------------+
     |
     v
+------------------------------------------------------------+
| 2. HTTPS Redirection                                       |  Redirects HTTP to HTTPS
+------------------------------------------------------------+
     |
     v
+------------------------------------------------------------+
| 3. CORS Middleware                                          |  Adds Access-Control-Allow-Origin
|    Configured in Program.cs (AllowAngular policy)          |  for Angular cross-origin requests
+------------------------------------------------------------+
     |
     v
+------------------------------------------------------------+
| 4. Authentication Middleware                                |  Reads "Authorization: Bearer <jwt>"
|    Configured in IdentityServiceExtensions.cs              |  Validates token signature/expiry
|                                                            |  Sets HttpContext.User with claims
+------------------------------------------------------------+
     |
     v
+------------------------------------------------------------+
| 5. Authorization Middleware                                 |  Checks [Authorize] attributes
|                                                            |  Returns 401/403 if denied
+------------------------------------------------------------+
     |
     v
+------------------------------------------------------------+
| 6. TenantMiddleware                                        |  Reads X-Tenant-Id header
|    src/Infrastructure/ERP.Infrastructure/Tenant/           |  Looks up tenant (with caching)
|    TenantMiddleware.cs                                     |  Stores TenantInfo in HttpContext
+------------------------------------------------------------+
     |
     v
+------------------------------------------------------------+
| 7. Serilog Request Logging                                 |  Logs: HTTP POST /api/v1/jobcards
|                                                            |  responded 201 in 42.3ms
+------------------------------------------------------------+
     |
     v
+------------------------------------------------------------+
| 8. Routing + Controller Action                             |  Routes to JobCardsController.Create()
|    Controller calls _mediator.Send(command)                |
+------------------------------------------------------------+
     |
     v
+------------------------------------------------------------+
| 9. MediatR Pipeline                                        |
|    [ValidationBehavior] -> validate command with FluentVal  |
|    [LoggingBehavior]    -> log request + duration           |
|    [Handler]            -> actual business logic            |
+------------------------------------------------------------+
     |
     v
+------------------------------------------------------------+
| 10. Repository + DbContext                                  |
|     Repository.AddAsync() -> tracks entity                  |
|     UnitOfWork.SaveChangesAsync() -> generates SQL          |
|       -> AuditableEntityInterceptor fills CreatedAt/By      |
|       -> SQL INSERT executed on tenant's database           |
|       -> DomainEventDispatcherInterceptor publishes events  |
+------------------------------------------------------------+
     |
     v
  Response flows back up through all middleware layers
  Angular receives: { "id": "3fa85f64-..." }
```

---

## 4. Clean Architecture

### What is it?

Clean Architecture (Robert C. Martin / "Uncle Bob") organizes code into concentric layers where **dependencies point inward**. Think of it like an onion: the center is pure business logic with zero knowledge of databases, web frameworks, or UI. Each outer layer adds one more concern (data access, API, etc.).

**Analogy:** A restaurant kitchen. The head chef (Domain) creates recipes without caring if orders arrive by phone, walk-in, or app. The waiter (API layer) takes orders. The supplier (Infrastructure) delivers ingredients. The chef never calls the supplier directly -- the restaurant manager (Application layer) coordinates.

### The 4 Layers in Our Project

| Layer | Project(s) | What it contains | Rules |
|-------|-----------|------------------|-------|
| **Domain** | `ERP.SharedKernel`, `*.Domain` | Entities, value objects, domain events, business rules | Zero external dependencies. Pure C#. |
| **Application** | `ERP.Application.Abstractions`, `*.Application` | CQRS commands/queries, interfaces (IRepository, IUnitOfWork), validation | References Domain only. No EF Core, no ASP.NET. |
| **Infrastructure** | `ERP.Persistence`, `ERP.Infrastructure`, `ERP.Identity`, `ERP.Notifications` | EF Core, SQL Server, JWT, SignalR, caching implementations | Implements Application interfaces. References external packages. |
| **Presentation** | `ERP.Api`, `*.Api` | Controllers, middleware, Program.cs | Thin layer. Delegates to Application via MediatR. |

### How it works in our project

The Dependency Rule is enforced by project references. `ERP.Application.Abstractions` only references `ERP.SharedKernel`. It defines `IRepository<T>` but never mentions `DbContext`. The implementation lives in `ERP.Persistence.Repositories.Repository<T>`, which references EF Core.

```
// Application layer defines the contract (no EF Core reference):
// File: src/Core/ERP.Application.Abstractions/Persistence/IRepository.cs
public interface IRepository<T> where T : BaseEntity
{
    Task<T?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task AddAsync(T entity, CancellationToken cancellationToken = default);
    // ...
}

// Infrastructure layer provides the implementation (uses EF Core):
// File: src/Infrastructure/ERP.Persistence/Repositories/Repository.cs
public class Repository<T> : IRepository<T> where T : BaseEntity
{
    protected readonly AppDbContext Context;
    protected readonly DbSet<T> DbSet;
    // ...
}
```

### Interview Q&A

**Q: What is Clean Architecture and why use it?**
A: Clean Architecture separates code into layers where dependencies point inward. The domain/business logic has no dependency on frameworks or databases. This makes the core logic testable in isolation, the database swappable (we switched between SQL Server and PostgreSQL without changing business logic), and the code maintainable because changes to the UI or database do not ripple into business rules.

**Q: What is the Dependency Rule?**
A: Source code dependencies must only point inward. An inner layer must never reference an outer layer. The Domain layer does not know EF Core exists. The Application layer defines interfaces (IRepository), and the Infrastructure layer implements them. This is achieved through Dependency Inversion -- high-level modules define abstractions, low-level modules implement them.

**Q: How do you enforce architectural rules?**
A: Through project references in .csproj files (a project physically cannot use types from a project it does not reference) and architecture tests using NetArchTest in `tests/ERP.Architecture.Tests/`.

---

## 5. Modular Monolith

### What is it?

A Modular Monolith is a single deployable application (monolith) that is internally organized into independent **modules**. Each module owns its domain, application logic, infrastructure, and API surface. Think of it as apartments in a building: each apartment is self-contained with its own kitchen and bathroom, but they all share the same building (deployment unit), plumbing (shared infrastructure), and foundation (shared kernel).

**Analogy:** A shopping mall. Each store (module) operates independently with its own staff, inventory, and rules. But they all share the mall's security system, parking, and utilities. If a store grows huge, you can move it to a standalone building (microservice) without redesigning the others.

### How modules are organized in our project

```
src/Modules/
+-- Workflow/                              # Workflow Module
|   +-- ERP.Workflow.Domain/               # Entities: ApprovalStep, WorkflowTemplate
|   +-- ERP.Workflow.Application/          # Commands: CreateWorkflow, ApproveStep
|   +-- ERP.Workflow.Infrastructure/       # EF configs, workflow-specific services
|   +-- ERP.Workflow.Api/                  # WorkflowController endpoints
|
+-- Production/                            # Production Module
    +-- ERP.Production.Domain/             # Entities: JobCard, WorkOrder, BOM
    +-- ERP.Production.Application/        # Commands: CreateJobCard, CompleteJobCard
    +-- ERP.Production.Infrastructure/     # EF configs, production-specific services
    +-- ERP.Production.Api/                # JobCardsController endpoints
```

Each module follows Clean Architecture internally (Domain -> Application -> Infrastructure -> Api). Modules share `ERP.SharedKernel` for base types and `ERP.Application.Abstractions` for CQRS/repository contracts, but they do not reference each other directly. Cross-module communication (when needed) uses domain events via MediatR.

### Interview Q&A

**Q: What is a Modular Monolith and how does it compare to microservices?**
A: A Modular Monolith is a single deployable unit with well-defined internal module boundaries. Unlike microservices, all modules run in the same process and share the same database infrastructure, so there is no network overhead between modules. Unlike a traditional monolith, each module has clear boundaries and can be extracted into a microservice later. It gives you the simplicity of a monolith (single deployment, no distributed transactions) with the organizational benefits of microservices (team autonomy, clear ownership).

**Q: How do modules communicate?**
A: Modules communicate through domain events published via MediatR. When a JobCard is completed in the Production module, it raises a `JobCardCompletedEvent`. A handler in the Workflow module can subscribe to this event and trigger the next approval step. This keeps modules decoupled -- they share events, not direct references.

---

## 6. DbContext

### What is it?

`DbContext` is Entity Framework Core's main class. Think of it as a **session with the database**. It is the bridge between your C# objects and database tables. When you modify a C# object that the DbContext tracks, it knows what changed and can generate the correct SQL to persist those changes.

**Analogy:** A shopping cart at a grocery store. You add items (new entities), remove items (delete entities), and swap items (update entities). Nothing actually happens at the register (database) until you "check out" (`SaveChangesAsync()`). At checkout, all your changes are processed in one transaction.

### How it works in our project

Our `AppDbContext` lives in `src/Infrastructure/ERP.Persistence/Context/AppDbContext.cs`. It does three things:

1. **Maps C# classes to database tables** via `DbSet<T>` properties and `IEntityTypeConfiguration<T>` classes
2. **Tracks changes** to entity objects in memory
3. **Saves changes** via `SaveChangesAsync()`, generating INSERT/UPDATE/DELETE SQL

```csharp
// File: src/Infrastructure/ERP.Persistence/Context/AppDbContext.cs
public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        // Auto-discover and apply all IEntityTypeConfiguration<T> in this assembly
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        // Global query filter: automatically exclude soft-deleted records
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (typeof(AuditableEntity).IsAssignableFrom(entityType.ClrType))
            {
                modelBuilder.Entity(entityType.ClrType)
                    .HasQueryFilter(CreateSoftDeleteFilter(entityType.ClrType));
            }
        }
    }
}
```

**Lifecycle:** DbContext is registered as **Scoped** (one instance per HTTP request). Request starts, a new DbContext is created. The handler queries and modifies data. `SaveChangesAsync()` commits everything in one transaction. Request ends, DbContext is disposed.

**Multi-tenancy twist:** Our DbContext does not receive a fixed connection string at startup. Instead, `TenantDbContextFactory` creates it per-request with the connection string of the current tenant's database. The DbContext itself has no idea which database it is connected to.

### Interview Q&A

**Q: What is DbContext and what is its lifecycle?**
A: DbContext is the main EF Core class that represents a session with the database. It maps C# objects to tables, tracks changes, and commits them via SaveChangesAsync(). It is registered as Scoped in DI, meaning one instance per HTTP request. This is important because Singleton would cause memory leaks and thread-safety issues (DbContext is not thread-safe), while Transient would break change tracking across multiple repository calls in the same request.

**Q: Why is DbContext Scoped and not Singleton?**
A: DbContext is not thread-safe, so sharing a single instance across concurrent requests would cause data corruption. It also accumulates tracked entities in memory, so a Singleton DbContext would be a memory leak. Scoped gives us one instance per request -- perfect for the Unit of Work pattern where all changes in a request are saved together, and the context is disposed when the request ends.

**Q: What is a global query filter?**
A: A global query filter is a predicate applied automatically to all LINQ queries for a given entity type. Our AppDbContext applies `entity => !entity.IsDeleted` to every entity inheriting AuditableEntity. This means deleted records are automatically excluded from every query without developers remembering to add `.Where(x => !x.IsDeleted)`. You can bypass it with `.IgnoreQueryFilters()` when you need deleted records.

---

## 7. Entity Framework Core

### What is it?

Entity Framework Core (EF Core) is an **Object-Relational Mapper (ORM)**. It translates between the object-oriented world (C# classes) and the relational world (SQL database tables). Instead of writing raw SQL, you work with C# objects and LINQ, and EF Core generates the SQL for you.

**Analogy:** A translator at the United Nations. You speak C# (your native language), the database speaks SQL. EF Core sits between you, translating your C# LINQ expressions into SQL statements and translating SQL result sets back into C# objects.

### How it works in our project

**Entity Mapping (Fluent API):** We use `IEntityTypeConfiguration<T>` classes for entity-to-table mapping rather than data annotations. These are auto-discovered by `ApplyConfigurationsFromAssembly()` in AppDbContext.

**Migrations:** Each database provider has its own migration assembly:
- SQL Server migrations: `src/Infrastructure/ERP.Persistence.SqlServer/`
- PostgreSQL migrations: `src/Infrastructure/ERP.Persistence.PostgreSql/`

**DbSet operations map to SQL:**
```
DbSet.AddAsync(entity)              -->  INSERT INTO table ...
DbSet.Update(entity)                -->  UPDATE table SET ... WHERE Id = @id
DbSet.Remove(entity)                -->  DELETE FROM table WHERE Id = @id
DbSet.Where(x => x.Status == "Active").ToListAsync()
                                     -->  SELECT * FROM table WHERE Status = 'Active'
DbSet.FindAsync(id)                 -->  SELECT * FROM table WHERE Id = @id
```

**Interceptors:** EF Core interceptors hook into the SaveChanges pipeline:
- `AuditableEntityInterceptor` -- auto-fills CreatedAt/By, ModifiedAt/By, and converts hard deletes to soft deletes
- `DomainEventDispatcherInterceptor` -- publishes domain events via MediatR after a successful save

```csharp
// File: src/Infrastructure/ERP.Persistence/Interceptors/AuditableEntityInterceptor.cs
// Runs BEFORE SaveChanges -- fills audit fields automatically
foreach (var entry in context.ChangeTracker.Entries<AuditableEntity>())
{
    switch (entry.State)
    {
        case EntityState.Added:
            entry.Entity.CreatedAt = now;
            entry.Entity.CreatedBy = userId;
            break;
        case EntityState.Modified:
            entry.Entity.LastModifiedAt = now;
            entry.Entity.LastModifiedBy = userId;
            break;
        case EntityState.Deleted:
            // Convert hard delete to soft delete
            entry.State = EntityState.Modified;
            entry.Entity.IsDeleted = true;
            break;
    }
}
```

### Fluent API vs Data Annotations

| Aspect | Fluent API (our approach) | Data Annotations |
|--------|--------------------------|-----------------|
| Location | Separate configuration class | Attributes on entity properties |
| Complexity | Can express everything EF Core supports | Limited subset |
| Domain purity | Entity class stays clean (no EF attributes) | Entity class polluted with DB concerns |
| Example | `builder.Property(x => x.Name).HasMaxLength(100);` | `[MaxLength(100)] public string Name` |

We chose Fluent API because it keeps domain entities free from infrastructure concerns (Clean Architecture rule).

### Interview Q&A

**Q: What is an ORM and why use EF Core?**
A: An ORM (Object-Relational Mapper) maps between C# objects and database tables. EF Core lets us work with LINQ and C# objects instead of raw SQL. Benefits: type-safety (compile-time checking), database provider abstraction (switch between SQL Server and PostgreSQL), change tracking, migrations for schema evolution, and protection against SQL injection.

**Q: What are EF Core migrations?**
A: Migrations are versioned snapshots of the database schema. When you change an entity (add a property, change a relationship), you create a migration that generates the SQL to alter the database. Migrations can be applied forward (update) or rolled back (revert). We keep separate migration assemblies per database provider because SQL Server and PostgreSQL have different DDL syntax.

**Q: Fluent API vs Data Annotations -- which do you prefer and why?**
A: Fluent API, because it keeps domain entities clean. Data Annotations put database concerns ([Column], [MaxLength], [ForeignKey]) directly on entity classes, which violates Clean Architecture's rule that inner layers should not know about infrastructure. Fluent API also supports advanced configurations that annotations cannot express (owned types, complex indexes, table splitting).

---

## 8. Repository Pattern

### What is it?

The Repository Pattern abstracts database access behind an interface. Your business logic works with the repository as if it were an in-memory collection. The repository hides the details of how data is actually stored and retrieved.

**Analogy:** A library's catalog system. You ask the catalog (IRepository) for a book by title. You do not care whether the book is in the basement, on the third floor, or in a partner library's warehouse. The catalog figures out where it is and gets it for you. If the library reorganizes its storage, your way of requesting books does not change.

### How it works in our project

**Interface** (Application layer, no EF Core dependency):
```csharp
// File: src/Core/ERP.Application.Abstractions/Persistence/IRepository.cs
public interface IRepository<T> where T : BaseEntity
{
    Task<T?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<T>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<T>> FindAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default);
    Task<bool> AnyAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default);
    Task AddAsync(T entity, CancellationToken cancellationToken = default);
    void Update(T entity);
    void Remove(T entity);
}
```

**Implementation** (Infrastructure layer, uses EF Core):
```csharp
// File: src/Infrastructure/ERP.Persistence/Repositories/Repository.cs
public class Repository<T> : IRepository<T> where T : BaseEntity
{
    protected readonly AppDbContext Context;
    protected readonly DbSet<T> DbSet;

    public Repository(AppDbContext context)
    {
        Context = context;
        DbSet = context.Set<T>();
    }

    public async Task<T?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => await DbSet.FindAsync([id], ct);

    public async Task AddAsync(T entity, CancellationToken ct = default)
        => await DbSet.AddAsync(entity, ct);
}
```

**DI Registration** (open generic):
```csharp
// File: src/Infrastructure/ERP.Persistence/Extensions/PersistenceServiceExtensions.cs
services.AddScoped(typeof(IRepository<>), typeof(Repository<>));
// This means: IRepository<JobCard> resolves to Repository<JobCard>
//             IRepository<WorkOrder> resolves to Repository<WorkOrder>
//             ... automatically for ANY entity type
```

### Interview Q&A

**Q: Why use the Repository Pattern when EF Core's DbContext already abstracts the database?**
A: Four reasons. (1) Testability -- you can mock IRepository in unit tests without an actual database. (2) Separation of concerns -- command handlers do not depend on EF Core, following Clean Architecture. (3) Swappability -- we switched between SQL Server and PostgreSQL by changing the implementation, not the business logic. (4) Centralized data access -- all queries go through one place where you can add caching, logging, or filtering consistently.

**Q: Is the Repository Pattern over DbContext an anti-pattern?**
A: This is debated. Some argue DbContext is already a repository + unit of work. In our case, the Repository pattern is justified because we practice Clean Architecture (Application layer must not reference EF Core) and multi-tenancy (the repository is unaware that it might connect to different databases per request). For simple CRUD apps with one database, using DbContext directly in handlers is perfectly fine.

---

## 9. Unit of Work

### What is it?

The Unit of Work pattern groups multiple data operations into a single transaction. Either all changes succeed together, or none are saved. It is the "save" button that commits everything at once.

**Analogy:** Writing a check at a restaurant. You order appetizer, main course, and dessert (three operations). You do not pay after each dish. You pay once at the end for everything (SaveChangesAsync). If your card declines, none of the dishes are charged -- the entire transaction rolls back.

### How it works in our project

**Interface** (Application layer):
```csharp
// File: src/Core/ERP.Application.Abstractions/Persistence/IUnitOfWork.cs
public interface IUnitOfWork : IDisposable
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
    Task BeginTransactionAsync(CancellationToken cancellationToken = default);
    Task CommitTransactionAsync(CancellationToken cancellationToken = default);
    Task RollbackTransactionAsync(CancellationToken cancellationToken = default);
}
```

**Implementation** (wraps DbContext.SaveChangesAsync):
```csharp
// File: src/Infrastructure/ERP.Persistence/Repositories/UnitOfWork.cs
public sealed class UnitOfWork : IUnitOfWork
{
    private readonly AppDbContext _context;

    public async Task<int> SaveChangesAsync(CancellationToken ct = default)
        => await _context.SaveChangesAsync(ct);

    public async Task CommitTransactionAsync(CancellationToken ct = default)
    {
        try
        {
            await _context.SaveChangesAsync(ct);
            await _currentTransaction.CommitAsync(ct);
        }
        catch
        {
            await RollbackTransactionAsync(ct);
            throw;
        }
    }
}
```

**Usage in a command handler:**
```csharp
// (Typical pattern in a handler)
await _jobCardRepo.AddAsync(newJobCard, ct);   // tracked, NOT yet saved
_workOrderRepo.Update(workOrder);               // tracked, NOT yet saved
await _unitOfWork.SaveChangesAsync(ct);          // NOW all changes saved atomically
```

### Interview Q&A

**Q: What is the Unit of Work pattern?**
A: Unit of Work coordinates multiple repository operations into a single database transaction. All changes succeed or fail together (atomicity). In our project, the IUnitOfWork wraps EF Core's SaveChangesAsync(). When the handler calls SaveChangesAsync(), the AuditableEntityInterceptor fills audit fields, the SQL is executed in a single transaction, and the DomainEventDispatcherInterceptor publishes domain events. If any part fails, nothing is persisted.

**Q: Why not just call SaveChanges in each repository method?**
A: Because you would lose transactional consistency. If creating a JobCard and updating a WorkOrder happen in separate SaveChanges calls, the WorkOrder update could fail after the JobCard was already saved, leaving the database in an inconsistent state. Unit of Work batches everything and commits once.

---

## 10. Dependency Injection (DI)

### What is it?

Dependency Injection is a technique where an object receives its dependencies from the outside rather than creating them itself. Instead of a class saying "I will create my own database connection," it says "give me a database connection and I will use it."

**Analogy:** Ordering food delivery vs cooking. Without DI, every class cooks its own meals (creates its own dependencies) -- wasteful, inflexible, hard to test. With DI, a central kitchen (DI container) prepares the meals (dependencies) and delivers them to whoever needs them (constructor injection).

### How it works in our project

**Three steps:**

**Step 1: Register** (tell the container what to create)
```csharp
// File: src/Infrastructure/ERP.Persistence/Extensions/PersistenceServiceExtensions.cs
services.AddScoped(typeof(IRepository<>), typeof(Repository<>));
services.AddScoped<IUnitOfWork, UnitOfWork>();
```

**Step 2: Inject** (request dependencies via constructor)
```csharp
// In a command handler:
public class CreateJobCardHandler
{
    private readonly IRepository<JobCard> _repo;   // DI provides Repository<JobCard>
    private readonly IUnitOfWork _unitOfWork;       // DI provides UnitOfWork

    public CreateJobCardHandler(IRepository<JobCard> repo, IUnitOfWork unitOfWork)
    {
        _repo = repo;
        _unitOfWork = unitOfWork;
    }
}
```

**Step 3: Dispose** (DI container disposes objects at end of their lifetime scope)

**Extension methods keep it organized:** Each project provides its own `Add*()` method:
```csharp
// File: src/Host/ERP.Api/Program.cs
builder.Services.AddPersistence();                        // repos, UoW, interceptors
builder.Services.AddInfrastructure(builder.Configuration); // tenancy, caching
builder.Services.AddIdentityServices(builder.Configuration); // JWT, auth
```

### Interview Q&A

**Q: What is Dependency Injection and why use it?**
A: DI is a technique where dependencies are provided externally via constructors instead of being created internally. Benefits: (1) Loose coupling -- classes depend on interfaces, not implementations. (2) Testability -- inject mock implementations in tests. (3) Flexibility -- swap implementations (SQL Server to PostgreSQL) by changing one registration line. (4) Lifecycle management -- the DI container manages object creation and disposal. ASP.NET Core has a built-in DI container.

**Q: What is the difference between constructor injection, method injection, and property injection?**
A: Constructor injection (our primary approach) provides dependencies through the constructor -- the class cannot be created without them. Method injection provides dependencies as method parameters -- used in middleware's InvokeAsync where scoped services are injected per-request. Property injection assigns dependencies to properties -- least preferred because it hides dependencies and makes them optional.

**Q: What is the Composition Root?**
A: The Composition Root is the single place in the application where all DI registrations happen. In our project, it is `src/Host/ERP.Api/Program.cs`. This is the only project that references all other projects because it needs to wire interfaces to implementations. No other project should know about all the others.

---

## 11. DI Lifetimes: Singleton vs Scoped vs Transient

### Full Explanation

The DI container needs to know how long each service should live. There are three lifetimes:

| Lifetime | Instance count | Created when | Disposed when | Thread-safe required? |
|----------|---------------|-------------|---------------|----------------------|
| **Singleton** | ONE for entire app | First request | App shuts down | YES |
| **Scoped** | ONE per HTTP request | Request starts | Request ends | No (one request = one thread) |
| **Transient** | NEW every injection | Every time requested | End of scope or GC | No |

**Analogy:**
- **Singleton** = The company printer. One printer for everyone. Must handle multiple people printing at once (thread-safe). Lives as long as the office exists.
- **Scoped** = A shopping cart. Each customer (HTTP request) gets their own cart. Cart is emptied (disposed) when the customer leaves. Two customers never share a cart.
- **Transient** = Disposable gloves. Fresh pair every time you need them. Used once and thrown away.

### What we use in this project

```csharp
// SINGLETON -- lives forever, shared across all requests
services.AddSingleton<ICacheService, MemoryCacheService>();  // Must be thread-safe!
services.AddSingleton<ITokenService, TokenService>();         // Stateless, expensive to create
services.AddSingleton<ITenantStore, ConfigurationTenantStore>(); // Read-only config data

// SCOPED -- one per HTTP request
services.AddScoped(typeof(IRepository<>), typeof(Repository<>)); // Shares DbContext per request
services.AddScoped<IUnitOfWork, UnitOfWork>();                    // Must match DbContext lifetime
services.AddScoped<AppDbContext>();                               // One DB session per request
services.AddScoped<ITenantProvider, HttpContextTenantProvider>(); // Reads from HttpContext
services.AddScoped<ICurrentUserService, CurrentUserService>();    // Reads JWT claims

// TRANSIENT -- new instance every time (not heavily used in our project)
// services.AddTransient<IEmailSender, SmtpEmailSender>();
```

### Common Mistakes

**Captive Dependency (the most common mistake):**
Injecting a Scoped service into a Singleton service. The Scoped service gets "captured" and lives forever inside the Singleton, becoming a de facto Singleton itself. This causes bugs because DbContext (Scoped) would accumulate entities forever, leak memory, and be used across multiple threads.

```csharp
// BAD: MemoryCacheService is Singleton but tries to use AppDbContext (Scoped)
public class MemoryCacheService  // Singleton
{
    private readonly AppDbContext _context; // Scoped -- CAPTURED! Lives forever now
    // Bug: _context will hold stale data and eventually crash
}

// ASP.NET Core will throw an exception at startup if ValidateScopes is enabled.
```

**Rule of thumb:** A service can only depend on services with an **equal or longer** lifetime:
- Singleton can depend on: Singleton only
- Scoped can depend on: Singleton, Scoped
- Transient can depend on: Singleton, Scoped, Transient

### Decision Guide

```
Is the service stateless and expensive to create?      --> Singleton
Does it need per-request state (DbContext, user info)? --> Scoped
Is it lightweight and stateless?                       --> Transient or Scoped
Not sure?                                              --> Start with Scoped
```

### Interview Q&A

**Q: Explain Singleton, Scoped, and Transient lifetimes.**
A: Singleton creates one instance for the entire application lifetime -- used for caches, configuration, and stateless services. Scoped creates one instance per HTTP request -- used for DbContext, repositories, and anything that should be shared within a request but isolated between requests. Transient creates a new instance every time it is injected -- used for lightweight, stateless services.

**Q: What happens if you inject a Scoped service into a Singleton?**
A: This is called a "Captive Dependency" and it is a bug. The Scoped service gets captured inside the Singleton and lives forever instead of being disposed at the end of the request. For DbContext, this means stale data, memory leaks, and thread-safety violations. ASP.NET Core detects this at startup when scope validation is enabled (on by default in Development).

**Q: Why is DbContext Scoped?**
A: Three reasons. (1) DbContext is not thread-safe, so Singleton would crash under concurrent requests. (2) DbContext accumulates tracked entities in memory, so Singleton would be a memory leak. (3) Scoped matches the Unit of Work pattern: one session per request, all changes committed together, then disposed.

---

## 12. Middleware

### What is it?

Middleware is code that runs in the ASP.NET Core HTTP request pipeline. Each piece of middleware can process the request, optionally modify it, and decide whether to pass it to the next middleware or short-circuit the pipeline.

**Analogy:** Airport security checkpoints. Your request (luggage) passes through multiple checkpoints in order: ticket check, ID verification, baggage scan, customs. Each checkpoint can let you through to the next one, or stop you (short-circuit). On the way back (response), you pass through the same checkpoints in reverse order. Order matters -- you cannot go through customs before your ticket is checked.

### How it works in our project

Middleware is registered in `Program.cs` and **order matters**:

```csharp
// File: src/Host/ERP.Api/Program.cs
app.UseMiddleware<GlobalExceptionMiddleware>(); // 1. Catches ALL exceptions
app.UseHttpsRedirection();                       // 2. Redirects HTTP to HTTPS
app.UseCors("AllowAngular");                     // 3. Adds CORS headers
app.UseAuthentication();                          // 4. Validates JWT token
app.UseAuthorization();                           // 5. Checks [Authorize]
app.UseMiddleware<TenantMiddleware>();            // 6. Resolves tenant
app.UseSerilogRequestLogging();                   // 7. Logs request duration
app.MapControllers();                             // 8. Routes to controllers
```

**Our TenantMiddleware** demonstrates all key middleware concepts:

```csharp
// File: src/Infrastructure/ERP.Infrastructure/Tenant/TenantMiddleware.cs
public class TenantMiddleware
{
    private readonly RequestDelegate _next;

    public TenantMiddleware(RequestDelegate next, ILogger<TenantMiddleware> logger) { ... }

    public async Task InvokeAsync(
        HttpContext context,
        ITenantStore tenantStore,   // Method injection (scoped services)
        IMemoryCache cache)         // Method injection (singleton service)
    {
        // Skip for public endpoints
        if (ShouldSkipTenantResolution(path)) { await _next(context); return; }

        // Read X-Tenant-Id header
        if (!context.Request.Headers.TryGetValue("X-Tenant-Id", out var tenantId))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { error = "Missing header" });
            return;  // SHORT-CIRCUIT: pipeline stops here, no _next()
        }

        // Look up tenant (with caching)
        var tenant = cache.Get(key) ?? await tenantStore.GetByIdentifierAsync(tenantId);

        // Store in HttpContext for downstream use
        context.Items["CurrentTenant"] = tenant;

        // Pass to next middleware
        await _next(context);
    }
}
```

### Interview Q&A

**Q: What is middleware in ASP.NET Core?**
A: Middleware is a component that sits in the HTTP request pipeline. Each middleware receives the request, can process it, and either passes it to the next middleware (by calling `_next(context)`) or short-circuits the pipeline (by returning without calling `_next`). Requests flow through middleware top-to-bottom; responses flow bottom-to-top.

**Q: Why does middleware order matter?**
A: Because each middleware can only work with what previous middleware has set up. For example, TenantMiddleware runs after Authentication because it needs the JWT to be validated first. If you put CORS after Authentication, preflight OPTIONS requests would fail because they do not carry auth tokens. Exception handling middleware runs first so it can catch exceptions from any downstream middleware.

**Q: What does "short-circuit" mean?**
A: Short-circuiting means the middleware returns a response without calling `_next(context)`. The request never reaches downstream middleware or controllers. Our TenantMiddleware short-circuits with 400/404/403 when the tenant header is missing, the tenant is not found, or the tenant is inactive.

---

## 13. JWT Authentication

### What is it?

JWT (JSON Web Token) is a compact, self-contained token used for authentication. After a user logs in, the server creates a JWT containing the user's identity and permissions. The client (Angular) sends this token with every API request. The server validates the token's signature to verify the user without hitting the database.

**Analogy:** A concert wristband. When you arrive (login), security checks your ticket and gives you a wristband (JWT). For the rest of the event, you just flash your wristband at any area (API endpoint) to prove you are allowed in. The wristband contains your access level (claims: VIP, general admission). Security can verify the wristband is genuine by checking its hologram (signature) without calling the box office (database).

### Token Structure

A JWT has three parts separated by dots: `xxxxx.yyyyy.zzzzz`

```
Header.Payload.Signature

Header:  { "alg": "HS256", "typ": "JWT" }          -- algorithm used
Payload: { "sub": "user-123", "email": "...",        -- claims (user data)
           "role": ["Admin", "QA_Inspector"],
           "tenant_id": "abc-mfg", "exp": 1234567 }
Signature: HMAC-SHA256(header + payload, secret_key)  -- tamper-proof seal
```

### Login Flow in Our Project

```
1. Angular sends:  POST /api/v1/auth/login { email, password }
2. API validates credentials against database
3. API creates JWT with claims (userId, roles, permissions, tenantId)
4. API returns: { accessToken: "eyJ...", refreshToken: "xK9..." }
5. Angular stores tokens and sends on every request:
     Authorization: Bearer eyJhbGciOiJIUzI1NiIs...
```

### Access Token + Refresh Token Flow

```
Access Token:  Short-lived (60 min). Used for API calls. Cannot be revoked.
Refresh Token: Long-lived (7 days). Used only to get new access tokens. Can be revoked.

1. Login         --> returns access token (60 min) + refresh token (7 days)
2. API calls     --> Angular sends access token in Authorization header
3. Token expires --> Angular sends refresh token to /auth/refresh
4. Server validates refresh token, issues new access token
5. Repeat step 2-4
6. After 7 days  --> refresh token expires, user must log in again
```

### Code Reference

```csharp
// File: src/Infrastructure/ERP.Identity/Services/TokenService.cs
public string GenerateAccessToken(AppUser user, IList<string> roles, ...)
{
    var claims = new List<Claim>
    {
        new(ClaimTypes.NameIdentifier, user.Id.ToString()),
        new(ClaimTypes.Email, user.Email),
        new("tenant_id", tenantId),
    };
    foreach (var role in roles)
        claims.Add(new Claim(ClaimTypes.Role, role));

    var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtSettings.SecretKey));
    var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

    var token = new JwtSecurityToken(
        issuer: _jwtSettings.Issuer,
        audience: _jwtSettings.Audience,
        claims: claims,
        expires: DateTime.UtcNow.AddMinutes(_jwtSettings.AccessTokenExpirationMinutes),
        signingCredentials: credentials);

    return new JwtSecurityTokenHandler().WriteToken(token);
}
```

**Validation is configured in:**
```csharp
// File: src/Infrastructure/ERP.Identity/Extensions/IdentityServiceExtensions.cs
options.TokenValidationParameters = new TokenValidationParameters
{
    ValidateIssuerSigningKey = true,   // Was it signed with our key?
    ValidateIssuer = true,              // Did our API issue it?
    ValidateAudience = true,            // Is it meant for our Angular app?
    ValidateLifetime = true,            // Has it expired?
    ClockSkew = TimeSpan.Zero           // No tolerance for expired tokens
};
```

### Interview Q&A

**Q: How does JWT authentication work?**
A: On login, the server validates credentials and creates a JWT containing the user's claims (ID, roles, permissions, tenant). The token is signed with a secret key using HMAC-SHA256. The client sends this token in the Authorization header with every request. The server validates the signature (to ensure it was not tampered with), checks expiration, and extracts claims to identify the user. No database lookup is needed for validation -- the token is self-contained.

**Q: Why use both access tokens and refresh tokens?**
A: Access tokens are short-lived (60 min) and sent with every request. If stolen, the attacker has limited time. They cannot be revoked because they are stateless. Refresh tokens are long-lived (7 days), stored securely, and only used to get new access tokens. They can be revoked by deleting them from the database. This gives us the performance of stateless authentication with the security of revocable sessions.

**Q: What are claims in a JWT?**
A: Claims are key-value pairs stored in the token's payload. They contain information about the user (name, email, ID) and what they can do (roles, permissions). Our JWT includes a custom "tenant_id" claim so every request carries the user's tenant without an extra database lookup.

---

## 14. CQRS with MediatR

### What is it?

CQRS (Command Query Responsibility Segregation) separates read operations (Queries) from write operations (Commands). Instead of one service class that does everything, each operation gets its own handler.

MediatR is a library that implements the Mediator pattern. Controllers send commands/queries to MediatR, and MediatR routes them to the correct handler. The controller never directly references the handler.

**Analogy:** A restaurant order system. **Commands** are orders placed by customers ("make me a pizza") -- they change state (kitchen makes food). **Queries** are questions to the waiter ("what is on the menu?") -- they only read information. MediatR is the waiter -- it takes your order (command/query) to the right station (handler) without you needing to know where the kitchen is.

### How it works in our project

**Command (write operation):**
```csharp
// File: src/Core/ERP.Application.Abstractions/CQRS/ICommand.cs
public interface ICommand : IRequest<Result> { }
public interface ICommand<TResponse> : IRequest<Result<TResponse>> { }
```

**Query (read operation):**
```csharp
// File: src/Core/ERP.Application.Abstractions/CQRS/IQuery.cs
public interface IQuery<TResponse> : IRequest<Result<TResponse>> { }
```

**Handlers:**
```csharp
// File: src/Core/ERP.Application.Abstractions/CQRS/ICommandHandler.cs
public interface ICommandHandler<in TCommand, TResponse>
    : IRequestHandler<TCommand, Result<TResponse>>
    where TCommand : ICommand<TResponse> { }

// File: src/Core/ERP.Application.Abstractions/CQRS/IQueryHandler.cs
public interface IQueryHandler<in TQuery, TResponse>
    : IRequestHandler<TQuery, Result<TResponse>>
    where TQuery : IQuery<TResponse> { }
```

### Pipeline Behaviors

Pipeline behaviors are middleware for MediatR. They intercept every command/query before it reaches the handler.

```
Controller --> _mediator.Send(command)
  --> [ValidationBehavior]   validates the command with FluentValidation
    --> [LoggingBehavior]    logs request name and duration
      --> [Handler]          executes business logic
```

**ValidationBehavior** automatically validates every command using FluentValidation. If validation fails, the handler is never called:
```csharp
// File: src/Core/ERP.Application.Abstractions/Behaviors/ValidationBehavior.cs
public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, ...)
{
    if (!_validators.Any()) return await next();
    var errors = /* run all validators */;
    if (errors.Count != 0)
        return Result.Failure(Error.Validation("Validation.Failed", errorMessages));
    return await next();  // Only proceed if valid
}
```

**LoggingBehavior** logs every request with execution timing:
```csharp
// File: src/Core/ERP.Application.Abstractions/Behaviors/LoggingBehavior.cs
_logger.LogInformation("Handling {RequestName}", requestName);
var stopwatch = Stopwatch.StartNew();
var response = await next();
if (stopwatch.ElapsedMilliseconds > 500)
    _logger.LogWarning("SLOW REQUEST: {RequestName} took {ElapsedMs}ms", ...);
```

### Interview Q&A

**Q: What is CQRS and why use it?**
A: CQRS separates reads (queries) from writes (commands) into different models. Benefits: (1) Single Responsibility -- each handler does one thing. (2) Scalability -- reads and writes can be optimized independently (e.g., cache queries, validate commands). (3) Clarity -- seeing `CreateJobCardCommand` tells you it modifies data, seeing `GetJobCardByIdQuery` tells you it only reads. (4) Testability -- small, focused handlers are easy to test.

**Q: What is MediatR and how does it relate to CQRS?**
A: MediatR is a Mediator pattern implementation for .NET. It decouples the sender (controller) from the receiver (handler). The controller sends a command/query to MediatR, and MediatR finds and invokes the correct handler via DI. This means controllers do not reference handlers directly, making them thin and focused on HTTP concerns only.

**Q: What are pipeline behaviors?**
A: Pipeline behaviors are MediatR's equivalent of middleware. They run before (and optionally after) every command/query handler. We use ValidationBehavior (validates with FluentValidation before the handler runs) and LoggingBehavior (logs request name and duration). This keeps cross-cutting concerns out of handlers.

---

## 15. Caching

### What is it?

Caching stores frequently accessed data in fast memory (RAM) so the application does not hit the database every time. The first request fetches from the database and stores the result in cache. Subsequent requests read from cache, which is orders of magnitude faster.

**Analogy:** Sticky notes on your desk. The first time you look up a phone number (database query), you write it on a sticky note (cache). Next time you need it, you just look at the sticky note instead of searching through the phone book again. The sticky note expires after a while (cache TTL), and you check the phone book again to make sure the number has not changed.

### L1 vs L2 Cache

| Feature | L1 (In-Memory) | L2 (Distributed / Redis) |
|---------|----------------|--------------------------|
| Speed | Nanoseconds | Milliseconds |
| Shared across instances | No (each server has its own) | Yes (central Redis server) |
| Survives restart | No | Yes |
| Use for | Config, small lookup tables | Session data, heavy query results |
| Our implementation | `MemoryCacheService` | Planned (TODO in code) |

### How it works in our project

**Interface** (Application layer):
```csharp
// File: src/Core/ERP.Application.Abstractions/Caching/ICacheService.cs
public interface ICacheService
{
    Task<T?> GetAsync<T>(string key, CancellationToken ct = default);
    Task SetAsync<T>(string key, T value, TimeSpan? expiration = null, CancellationToken ct = default);
    Task RemoveAsync(string key, CancellationToken ct = default);
    Task RemoveByPrefixAsync(string prefix, CancellationToken ct = default);
    Task<T?> GetOrCreateAsync<T>(string key, Func<Task<T>> factory, TimeSpan? expiration = null, ...);
}
```

**In-memory implementation** (registered as Singleton because all requests share the same cache):
```csharp
// File: src/Infrastructure/ERP.Infrastructure/Caching/MemoryCacheService.cs
public class MemoryCacheService : ICacheService
{
    private readonly IMemoryCache _cache;
    private readonly ConcurrentDictionary<string, bool> _cacheKeys = new(); // Thread-safe!

    public Task SetAsync<T>(string key, T value, TimeSpan? expiration = null, ...)
    {
        _cache.Set(key, value, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = expiration ?? TimeSpan.FromMinutes(15)
        });
        _cacheKeys.TryAdd(key, true);
        return Task.CompletedTask;
    }
}
```

**Caching in TenantMiddleware** (using IMemoryCache directly for tenant lookups):
```csharp
// File: src/Infrastructure/ERP.Infrastructure/Tenant/TenantMiddleware.cs
var cacheKey = $"tenant:{tenantIdentifier}";
if (!cache.TryGetValue(cacheKey, out TenantInfo? tenant))
{
    tenant = await tenantStore.GetByIdentifierAsync(tenantIdentifier);
    if (tenant is not null)
        cache.Set(cacheKey, tenant, TimeSpan.FromMinutes(30));
}
```

### Cache Invalidation

Cache invalidation is the process of removing stale data from the cache when the underlying data changes. Our `ICacheService` supports:
- `RemoveAsync(key)` -- remove a specific entry (e.g., when a job card is updated)
- `RemoveByPrefixAsync(prefix)` -- remove all entries matching a prefix (e.g., `"jobcard:*"` when bulk operations happen)

### Interview Q&A

**Q: What is caching and why use it?**
A: Caching stores frequently accessed data in fast memory to avoid repeated expensive database queries. A database query might take 100ms; a cache read takes under 1ms. We use L1 (in-memory) cache for data like tenant lookups and department lists. For multi-instance deployments, a distributed cache (Redis) would be used as L2.

**Q: What is cache invalidation and why is it considered hard?**
A: Cache invalidation is ensuring the cache does not serve stale data. It is considered hard because you must decide when to invalidate (on every write? on a timer?), what to invalidate (just one key? all related keys?), and how to handle race conditions (two requests updating the same entity simultaneously). Our approach uses explicit invalidation via `RemoveByPrefixAsync` after write operations, combined with TTL expiration as a safety net.

---

## 16. Multi-Tenancy

### What is it?

Multi-tenancy is an architecture where a single application instance serves multiple client organizations (tenants), each with their own isolated data. Our ERP uses **database-per-tenant** isolation, meaning each tenant has a completely separate database.

**Analogy:** An apartment building. Each apartment (tenant database) is completely separate -- different furniture, different locks, different residents. But they all share the same building structure (application code), elevator (API), and management office (master database that knows which apartment belongs to which resident).

### How it works in our project

The multi-tenancy system has four components:

**1. TenantInfo** -- data about each tenant:
```csharp
// File: src/Infrastructure/ERP.Infrastructure/Tenant/TenantInfo.cs
public class TenantInfo
{
    public string Identifier { get; set; }       // "abc-manufacturing"
    public string ConnectionString { get; set; }  // "Server=...;Database=ERP_ABC;..."
    public string DbProvider { get; set; }         // "SqlServer" or "PostgreSql"
    public bool IsActive { get; set; }
}
```

**2. TenantMiddleware** -- resolves the tenant for every request:
```
Request with header X-Tenant-Id: abc-mfg
  --> TenantMiddleware reads header
  --> Looks up tenant in ITenantStore (with caching)
  --> Stores TenantInfo in HttpContext.Items["CurrentTenant"]
  --> If not found: returns 404 (short-circuit)
```
File: `src/Infrastructure/ERP.Infrastructure/Tenant/TenantMiddleware.cs`

> **`Tenant.Code` is a case-insensitive identifier (`DB-10`, merged 2026-09-27).** The header
> may arrive in any casing, with or without surrounding whitespace; resolution canonicalizes
> it to `Trim()` + `ToLowerInvariant()` **in the application**, so a tenant is the same tenant
> on SQL Server (case-insensitive collation) and PostgreSQL (case-sensitive). The cache key is
> canonical too, so one tenant cannot occupy a cache entry per casing.
>
> What did **not** change: the missing/blank-header guard still returns `400`
> `TENANT_HEADER_MISSING` before any lookup; the store still receives the **raw** header
> value; the 30-minute cache TTL is unchanged; and the `X-Tenant-Id` / `tenant_id`
> cross-check remains `StringComparison.OrdinalIgnoreCase`. Canonical form is enforced by the
> application, **not** by the database — see `DATABASE_ARCHITECTURE.md` and the `DB-11`
> deployment precheck in `TENANT_PROVISIONING.md`.

**3. ITenantProvider** -- provides tenant info to downstream services:
```csharp
// File: src/Infrastructure/ERP.Infrastructure/Tenant/HttpContextTenantProvider.cs
public TenantInfo GetCurrentTenant()
{
    var httpContext = _httpContextAccessor.HttpContext;
    return httpContext.Items["CurrentTenant"] as TenantInfo;
}
```

**4. TenantDbContextFactory** -- creates the right database connection per tenant:
```csharp
// File: src/Infrastructure/ERP.Infrastructure/Tenant/TenantDbContextFactory.cs
public AppDbContext CreateDbContext()
{
    var tenant = _tenantProvider.GetCurrentTenant();
    var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();

    switch (tenant.DbProvider.ToLower())
    {
        case "sqlserver":
            optionsBuilder.UseSqlServerProvider(tenant.ConnectionString);
            break;
        case "postgresql":
            optionsBuilder.UsePostgreSqlProvider(tenant.ConnectionString);
            break;
    }

    return new AppDbContext(optionsBuilder.Options);
}
```

**Tenant resolution flow:**
```
Angular sends: X-Tenant-Id: abc-mfg
  --> TenantMiddleware resolves tenant, stores in HttpContext
  --> Controller receives request, needs data
  --> DI creates Repository --> Repository needs AppDbContext
  --> DI calls TenantDbContextFactory.CreateDbContext()
  --> Factory reads current tenant: abc-mfg uses SqlServer at Server=...
  --> Factory creates AppDbContext with abc-mfg's connection string
  --> Repository queries abc-mfg's database transparently
```

### Interview Q&A

**Q: What is multi-tenancy and what are the isolation strategies?**
A: Multi-tenancy serves multiple organizations from one application. Three isolation strategies: (1) Shared database, shared schema -- tenants share tables, filtered by TenantId column (cheapest, least isolation). (2) Shared database, separate schema -- each tenant has its own schema (moderate isolation). (3) Separate database per tenant (our approach) -- full isolation, each tenant has its own database. We chose database-per-tenant because ERP data (financials, inventory) requires strong isolation, and different clients may need different database providers.

**Q: How do you resolve the tenant at runtime?**
A: The Angular frontend sends an `X-Tenant-Id` header with every request. TenantMiddleware extracts this header, looks up the tenant in the master store (with in-memory caching to avoid repeated lookups), validates that the tenant exists and is active, and stores the TenantInfo in HttpContext. A TenantDbContextFactory then creates the AppDbContext with the correct connection string and database provider for that tenant.

---

## 17. SignalR

### What is it?

SignalR enables real-time, bidirectional communication between server and client over WebSocket connections. Unlike normal HTTP where the client must ask for data, SignalR allows the server to push messages to connected clients instantly.

**Analogy:** Normal HTTP is like email -- you send a request and wait for a reply. SignalR is like a phone call -- once the connection is established, both sides can talk at any time. When a job card status changes on the production floor, the server immediately tells all watching dashboards "Job Card #123 is now Complete" without them having to refresh.

### How it works in our project

**Hub** (the server-side endpoint clients connect to):
```csharp
// File: src/Infrastructure/ERP.Notifications/Hubs/ErpNotificationHub.cs
[Authorize]  // Requires JWT authentication
public class ErpNotificationHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        var tenantId = Context.User?.FindFirst("tenant_id")?.Value;
        if (!string.IsNullOrEmpty(tenantId))
            await Groups.AddToGroupAsync(Context.ConnectionId, $"tenant:{tenantId}");
        await base.OnConnectedAsync();
    }

    public async Task JoinGroup(string groupName)
    {
        var tenantId = Context.User?.FindFirst("tenant_id")?.Value;
        await Groups.AddToGroupAsync(Context.ConnectionId, $"tenant:{tenantId}:{groupName}");
    }
}
```

**Registration:**
```csharp
// File: src/Host/ERP.Api/Program.cs
builder.Services.AddSignalR();
app.MapHub<ErpNotificationHub>("/hubs/notifications");
```

**Groups** organize connections so notifications reach the right users:
```
Group: "tenant:abc-mfg"               --> all users of ABC Manufacturing
Group: "tenant:abc-mfg:dept:cutting"   --> only Cutting department users
Group: "tenant:abc-mfg:qa"            --> only QA inspectors
```

**SignalR + JWT:** SignalR uses WebSocket, which cannot send HTTP headers. The JWT token is sent as a query parameter instead, handled in the Identity configuration:
```csharp
// File: src/Infrastructure/ERP.Identity/Extensions/IdentityServiceExtensions.cs
options.Events = new JwtBearerEvents
{
    OnMessageReceived = context =>
    {
        var accessToken = context.Request.Query["access_token"];
        if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs"))
            context.Token = accessToken;
        return Task.CompletedTask;
    }
};
```

### Interview Q&A

**Q: What is SignalR and when would you use it?**
A: SignalR provides real-time server-to-client communication over WebSocket (with fallbacks to Server-Sent Events and Long Polling). Use it when the server needs to push updates without the client polling. In our ERP: job card status changes, QA alerts, dashboard updates, production completion notifications.

**Q: How does SignalR authentication work?**
A: SignalR connections use WebSocket, which does not support custom HTTP headers after the initial handshake. The JWT token is sent as a query parameter (`?access_token=eyJ...`) during the connection handshake. We configure JwtBearerEvents.OnMessageReceived to read the token from the query string for requests to the `/hubs` path.

**Q: What are SignalR Groups?**
A: Groups are named collections of connections. When you send a message to a group, all connections in that group receive it. We use groups to isolate notifications by tenant and department. A user in ABC Manufacturing's Cutting department is in groups `tenant:abc-mfg` and `tenant:abc-mfg:dept:cutting`. Notifications sent to either group reach them.

---

## 18. API Versioning

### What is it?

API Versioning allows you to evolve your API without breaking existing clients. When you need to change an endpoint's behavior or response format, you create a new version instead of modifying the existing one.

**Analogy:** Software updates on your phone. When iOS 17 comes out, iOS 16 apps still work. The old version is supported while clients migrate to the new one. If you changed iOS without versioning, every app would break overnight.

### How it works in our project

The API versioning packages are included in the host project:
```xml
<!-- File: src/Host/ERP.Api/ERP.Api.csproj -->
<PackageReference Include="Asp.Versioning.Http" Version="8.1.0" />
<PackageReference Include="Asp.Versioning.Mvc.ApiExplorer" Version="8.1.0" />
```

**Two common approaches:**

| Approach | Example | Pros | Cons |
|----------|---------|------|------|
| URL-based | `/api/v1/jobcards` | Clear, easy to test | Breaks REST purity |
| Header-based | `Api-Version: 2.0` | Cleaner URLs | Harder to test, less visible |

Our project uses **URL-based versioning** (`/api/v1/...`) as seen in the TenantMiddleware skip path:
```csharp
path.StartsWith("/api/v1/auth/login")
```

### Interview Q&A

**Q: Why version an API?**
A: To allow backwards-incompatible changes without breaking existing clients. When you need to change a response format, rename a field, or restructure an endpoint, create a v2 instead of modifying v1. Old clients continue using v1 while new clients adopt v2. This is especially important when multiple frontend versions (mobile app v1, web app v2) consume the same API.

**Q: URL-based vs header-based versioning -- which is better?**
A: URL-based (e.g., `/api/v1/resource`) is more visible and easier to test (just change the URL). Header-based keeps URLs clean but is harder to discover and test. URL-based is the most common in practice and what we use. A third option, query string versioning (`?api-version=1.0`), is also possible but less common.

---

## 19. Health Checks

### What is it?

Health checks are endpoints that report whether the application and its dependencies (database, cache, external services) are functioning correctly. Monitoring tools and orchestrators (Kubernetes, load balancers) call these endpoints to decide if an instance should receive traffic.

**Analogy:** A doctor's checkup for your application. The doctor (monitoring system) checks vital signs: is the heartbeat normal (app running)? is blood pressure OK (database connected)? can the patient walk (can process requests)? If something is wrong, the patient is sent home (instance removed from load balancer) until they recover.

### Liveness vs Readiness

| Check | Question | Example | What happens on failure |
|-------|----------|---------|------------------------|
| **Liveness** | "Is the process alive?" | App responds to `/health/live` | Restart the instance |
| **Readiness** | "Can it handle requests?" | DB connected, cache warm | Stop sending traffic, but do not restart |

### How it works in our project

```csharp
// File: src/Host/ERP.Api/Program.cs
builder.Services.AddHealthChecks();
app.MapHealthChecks("/health");
```

Health check packages for all our dependencies:
```xml
<!-- File: src/Host/ERP.Api/ERP.Api.csproj -->
<PackageReference Include="AspNetCore.HealthChecks.SqlServer" Version="8.0.2" />
<PackageReference Include="AspNetCore.HealthChecks.NpgSql" Version="8.0.2" />
<PackageReference Include="AspNetCore.HealthChecks.Redis" Version="8.0.1" />
<PackageReference Include="AspNetCore.HealthChecks.UI.Client" Version="8.0.1" />
```

The `/health` endpoint is excluded from tenant resolution (no X-Tenant-Id header needed):
```csharp
// File: src/Infrastructure/ERP.Infrastructure/Tenant/TenantMiddleware.cs
private static bool ShouldSkipTenantResolution(string path)
{
    return path.StartsWith("/health") || ...;
}
```

### Interview Q&A

**Q: What are health checks in ASP.NET Core?**
A: Health checks are endpoints that report application health status (Healthy, Degraded, or Unhealthy). They verify that dependencies like databases, caches, and external services are reachable. Load balancers and container orchestrators use them to route traffic only to healthy instances. ASP.NET Core provides a built-in health check framework with extensible checks.

**Q: What is the difference between liveness and readiness probes?**
A: A liveness probe checks if the application process is running and not deadlocked. If it fails, the orchestrator restarts the instance. A readiness probe checks if the application can handle traffic (e.g., database connected, cache warmed up). If it fails, the orchestrator stops routing traffic but does not restart -- the instance might just need a moment to reconnect to a dependency.

---

## 20. Serilog / Structured Logging

### What is it?

Structured logging stores log entries as structured data (key-value pairs) rather than plain text strings. This makes logs searchable, filterable, and queryable. Serilog is the most popular structured logging library for .NET.

**Analogy:** Traditional logging is like writing diary entries ("Today I processed order 123 for customer John"). Structured logging is like filling out a form with fields: `{ Event: "OrderProcessed", OrderId: 123, Customer: "John", Duration: 42ms }`. The form entries can be searched, filtered, and aggregated -- try searching a diary for all orders over 100ms!

### How it works in our project

**Configuration:**
```csharp
// File: src/Host/ERP.Api/Program.cs
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)  // Read settings from appsettings.json
    .Enrich.FromLogContext()                          // Add contextual properties
    .WriteTo.Console()                                // Sink 1: Console output
    .WriteTo.File("logs/erp-.log",                   // Sink 2: Rolling log files
        rollingInterval: RollingInterval.Day)
    .CreateLogger();

builder.Host.UseSerilog();
app.UseSerilogRequestLogging();  // Logs every HTTP request with timing
```

**Key Concepts:**

**Sinks:** Destinations where logs are written. We use Console and File. Others available: Seq, Elasticsearch, Application Insights, Datadog.

**Enrichment:** Automatically adds contextual properties to every log entry. `Enrich.FromLogContext()` adds properties like RequestId, MachineName, and custom properties pushed via `LogContext.PushProperty()`.

**Structured properties:** Using `{@Object}` (destructuring) or `{Property}` (stringify):
```csharp
// Structured -- object stored as JSON in logs, searchable
Log.Information("Created {@JobCard}", jobCard);
// Output: Created { Id: "abc", Number: "JC-001", Quantity: 100 }

// Stringified -- just calls .ToString()
Log.Information("Created {JobCardId}", jobCard.Id);
```

### Interview Q&A

**Q: What is structured logging and why use Serilog?**
A: Structured logging stores log data as key-value pairs, not just text. With traditional logging, finding all requests that took over 500ms requires parsing text. With structured logging, you query `ElapsedMs > 500`. Serilog is .NET's most popular structured logging library. It supports multiple sinks (write to console, file, Seq, Elasticsearch simultaneously), enrichment (auto-add RequestId, machine name), and message templates with named properties.

**Q: What is a Serilog sink?**
A: A sink is an output destination for log events. Serilog can write to multiple sinks simultaneously. Console sink writes to stdout (for development), File sink writes rotating log files, Seq sink sends to a log server with a web UI for searching, and Application Insights sends to Azure monitoring. Adding a new sink requires only a NuGet package and one configuration line.

---

## 21. CORS

### What is it?

CORS (Cross-Origin Resource Sharing) is a browser security mechanism that blocks web pages from making requests to a different domain than the one that served the page. When Angular (localhost:4200) calls the API (localhost:5000), the browser blocks it because the ports differ. CORS headers tell the browser "it is OK, this API trusts that origin."

**Analogy:** A building security policy. By default, only employees with building badges (same origin) can enter. CORS is like adding a visitor list: "Allow anyone from Company X (Angular app) to access floors 1-5 (API endpoints)." The security guard (browser) checks the visitor list before letting anyone in.

### How it works in our project

```csharp
// File: src/Host/ERP.Api/Program.cs
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAngular", policy =>
    {
        policy.WithOrigins(
                builder.Configuration.GetSection("AllowedOrigins").Get<string[]>()
                ?? ["http://localhost:4200"])   // Angular dev server
            .AllowAnyMethod()                   // GET, POST, PUT, DELETE, etc.
            .AllowAnyHeader()                   // Authorization, Content-Type, X-Tenant-Id
            .AllowCredentials();                // Required for SignalR WebSocket
    });
});

// Applied in the pipeline BEFORE authentication:
app.UseCors("AllowAngular");
```

**Why `.AllowCredentials()`?** SignalR WebSocket connections send cookies and authorization headers. Without AllowCredentials, the browser blocks them.

**Why CORS before Authentication?** Browser CORS preflight requests (OPTIONS method) do not carry authorization tokens. If authentication ran first, preflight requests would get 401 Unauthorized and the real request would never happen.

### Interview Q&A

**Q: What is CORS and why is it needed?**
A: CORS is a browser security mechanism that prevents web pages from making requests to a different origin (domain, port, or protocol). When our Angular app at localhost:4200 calls the API at localhost:5000, the browser blocks it as a cross-origin request. We configure CORS on the server to add response headers (`Access-Control-Allow-Origin: http://localhost:4200`) telling the browser this cross-origin request is allowed.

**Q: What is a CORS preflight request?**
A: For non-simple requests (those with custom headers like Authorization or X-Tenant-Id, or methods like PUT/DELETE), the browser first sends an OPTIONS request (preflight) to ask "are you OK with this type of request?" The server responds with CORS headers indicating what is allowed. Only if the preflight succeeds does the browser send the actual request. This is why CORS middleware must run before Authentication -- preflight requests have no auth token.

---

## 22. Global Exception Handling

### What is it?

Global exception handling is a middleware that wraps the entire request pipeline in a try-catch. Any unhandled exception from any middleware or controller is caught, logged, and converted into a clean JSON error response. This prevents stack traces from leaking to clients and ensures Angular always receives a consistent error format.

**Analogy:** A safety net under a trapeze artist. No matter where the artist (request) falls from (which middleware or controller throws), the net (exception middleware) catches them and delivers a safe, controlled response to the audience (client).

### How it works in our project

```csharp
// File: src/Host/ERP.Api/Middleware/GlobalExceptionMiddleware.cs
public class GlobalExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<GlobalExceptionMiddleware> _logger;

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);  // Process the entire pipeline
        }
        catch (Exception ex)
        {
            var traceId = context.TraceIdentifier;
            _logger.LogError(ex, "Unhandled exception. TraceId: {TraceId}", traceId);

            context.Response.StatusCode = (int)GetStatusCode(ex);
            await context.Response.WriteAsJsonAsync(new
            {
                statusCode = context.Response.StatusCode,
                message = GetClientMessage(ex),  // Safe message, no stack trace
                traceId                           // For support to find the log
            });
        }
    }

    // Map exception types to HTTP status codes:
    private static HttpStatusCode GetStatusCode(Exception ex) => ex switch
    {
        ArgumentException            => HttpStatusCode.BadRequest,       // 400
        UnauthorizedAccessException  => HttpStatusCode.Unauthorized,     // 401
        KeyNotFoundException         => HttpStatusCode.NotFound,          // 404
        InvalidOperationException    => HttpStatusCode.Conflict,          // 409
        _                            => HttpStatusCode.InternalServerError // 500
    };
}
```

**Key design decisions:**
- Registered **first** in the pipeline so it catches exceptions from all downstream middleware
- Returns a JSON response (not HTML) because Angular is the client
- Includes a `traceId` so the support team can correlate the client error with the server log
- Hides internal details (no stack traces, connection strings) from the client

### Interview Q&A

**Q: How do you handle exceptions globally in ASP.NET Core?**
A: We use exception-handling middleware registered first in the pipeline. It wraps all downstream middleware in a try-catch. Unhandled exceptions are logged with full details (stack trace, inner exception) using Serilog, then converted to a clean JSON response with an appropriate HTTP status code. We map exception types to status codes (ArgumentException to 400, KeyNotFoundException to 404, etc.) and include a traceId for debugging without exposing internal details.

**Q: Why use middleware instead of a global exception filter?**
A: Middleware catches exceptions from the entire pipeline (including other middleware), while exception filters only catch exceptions from controllers and action methods. Our TenantMiddleware could throw an exception before reaching any controller -- only middleware catches that. Additionally, middleware gives more control over the response format and headers.

---

## 23. Multi-Database Provider

### What is it?

Multi-database provider support means the same application code runs against either database engine. **SQL Server and PostgreSQL are both first-class, chosen per customer deployment** — Master/Identity DB *and* Tenant DBs use the same provider (approved decision `ARCH-DB-03`). Mixing providers *within one deployment* is technically possible via `Tenant.DbProvider` but is **not a primary requirement** (`ARCH-DB-04`).

> **PROVIDER SELECTION AND CREATION WORK; FULL PARITY IS PARTIALLY OPEN.** Status 2026-10-10
> (Phase 1 slices P1-b – P1-k merged; P1-l documentation reconciliation in progress).
>
> **Closed — provider selection, creation and from-empty parity:**
>
> | Closed | How |
> |---|---|
> | **`DB-01` — Master provider selection**, runtime **and** design-time | `DatabaseProviderResolver` reads `Database:Provider` (`SqlServer` or `PostgreSql`) and both sites switch to the existing provider seams. Runtime resolves **outside** the `AddDbContext` lambda, so a missing or unrecognised value fails at **startup**, not on first request. Design-time (`MasterDbContextFactory`) layers `.AddEnvironmentVariables()`, so `Database__Provider` selects the engine for `dotnet ef` without editing tracked config |
> | **`DB-04` — provider-default divergence** | `AppDbContextFactory` now reads `Database:Provider` instead of `Tenants[0].DbProvider`, and `TenantProvisioningService` validates through `DatabaseProviderResolver.TryParse`. **Every silent SQL Server fallback is gone**; unknown providers throw |
> | **`DB-10` — `Tenant.Code` canonical semantics** | Tenant identity is canonicalized by the application (`Trim()` + `ToLowerInvariant()`), so it no longer depends on the engine's collation. See §16 and `DATABASE_ARCHITECTURE.md` |
> | **`DB-02` — PostgreSQL migration lineages** | One PostgreSQL-native baseline per context in `ERP.Persistence.PostgreSql/Migrations`: `MasterDb/InitialMasterBaseline` (P1-i, PR #24) and `AppDb/InitialTenantBaseline` (P1-j, PR #25). They do not replay SQL Server's history |
>
> **Partially open / still open:**
>
> | Gap | Evidence |
> |---|---|
> | **`DB-03` — provider parity: PARTIALLY OPEN** | Implemented by P1-k (PR #26): 59 offline `ProviderParityTests` and the live harness `tools/ERP.ParityCheck` (29 PASS, 4 EXPECTED DIFFERENCE, 0 FAIL, 0 SKIPPED, 1 DEFERRED). Still open: **`L-10` incremental-upgrade parity is DEFERRED** — no incremental PostgreSQL migration exists. Accepted differences and debt: `DATABASE_ARCHITECTURE.md` → *Provider Mapping* |
> | **`DB-09` — `Plant.Code` provider divergence** | No application canonicalization: case-insensitive on SQL Server, case-sensitive on PostgreSQL. Documented, **not resolved** |
> | **`DB-11` — deployment canonicality precheck** | Runbook documented (P1-l) in `TENANT_PROVISIONING.md`. Because `DB-10` canonicalizes reads, a non-canonically stored `Tenant.Code` is unreachable, so the **read-only** check must still be **run** before deploying to any unverified Master DB |
> | **`DB-08` — `ERP.DbMigrator` is a stub** | `tools/ERP.DbMigrator/Program.cs` prints four lines and exits; it parses no arguments. It is used only as the `dotnet ef` design-time startup project. **Deliberately untouched** |
>
> `TenantDbContextFactory` (tenant, runtime) already threw on an unknown provider and was
> **deliberately left unmodified**. These are implementation gaps against an approved
> requirement, **not open questions**. Tracked in
> `docs/SATY_ERP_MASTER_EXECUTION_PLAN.md` §6.2 (frontend repo), which is authoritative for
> status.

**Analogy:** A universal remote control. It can operate different TV brands (Samsung, LG, Sony) without changing the remote itself. You just tell it which TV to control. Our application code is the remote; the database providers (SQL Server, PostgreSQL) are the TVs. TenantDbContextFactory tells the remote which TV to talk to.

### How it works in our project

**Separate provider projects** keep database-specific code isolated:
```
ERP.Persistence.SqlServer/    --> SQL Server-specific config + migrations (2 Master + 3 tenant)
ERP.Persistence.PostgreSql/   --> PostgreSQL-specific config + migrations (1 Master + 1 tenant baseline)
```

**Parity verification (P1-k).** `tests/ERP.UnitTests/Persistence/ProviderParityTests.cs` compares
the two providers' models offline. `tools/ERP.ParityCheck` is a console harness:
`--self-test` and `--simulate` run fully **offline**; `--live` needs explicit authorization
**and** server-level endpoints in `ERP_PARITY_SQLSERVER` / `ERP_PARITY_POSTGRESQL`, and it only
ever creates and drops the approved disposable databases (`ERP_P1k_Parity_*_Throwaway`), refusing
protected names and pre-existing targets. A missing endpoint is reported SKIPPED, never PASS.

**SQL Server provider:**
```csharp
// File: src/Infrastructure/ERP.Persistence.SqlServer/Extensions/SqlServerServiceExtensions.cs
public static DbContextOptionsBuilder UseSqlServerProvider(
    this DbContextOptionsBuilder optionsBuilder, string connectionString)
{
    optionsBuilder.UseSqlServer(connectionString, sqlOptions =>
    {
        sqlOptions.MigrationsAssembly(typeof(SqlServerServiceExtensions).Assembly.FullName);
        sqlOptions.EnableRetryOnFailure(maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(5), ...);
        sqlOptions.CommandTimeout(30);
    });
    return optionsBuilder;
}
```

**PostgreSQL provider:**
```csharp
// File: src/Infrastructure/ERP.Persistence.PostgreSql/Extensions/PostgreSqlServiceExtensions.cs
public static DbContextOptionsBuilder UsePostgreSqlProvider(
    this DbContextOptionsBuilder optionsBuilder, string connectionString)
{
    optionsBuilder.UseNpgsql(connectionString, npgsqlOptions =>
    {
        npgsqlOptions.MigrationsAssembly(typeof(PostgreSqlServiceExtensions).Assembly.FullName);
        npgsqlOptions.EnableRetryOnFailure(maxRetryCount: 3, ...);
        npgsqlOptions.CommandTimeout(30);
    });
    return optionsBuilder;
}
```

**TenantDbContextFactory selects the right provider per request:**
```csharp
// File: src/Infrastructure/ERP.Infrastructure/Tenant/TenantDbContextFactory.cs
public AppDbContext CreateDbContext()
{
    var tenant = _tenantProvider.GetCurrentTenant();
    var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();

    switch (tenant.DbProvider.ToLower())
    {
        case "sqlserver":  optionsBuilder.UseSqlServerProvider(tenant.ConnectionString);  break;
        case "postgresql": optionsBuilder.UsePostgreSqlProvider(tenant.ConnectionString); break;
        default: throw new InvalidOperationException($"Unsupported provider: '{tenant.DbProvider}'");
    }

    return new AppDbContext(optionsBuilder.Options);
}
```

**Why separate migration assemblies?** SQL Server and PostgreSQL generate different DDL syntax. SQL Server uses `NVARCHAR(MAX)` while PostgreSQL uses `TEXT`. SQL Server uses `IDENTITY` for auto-increment while PostgreSQL uses `SERIAL`. Separate migration assemblies ensure each provider gets the correct SQL.

### Interview Q&A

**Q: How do you support multiple database providers in a single application?**
A: We use EF Core's provider-agnostic model with separate provider projects. The AppDbContext defines the schema using Fluent API (provider-agnostic). Each provider project (ERP.Persistence.SqlServer, ERP.Persistence.PostgreSql) contains provider-specific configuration and its own migration assembly. At runtime, TenantDbContextFactory reads the current tenant's DbProvider setting and creates an AppDbContext with the correct provider and connection string. The repository and business logic are completely unaware of which database they are talking to.

**Q: Why use separate projects for each database provider?**
A: Three reasons. (1) Isolation -- SQL Server-specific code does not pollute PostgreSQL code. (2) Migrations -- each provider generates different DDL, so migrations must be separate. (3) Deployment flexibility -- a client using only PostgreSQL does not need the SQL Server NuGet packages in their deployment.

---

## 24. Quick Reference Table

| Concept | Interface / Abstraction | Implementation | File Path |
|---------|------------------------|----------------|-----------|
| Clean Architecture | -- | Project structure | `src/Core/`, `src/Infrastructure/`, `src/Host/` |
| Modular Monolith | -- | Module folders | `src/Modules/Workflow/`, `src/Modules/Production/` |
| DbContext | -- | `AppDbContext` | `src/Infrastructure/ERP.Persistence/Context/AppDbContext.cs` |
| Repository | `IRepository<T>` | `Repository<T>` | `src/Core/ERP.Application.Abstractions/Persistence/IRepository.cs` / `src/Infrastructure/ERP.Persistence/Repositories/Repository.cs` |
| Unit of Work | `IUnitOfWork` | `UnitOfWork` | `src/Core/ERP.Application.Abstractions/Persistence/IUnitOfWork.cs` / `src/Infrastructure/ERP.Persistence/Repositories/UnitOfWork.cs` |
| DI Registration | -- | Extension methods | `src/Infrastructure/ERP.Persistence/Extensions/PersistenceServiceExtensions.cs` / `src/Infrastructure/ERP.Infrastructure/Extensions/InfrastructureServiceExtensions.cs` / `src/Infrastructure/ERP.Identity/Extensions/IdentityServiceExtensions.cs` |
| Composition Root | -- | `Program.cs` | `src/Host/ERP.Api/Program.cs` |
| Tenant Middleware | -- | `TenantMiddleware` | `src/Infrastructure/ERP.Infrastructure/Tenant/TenantMiddleware.cs` |
| Exception Middleware | -- | `GlobalExceptionMiddleware` | `src/Host/ERP.Api/Middleware/GlobalExceptionMiddleware.cs` |
| JWT Token Service | `ITokenService` | `TokenService` | `src/Infrastructure/ERP.Identity/Services/ITokenService.cs` / `src/Infrastructure/ERP.Identity/Services/TokenService.cs` |
| CQRS Commands | `ICommand<T>` | -- | `src/Core/ERP.Application.Abstractions/CQRS/ICommand.cs` |
| CQRS Queries | `IQuery<T>` | -- | `src/Core/ERP.Application.Abstractions/CQRS/IQuery.cs` |
| Validation Pipeline | `IPipelineBehavior` | `ValidationBehavior` | `src/Core/ERP.Application.Abstractions/Behaviors/ValidationBehavior.cs` |
| Logging Pipeline | `IPipelineBehavior` | `LoggingBehavior` | `src/Core/ERP.Application.Abstractions/Behaviors/LoggingBehavior.cs` |
| Cache Service | `ICacheService` | `MemoryCacheService` | `src/Core/ERP.Application.Abstractions/Caching/ICacheService.cs` / `src/Infrastructure/ERP.Infrastructure/Caching/MemoryCacheService.cs` |
| Tenant Provider | `ITenantProvider` | `HttpContextTenantProvider` | `src/Infrastructure/ERP.Infrastructure/Tenant/ITenantProvider.cs` / `src/Infrastructure/ERP.Infrastructure/Tenant/HttpContextTenantProvider.cs` |
| Tenant Store | `ITenantStore` | `ConfigurationTenantStore` | `src/Infrastructure/ERP.Infrastructure/Tenant/ITenantStore.cs` / `src/Infrastructure/ERP.Infrastructure/Tenant/ConfigurationTenantStore.cs` |
| Tenant DB Factory | `IDbContextFactory<AppDbContext>` | `TenantDbContextFactory` | `src/Infrastructure/ERP.Infrastructure/Tenant/TenantDbContextFactory.cs` |
| SignalR Hub | -- | `ErpNotificationHub` | `src/Infrastructure/ERP.Notifications/Hubs/ErpNotificationHub.cs` |
| Current User | `ICurrentUserService` | `CurrentUserService` | `src/Core/ERP.Application.Abstractions/Identity/ICurrentUserService.cs` |
| Audit Interceptor | `SaveChangesInterceptor` | `AuditableEntityInterceptor` | `src/Infrastructure/ERP.Persistence/Interceptors/AuditableEntityInterceptor.cs` |
| Domain Events | `SaveChangesInterceptor` | `DomainEventDispatcherInterceptor` | `src/Infrastructure/ERP.Persistence/Interceptors/DomainEventDispatcherInterceptor.cs` |
| Base Entity | -- | `BaseEntity` | `src/Core/ERP.SharedKernel/Entities/BaseEntity.cs` |
| Auditable Entity | -- | `AuditableEntity` | `src/Core/ERP.SharedKernel/Entities/AuditableEntity.cs` |
| SQL Server Provider | -- | `SqlServerServiceExtensions` | `src/Infrastructure/ERP.Persistence.SqlServer/Extensions/SqlServerServiceExtensions.cs` |
| PostgreSQL Provider | -- | `PostgreSqlServiceExtensions` | `src/Infrastructure/ERP.Persistence.PostgreSql/Extensions/PostgreSqlServiceExtensions.cs` |

---

*This document reflects the architecture as of .NET 8. All file paths are relative to the repository root (`erp_api/`).*
