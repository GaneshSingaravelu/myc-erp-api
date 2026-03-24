# Design Patterns in Our ERP API

> A practical, interview-ready guide to every design pattern used in this project.
> Built on **.NET 8** with **Clean Architecture**.

---

## Quick Reference Table

| # | Pattern | What It Does | Key Files |
|---|---------|-------------|-----------|
| 1 | Repository | Abstracts database access behind an interface | `IRepository<T>`, `Repository<T>` |
| 2 | Unit of Work | Batches multiple changes into one transaction | `IUnitOfWork`, `UnitOfWork` |
| 3 | CQRS | Separates reads from writes | `ICommand`, `IQuery`, handlers |
| 4 | Mediator | Decouples senders from handlers | MediatR in `Program.cs` |
| 5 | Factory | Creates objects with complex setup logic | `TenantDbContextFactory` |
| 6 | Strategy | Swap implementations without changing callers | `ICacheService` / `MemoryCacheService` |
| 7 | Middleware / Chain of Responsibility | Process requests through a pipeline | `TenantMiddleware`, `GlobalExceptionMiddleware` |
| 8 | Pipeline | Cross-cutting concerns before/after handlers | `ValidationBehavior`, `LoggingBehavior` |
| 9 | Result / Railway-Oriented Programming | Return success or error, never exceptions | `Result<T>`, `Error` |
| 10 | Value Object | Identity based on properties, not Id | `ValueObject`, `JobCardNumber` |
| 11 | Domain Events | Notify other parts when something happens | `IDomainEvent`, `DomainEventDispatcherInterceptor` |
| 12 | Dependency Inversion (DIP) | Depend on abstractions, not implementations | Interfaces in Application, implementations in Infrastructure |
| 13 | Guard Clause | Validate inputs at the boundary | `Guard.cs` |
| 14 | Soft Delete | Mark deleted instead of actually deleting | `AuditableEntity.IsDeleted` + global query filter |
| 15 | Layer Supertype | Shared base class for all entities | `BaseEntity`, `AuditableEntity` |
| 16 | Singleton / Scoped / Transient | DI lifetime management | Registration in extension methods |
| 17 | Extension Method | Modular DI registration | `AddPersistence()`, `AddInfrastructure()`, `AddIdentityServices()` |
| 18 | Options Pattern | Strongly-typed configuration | `JwtSettings` with `IOptions<T>` |
| 19 | Interceptor | Hook into EF Core save pipeline | `AuditableEntityInterceptor`, `DomainEventDispatcherInterceptor` |
| 20 | Composition Root | Single place where the app is assembled | `Program.cs` |

---

## How It All Fits Together (Architecture Overview)

```
  HTTP Request
       |
       v
  +------------------+
  |   Program.cs     |  <-- Composition Root (pattern #20)
  |  (Middleware)     |
  +------------------+
       |
       v
  +------------------+
  | GlobalException  |  <-- Middleware / Chain of Responsibility (#7)
  | TenantMiddleware |
  +------------------+
       |
       v
  +------------------+
  |   Controller     |  sends ICommand/IQuery via MediatR (#4)
  +------------------+
       |
       v
  +------------------+
  | ValidationBehavior| <-- Pipeline (#8)
  | LoggingBehavior  |
  +------------------+
       |
       v
  +------------------+
  | Command/Query    |  <-- CQRS (#3)
  |    Handler       |  uses IRepository (#1), IUnitOfWork (#2)
  +------------------+  returns Result<T> (#9)
       |
       v
  +------------------+
  |  Repository<T>   |  <-- Repository (#1), uses AppDbContext
  +------------------+
       |
       v
  +------------------+
  | AppDbContext      |  <-- created by TenantDbContextFactory (#5)
  | (Interceptors)   |  <-- Interceptor (#19): audit + domain events
  +------------------+
       |
       v
  +------------------+
  |    Database      |  SQL Server or PostgreSQL (per tenant)
  +------------------+
```

---

## 1. Repository Pattern

### What It Is
A Repository acts as an in-memory collection of domain objects. Your business logic works with it like a list -- it hides all the database details (SQL, EF Core, connection strings).

### Real-World Analogy
Think of a **library**. You ask the librarian (repository) for a book by title. You do not care whether the book is on shelf A3 or in the basement storage. The librarian knows where it is and gives it to you. That is the repository -- it hides *where* and *how* data is stored.

### How We Use It in This Project

| File | Layer | Role |
|------|-------|------|
| `src/Core/ERP.Application.Abstractions/Persistence/IRepository.cs` | Application | Interface (the contract) |
| `src/Infrastructure/ERP.Persistence/Repositories/Repository.cs` | Infrastructure | Implementation (uses EF Core) |

The Application layer only depends on `IRepository<T>`. It never references EF Core.

### Code Example

**The interface (what handlers see):**
```csharp
// src/Core/ERP.Application.Abstractions/Persistence/IRepository.cs
public interface IRepository<T> where T : BaseEntity
{
    Task<T?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<T>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<T>> FindAsync(Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default);
    Task AddAsync(T entity, CancellationToken cancellationToken = default);
    void Update(T entity);
    void Remove(T entity);
}
```

**The implementation (what EF Core does behind the scenes):**
```csharp
// src/Infrastructure/ERP.Persistence/Repositories/Repository.cs
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
        => await DbSet.FindAsync([id], cancellationToken);

    public async Task AddAsync(T entity, CancellationToken cancellationToken = default)
        => await DbSet.AddAsync(entity, cancellationToken);

    public void Update(T entity) => DbSet.Update(entity);
}
```

### Interview Tip
> "We use the Repository pattern to abstract data access. Our handlers depend on `IRepository<T>`, not on EF Core directly. This follows the Dependency Inversion Principle and makes our code testable -- in unit tests, we mock `IRepository<T>` instead of setting up a real database."

---

## 2. Unit of Work

### What It Is
Unit of Work coordinates saving multiple changes as a single database transaction. Either ALL changes succeed, or NONE of them are saved.

### Real-World Analogy
Imagine a **bank transfer**: you debit account A and credit account B. If crediting B fails, the debit on A must be rolled back. Unit of Work guarantees this all-or-nothing behavior.

### How We Use It in This Project

| File | Layer | Role |
|------|-------|------|
| `src/Core/ERP.Application.Abstractions/Persistence/IUnitOfWork.cs` | Application | Interface |
| `src/Infrastructure/ERP.Persistence/Repositories/UnitOfWork.cs` | Infrastructure | Implementation (wraps `DbContext.SaveChangesAsync`) |

### Code Example

```csharp
// src/Core/ERP.Application.Abstractions/Persistence/IUnitOfWork.cs
public interface IUnitOfWork : IDisposable
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
    Task BeginTransactionAsync(CancellationToken cancellationToken = default);
    Task CommitTransactionAsync(CancellationToken cancellationToken = default);
    Task RollbackTransactionAsync(CancellationToken cancellationToken = default);
}
```

**How a handler uses it:**
```csharp
// In a command handler:
await _jobCardRepo.AddAsync(newJobCard);       // tracked, NOT saved yet
_workOrderRepo.Update(workOrder);               // tracked, NOT saved yet
await _unitOfWork.SaveChangesAsync(ct);         // NOW both are saved atomically
```

### Interview Tip
> "We use Unit of Work to ensure transactional consistency. Multiple repository operations are batched and committed together via `SaveChangesAsync()`. Under the hood, EF Core's DbContext *is* a Unit of Work. Our `IUnitOfWork` interface wraps it so handlers don't depend on EF Core directly."

---

## 3. CQRS (Command Query Responsibility Segregation)

### What It Is
CQRS means: **separate your read operations from your write operations**. Commands change data. Queries read data. They have different interfaces, different handlers, and can be optimized independently.

### Real-World Analogy
At a **restaurant**, the kitchen (write side) handles orders -- it creates food, modifies dishes. The display board (read side) shows what is available -- it never changes the menu. Different staff, different optimizations, same underlying data.

### How We Use It in This Project

| File | Layer | Role |
|------|-------|------|
| `src/Core/ERP.Application.Abstractions/CQRS/ICommand.cs` | Application | Command marker interface |
| `src/Core/ERP.Application.Abstractions/CQRS/ICommandHandler.cs` | Application | Command handler interface |
| `src/Core/ERP.Application.Abstractions/CQRS/IQuery.cs` | Application | Query marker interface |
| `src/Core/ERP.Application.Abstractions/CQRS/IQueryHandler.cs` | Application | Query handler interface |

### Code Example

```csharp
// src/Core/ERP.Application.Abstractions/CQRS/ICommand.cs
public interface ICommand : IRequest<Result> { }
public interface ICommand<TResponse> : IRequest<Result<TResponse>> { }

// src/Core/ERP.Application.Abstractions/CQRS/IQuery.cs
public interface IQuery<TResponse> : IRequest<Result<TResponse>> { }
```

**Using CQRS in practice:**
```csharp
// COMMAND (writes data):
public record CreateJobCardCommand(Guid WorkOrderId, int Quantity) : ICommand<Guid>;

public class CreateJobCardHandler : ICommandHandler<CreateJobCardCommand, Guid>
{
    public async Task<Result<Guid>> Handle(CreateJobCardCommand cmd, CancellationToken ct)
    {
        var jobCard = new JobCard(cmd.WorkOrderId, cmd.Quantity);
        await _repo.AddAsync(jobCard, ct);
        return jobCard.Id;  // implicit conversion to Result<Guid>
    }
}

// QUERY (reads data):
public record GetJobCardByIdQuery(Guid Id) : IQuery<JobCardDto>;

public class GetJobCardByIdHandler : IQueryHandler<GetJobCardByIdQuery, JobCardDto>
{
    public async Task<Result<JobCardDto>> Handle(GetJobCardByIdQuery query, CancellationToken ct)
    {
        var jobCard = await _repo.GetByIdAsync(query.Id, ct);
        if (jobCard is null)
            return Result.Failure<JobCardDto>(Error.NotFound("JobCard", query.Id));
        return new JobCardDto(jobCard.Id, jobCard.Number);
    }
}
```

### Interview Tip
> "We use CQRS to separate reads and writes. Commands return `Result` (success/failure), queries return `Result<TDto>`. Each has exactly one handler, routed via MediatR. This gives us single responsibility, easy testing, and the ability to optimize reads and writes independently -- for example, queries can skip validation and use caching."

---

## 4. Mediator Pattern

### What It Is
The Mediator pattern decouples the sender of a request from its handler. Instead of the controller calling the handler directly, it sends a message through a mediator, which routes it to the correct handler.

### Real-World Analogy
Think of an **air traffic control tower**. Planes (controllers) do not talk to each other directly. They send messages to the tower (mediator), which coordinates everything. This prevents chaos and keeps each plane focused on flying.

### How We Use It in This Project

We use the **MediatR** library. It is configured in `Program.cs`:

```csharp
// src/Host/ERP.Api/Program.cs
builder.Services.AddMediatR(cfg =>
{
    cfg.RegisterServicesFromAssembly(typeof(ValidationBehavior<,>).Assembly);
    cfg.AddBehavior(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
    cfg.AddBehavior(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));
});
```

**Flow:**
```
Controller                    MediatR                      Handler
    |                            |                            |
    |--- Send(CreateJobCard) --> |                            |
    |                            |--- routes to handler ----> |
    |                            |                            |--- executes logic
    |                            | <--- returns Result ------ |
    | <--- returns Result ------ |                            |
```

### Code Example

```csharp
// In a controller:
[HttpPost]
public async Task<IActionResult> Create(CreateJobCardRequest request)
{
    var command = new CreateJobCardCommand(request.WorkOrderId, request.Quantity);
    var result = await _mediator.Send(command);  // MediatR finds the handler
    return result.IsSuccess ? Ok(result.Value) : BadRequest(result.Error);
}
```

### Interview Tip
> "We use the Mediator pattern via MediatR to decouple controllers from handlers. The controller does not know which handler processes a command -- MediatR routes it automatically. This also lets us add pipeline behaviors (validation, logging) that run before every handler, without modifying any handler code."

---

## 5. Factory Pattern

### What It Is
The Factory pattern creates objects when the creation logic is complex or depends on runtime conditions. Instead of using `new` directly, a factory method decides *which* object to create and *how* to configure it.

### Real-World Analogy
Ordering a **pizza by phone**. You say "large pepperoni." The pizza shop (factory) decides which dough to use, which oven, how long to bake. You get a finished pizza without knowing the details. The factory encapsulates the complex creation process.

### How We Use It in This Project

| File | Role |
|------|------|
| `src/Infrastructure/ERP.Infrastructure/Tenant/TenantDbContextFactory.cs` | Creates `AppDbContext` with the correct connection string and database provider per tenant |

This is critical for multi-tenancy: each tenant can use a different database (SQL Server or PostgreSQL), so the factory reads the current tenant's config and creates the right `DbContext`.

### Code Example

```csharp
// src/Infrastructure/ERP.Infrastructure/Tenant/TenantDbContextFactory.cs
public class TenantDbContextFactory : IDbContextFactory<AppDbContext>
{
    private readonly ITenantProvider _tenantProvider;

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
            default:
                throw new InvalidOperationException(
                    $"Unsupported database provider: '{tenant.DbProvider}'");
        }

        return new AppDbContext(optionsBuilder.Options);
    }
}
```

**Registered in DI:**
```csharp
// src/Infrastructure/ERP.Infrastructure/Extensions/InfrastructureServiceExtensions.cs
services.AddScoped<IDbContextFactory<AppDbContext>, TenantDbContextFactory>();
services.AddScoped(sp =>
    sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext());
```

### Interview Tip
> "We use the Factory pattern for multi-tenant database connections. Each tenant can use SQL Server or PostgreSQL, so `TenantDbContextFactory` reads the current tenant's configuration and creates a `DbContext` with the correct provider and connection string. The rest of the code never knows which database it is talking to."

---

## 6. Strategy Pattern

### What It Is
The Strategy pattern lets you swap out an algorithm or implementation at runtime without changing the code that uses it. You program against an interface, and the DI container provides the right implementation.

### Real-World Analogy
Think of **navigation apps**. You choose a strategy: fastest route, shortest route, or avoid highways. The app uses whichever strategy you select, but the destination stays the same. The strategy is swappable.

### How We Use It in This Project

| File | Layer | Role |
|------|-------|------|
| `src/Core/ERP.Application.Abstractions/Caching/ICacheService.cs` | Application | Interface (strategy contract) |
| `src/Infrastructure/ERP.Infrastructure/Caching/MemoryCacheService.cs` | Infrastructure | Concrete strategy (in-memory) |

Today we use `MemoryCacheService`. When we need distributed caching, we create `RedisCacheService` implementing the same `ICacheService` interface and swap it in DI -- zero changes to business logic.

### Code Example

```csharp
// src/Core/ERP.Application.Abstractions/Caching/ICacheService.cs
public interface ICacheService
{
    Task<T?> GetAsync<T>(string key, CancellationToken ct = default);
    Task SetAsync<T>(string key, T value, TimeSpan? expiration = null, CancellationToken ct = default);
    Task RemoveAsync(string key, CancellationToken ct = default);
    Task<T?> GetOrCreateAsync<T>(string key, Func<Task<T>> factory, TimeSpan? expiration = null, CancellationToken ct = default);
}
```

**Swapping strategy is a one-line change:**
```csharp
// Current (in-memory):
services.AddSingleton<ICacheService, MemoryCacheService>();

// Future (Redis -- just change this line):
// services.AddSingleton<ICacheService, RedisCacheService>();
```

### Interview Tip
> "Our caching uses the Strategy pattern. Business logic depends on `ICacheService`, not a specific cache implementation. Right now we use `MemoryCacheService` for simplicity. When we scale to multiple instances, we swap in a `RedisCacheService` -- one line change in DI registration, zero changes to handlers."

---

## 7. Middleware Pattern / Chain of Responsibility

### What It Is
Middleware components form a pipeline that every HTTP request passes through. Each middleware can process the request, pass it to the next middleware, or short-circuit the pipeline entirely. This is the Chain of Responsibility pattern -- each handler in the chain decides whether to handle the request or pass it along.

### Real-World Analogy
Think of an **airport security line**. You pass through multiple checkpoints: ticket check, ID verification, metal detector, bag scan. Each checkpoint can reject you (short-circuit) or let you through to the next one. That is Chain of Responsibility.

### How We Use It in This Project

| File | What It Does |
|------|-------------|
| `src/Host/ERP.Api/Middleware/GlobalExceptionMiddleware.cs` | Catches all unhandled exceptions, returns clean JSON |
| `src/Infrastructure/ERP.Infrastructure/Tenant/TenantMiddleware.cs` | Reads `X-Tenant-Id` header, resolves tenant, short-circuits if missing |

**Pipeline order in Program.cs:**
```
Request --> [GlobalException] --> [CORS] --> [Auth] --> [Tenant] --> [Controller]
Response <-- [GlobalException] <-- [CORS] <-- [Auth] <-- [Tenant] <-- [Controller]
```

### Code Example

**TenantMiddleware -- can short-circuit the pipeline:**
```csharp
// src/Infrastructure/ERP.Infrastructure/Tenant/TenantMiddleware.cs
public class TenantMiddleware
{
    private readonly RequestDelegate _next;

    public async Task InvokeAsync(HttpContext context, ITenantStore tenantStore, IMemoryCache cache)
    {
        // Short-circuit: return 400 if header is missing
        if (!context.Request.Headers.TryGetValue("X-Tenant-Id", out var tenantId))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { error = "Missing X-Tenant-Id header" });
            return;  // Pipeline stops here -- controller never runs
        }

        // Resolve tenant, store in HttpContext.Items
        context.Items["CurrentTenant"] = tenant;

        // Pass to next middleware
        await _next(context);
    }
}
```

**GlobalExceptionMiddleware -- wraps the entire pipeline:**
```csharp
// src/Host/ERP.Api/Middleware/GlobalExceptionMiddleware.cs
public class GlobalExceptionMiddleware
{
    private readonly RequestDelegate _next;

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);  // Let the rest of the pipeline run
        }
        catch (Exception ex)
        {
            // Catch any unhandled exception from downstream
            context.Response.StatusCode = 500;
            await context.Response.WriteAsJsonAsync(new
            {
                statusCode = 500,
                message = "An internal server error occurred.",
                traceId = context.TraceIdentifier
            });
        }
    }
}
```

### Interview Tip
> "ASP.NET Core's middleware pipeline implements Chain of Responsibility. Each middleware can process the request, pass it to the next, or short-circuit. We use `TenantMiddleware` to resolve the tenant early in the pipeline -- if the tenant header is missing, we return 400 immediately. `GlobalExceptionMiddleware` wraps everything to catch unhandled exceptions and return consistent JSON errors."

---

## 8. Pipeline Pattern (MediatR Behaviors)

### What It Is
Pipeline behaviors are like middleware, but for MediatR commands and queries instead of HTTP requests. They intercept every command/query before it reaches the handler, letting you add cross-cutting concerns (validation, logging, caching) without modifying any handler.

### Real-World Analogy
Like a **car assembly line**. The car (request) passes through multiple stations: painting, quality check, wiring. Each station adds something, and the car only reaches the customer (handler) after passing through all stations. If quality check fails, the car never reaches the customer.

### How We Use It in This Project

| File | What It Does |
|------|-------------|
| `src/Core/ERP.Application.Abstractions/Behaviors/ValidationBehavior.cs` | Runs FluentValidation rules; returns error if validation fails (handler never runs) |
| `src/Core/ERP.Application.Abstractions/Behaviors/LoggingBehavior.cs` | Logs request start/end with execution time; warns if > 500ms |

```
Request Flow:
  Controller --> MediatR.Send(command)
    --> [ValidationBehavior]  <-- validates the command (returns error if invalid)
      --> [LoggingBehavior]   <-- logs timing
        --> [Handler]         <-- actual business logic
```

### Code Example

**ValidationBehavior -- stops bad requests before they reach the handler:**
```csharp
// src/Core/ERP.Application.Abstractions/Behaviors/ValidationBehavior.cs
public sealed class ValidationBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
    where TResponse : Result
{
    private readonly IEnumerable<IValidator<TRequest>> _validators;

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        if (!_validators.Any())
            return await next();  // No validators? Skip validation.

        var context = new ValidationContext<TRequest>(request);
        var results = await Task.WhenAll(
            _validators.Select(v => v.ValidateAsync(context, cancellationToken)));

        var errors = results.SelectMany(r => r.Errors).Where(e => e is not null).ToList();

        if (errors.Count != 0)
        {
            // Return failure -- handler NEVER runs
            var error = Error.Validation("Validation.Failed",
                string.Join("; ", errors.Select(e => e.ErrorMessage)));
            return (TResponse)(object)Result.Failure(error);
        }

        return await next();  // Validation passed -- proceed to handler
    }
}
```

**LoggingBehavior -- measures execution time:**
```csharp
// src/Core/ERP.Application.Abstractions/Behaviors/LoggingBehavior.cs
public sealed class LoggingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct)
    {
        _logger.LogInformation("Handling {RequestName}: {@Request}", typeof(TRequest).Name, request);

        var stopwatch = Stopwatch.StartNew();
        var response = await next();
        stopwatch.Stop();

        if (stopwatch.ElapsedMilliseconds > 500)
            _logger.LogWarning("SLOW REQUEST: {RequestName} took {ElapsedMs}ms",
                typeof(TRequest).Name, stopwatch.ElapsedMilliseconds);

        return response;
    }
}
```

### Interview Tip
> "We use MediatR pipeline behaviors for cross-cutting concerns. `ValidationBehavior` runs FluentValidation rules before every command -- if validation fails, the handler never executes. `LoggingBehavior` measures execution time and warns on slow requests. This keeps handlers focused on business logic only."

---

## 9. Result Pattern / Railway-Oriented Programming

### What It Is
Instead of throwing exceptions for business failures, methods return a `Result<T>` that is either a success (with a value) or a failure (with an error). The method signature tells you it can fail, and the compiler nudges you to check.

### Real-World Analogy
Think of a **medical test result**. The result is either "positive" (found something, here are the details) or "negative" (nothing found). The doctor does not throw a tantrum when the test is negative -- it is an expected outcome, not an error.

Similarly, "user not found" is an expected business outcome, not an exception.

### How We Use It in This Project

| File | Role |
|------|------|
| `src/Core/ERP.SharedKernel/Results/Result.cs` | `Result` and `Result<T>` classes |
| `src/Core/ERP.SharedKernel/Results/Error.cs` | Structured error type |

### Code Example

```csharp
// src/Core/ERP.SharedKernel/Results/Result.cs
public class Result
{
    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;
    public Error Error { get; }

    public static Result Success() => new(true, Error.None);
    public static Result Failure(Error error) => new(false, error);
    public static Result<TValue> Success<TValue>(TValue value) => new(value, true, Error.None);
    public static Result<TValue> Failure<TValue>(Error error) => new(default, false, error);
}

public class Result<TValue> : Result
{
    public TValue Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException("Cannot access Value of a failure result.");

    // Implicit conversion: return a value directly as Result<T>
    public static implicit operator Result<TValue>(TValue? value) =>
        value is not null ? Success(value) : Failure<TValue>(Error.NullValue);
}
```

```csharp
// src/Core/ERP.SharedKernel/Results/Error.cs
public sealed record Error(string Code, string Message)
{
    public static readonly Error None = new(string.Empty, string.Empty);
    public static Error NotFound(string entityName, object id)
        => new($"{entityName}.NotFound", $"{entityName} with Id '{id}' was not found.");
    public static Error Validation(string code, string message)
        => new(code, message);
}
```

**In a handler:**
```csharp
public async Task<Result<JobCardDto>> Handle(GetJobCardByIdQuery query, CancellationToken ct)
{
    var jobCard = await _repo.GetByIdAsync(query.Id, ct);
    if (jobCard is null)
        return Result.Failure<JobCardDto>(Error.NotFound("JobCard", query.Id));
    return new JobCardDto(jobCard.Id, jobCard.Number);  // implicit conversion
}
```

**In a controller:**
```csharp
var result = await _mediator.Send(command);
return result.IsSuccess ? Ok(result.Value) : BadRequest(result.Error);
```

### Interview Tip
> "We use the Result pattern instead of exceptions for business rule violations. Exceptions are reserved for truly unexpected failures (database down, network error). Business outcomes like 'not found' or 'validation failed' are expected -- they return `Result.Failure` with a structured `Error`. This makes the code more explicit, avoids try/catch blocks everywhere, and maps cleanly to HTTP status codes."

---

## 10. Value Object Pattern

### What It Is
A Value Object is defined by its properties, not by a unique identity. Two value objects with the same data are considered equal, regardless of when or where they were created. They are immutable -- once created, they do not change.

### Real-World Analogy
**Money**. A 100 rupee note is a 100 rupee note, no matter which specific bill you hold. You care about the *amount and currency*, not the serial number. Two `Money(100, "INR")` objects are equal.

Contrast with an **Entity**: two employees named "John Smith" are different people -- identity matters.

### How We Use It in This Project

| File | Role |
|------|------|
| `src/Core/ERP.SharedKernel/ValueObjects/ValueObject.cs` | Abstract base class with equality based on components |

### Code Example

```csharp
// src/Core/ERP.SharedKernel/ValueObjects/ValueObject.cs
public abstract class ValueObject
{
    protected abstract IEnumerable<object?> GetEqualityComponents();

    public override bool Equals(object? obj)
    {
        if (obj is null || obj.GetType() != GetType()) return false;
        var other = (ValueObject)obj;
        return GetEqualityComponents().SequenceEqual(other.GetEqualityComponents());
    }

    public override int GetHashCode()
    {
        return GetEqualityComponents()
            .Select(x => x?.GetHashCode() ?? 0)
            .Aggregate((x, y) => x ^ y);
    }

    public static bool operator ==(ValueObject? left, ValueObject? right) { ... }
    public static bool operator !=(ValueObject? left, ValueObject? right) => !(left == right);
}
```

**A concrete value object (example for a future module):**
```csharp
public class JobCardNumber : ValueObject
{
    public string Value { get; }

    public JobCardNumber(string value)
    {
        Guard.AgainstNullOrEmpty(value, nameof(value));
        Value = value;
    }

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }
}

// Usage:
var num1 = new JobCardNumber("260322N001S000R00");
var num2 = new JobCardNumber("260322N001S000R00");
Console.WriteLine(num1 == num2);  // true -- same value = equal
```

### Interview Tip
> "Value Objects are compared by their properties, not by identity. We use them for things like `JobCardNumber`, `Money`, and `Address` -- where the value itself is what matters. They are immutable and validated on creation. Our `ValueObject` base class overrides `Equals` and `GetHashCode` to compare by components, so two objects with the same data are always equal."

---

## 11. Domain Events Pattern

### What It Is
When something important happens in the domain (a job card is completed, an order is placed), the entity raises a domain event. Other parts of the system subscribe to these events and react, without the original code knowing about them.

### Real-World Analogy
**Radio broadcasting**. When something newsworthy happens, the station broadcasts it. Anyone with a radio (subscriber) hears it and decides what to do. The station does not know who is listening or how many listeners there are.

### How We Use It in This Project

| File | Role |
|------|------|
| `src/Core/ERP.SharedKernel/DomainEvents/IDomainEvent.cs` | Event marker interface (extends MediatR `INotification`) |
| `src/Core/ERP.SharedKernel/DomainEvents/DomainEvent.cs` | Base record with `OccurredAt` timestamp |
| `src/Core/ERP.SharedKernel/Entities/BaseEntity.cs` | Entities hold a `_domainEvents` list |
| `src/Infrastructure/ERP.Persistence/Interceptors/DomainEventDispatcherInterceptor.cs` | Dispatches events AFTER `SaveChanges` succeeds |

**Flow:**
```
1. Handler modifies entity --> entity.AddDomainEvent(new JobCardCompletedEvent(...))
2. SaveChangesAsync() is called
3. Data saved to database
4. DomainEventDispatcherInterceptor collects events from saved entities
5. Publishes each event via MediatR
6. Event handlers react (send notification, update dashboard, etc.)
```

### Code Example

```csharp
// src/Core/ERP.SharedKernel/DomainEvents/IDomainEvent.cs
public interface IDomainEvent : INotification
{
    DateTime OccurredAt { get; }
}

// src/Core/ERP.SharedKernel/DomainEvents/DomainEvent.cs
public abstract record DomainEvent : IDomainEvent
{
    public DateTime OccurredAt { get; } = DateTime.UtcNow;
}
```

**Entity raises an event:**
```csharp
// In a domain entity:
public void Complete(int approvedQuantity)
{
    Status = JobCardStatus.Completed;
    ApprovedQuantity = approvedQuantity;
    AddDomainEvent(new JobCardCompletedEvent(Id, approvedQuantity));
}
```

**Interceptor dispatches after save:**
```csharp
// src/Infrastructure/ERP.Persistence/Interceptors/DomainEventDispatcherInterceptor.cs
public override async ValueTask<int> SavedChangesAsync(...)
{
    var domainEvents = context.ChangeTracker
        .Entries<BaseEntity>()
        .SelectMany(entry =>
        {
            var events = entry.Entity.DomainEvents.ToList();
            entry.Entity.ClearDomainEvents();
            return events;
        }).ToList();

    foreach (var domainEvent in domainEvents)
        await _publisher.Publish(domainEvent, cancellationToken);

    return result;
}
```

### Interview Tip
> "We use Domain Events to decouple side effects from business logic. When a job card is completed, the entity raises a `JobCardCompletedEvent`. The `DomainEventDispatcherInterceptor` publishes it AFTER the database save succeeds. Event handlers then send notifications, update dashboards, etc. The entity never knows about these subscribers -- this follows the Open/Closed Principle."

---

## 12. Dependency Inversion Principle (DIP)

### What It Is
High-level modules should not depend on low-level modules. Both should depend on abstractions. In practice: define interfaces in the Application layer, implement them in the Infrastructure layer.

### Real-World Analogy
**Electrical outlets and plugs**. Your laptop (high-level) does not depend on a specific power plant (low-level). It depends on a standard plug (abstraction). Any outlet that matches the plug specification works. The power source can be solar, nuclear, or coal -- your laptop does not care.

### How We Use It in This Project

```
APPLICATION LAYER (defines interfaces):       INFRASTRUCTURE LAYER (implements them):
  IRepository<T>                         -->    Repository<T>
  IUnitOfWork                            -->    UnitOfWork
  ICacheService                          -->    MemoryCacheService
  ICurrentUserService                    -->    CurrentUserService
  ITokenService                          -->    TokenService
```

**Key file pairs:**

| Interface (Application) | Implementation (Infrastructure) |
|------------------------|---------------------------------|
| `src/Core/ERP.Application.Abstractions/Persistence/IRepository.cs` | `src/Infrastructure/ERP.Persistence/Repositories/Repository.cs` |
| `src/Core/ERP.Application.Abstractions/Persistence/IUnitOfWork.cs` | `src/Infrastructure/ERP.Persistence/Repositories/UnitOfWork.cs` |
| `src/Core/ERP.Application.Abstractions/Caching/ICacheService.cs` | `src/Infrastructure/ERP.Infrastructure/Caching/MemoryCacheService.cs` |
| `src/Core/ERP.Application.Abstractions/Identity/ICurrentUserService.cs` | `src/Infrastructure/ERP.Infrastructure/Tenant/CurrentUserService.cs` |

**Why the arrows point inward:**
```
   Presentation (API)
         |
         v
   Application (Handlers, Interfaces)  <--- owns the interfaces
         ^
         |
   Infrastructure (EF Core, Redis, JWT) <--- implements them
```

The Application layer never references Infrastructure. Infrastructure references Application to implement its interfaces. The DI container (in `Program.cs`) wires them together.

### Interview Tip
> "We follow the Dependency Inversion Principle strictly. Interfaces like `IRepository<T>` and `ICacheService` live in the Application layer. Implementations like `Repository<T>` and `MemoryCacheService` live in Infrastructure. The Application layer has zero knowledge of EF Core, SQL Server, or any framework. This makes it testable and swappable."

---

## 13. Guard Clause Pattern

### What It Is
Guard clauses validate method inputs at the very beginning and throw immediately if something is wrong. This eliminates deeply nested `if` statements and makes methods flat and readable.

### Real-World Analogy
A **bouncer at a club**. Before you enter, the bouncer checks your ID, dress code, and guest list. If anything fails, you are rejected immediately. The bouncer does not let you in and then check your ID inside.

### How We Use It in This Project

| File | Role |
|------|------|
| `src/Core/ERP.SharedKernel/Guards/Guard.cs` | Static class with guard methods |

### Code Example

```csharp
// src/Core/ERP.SharedKernel/Guards/Guard.cs
public static class Guard
{
    public static void AgainstNull(object? value, string paramName)
    {
        if (value is null)
            throw new ArgumentNullException(paramName, $"{paramName} cannot be null.");
    }

    public static void AgainstNullOrEmpty(string? value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{paramName} cannot be null or empty.", paramName);
    }

    public static void AgainstZeroOrNegative(int value, string paramName)
    {
        if (value <= 0)
            throw new ArgumentOutOfRangeException(paramName, $"{paramName} must be positive.");
    }

    public static void AgainstOutOfRange(int value, int min, int max, string paramName)
    {
        if (value < min || value > max)
            throw new ArgumentOutOfRangeException(paramName,
                $"{paramName} must be between {min} and {max}. Got: {value}");
    }
}
```

**Usage in a domain entity constructor:**
```csharp
public class JobCard : AuditableEntity
{
    public JobCard(Guid workOrderId, int quantity, string operationCode)
    {
        Guard.AgainstNull(workOrderId, nameof(workOrderId));
        Guard.AgainstZeroOrNegative(quantity, nameof(quantity));
        Guard.AgainstNullOrEmpty(operationCode, nameof(operationCode));

        WorkOrderId = workOrderId;
        Quantity = quantity;
        OperationCode = operationCode;
    }
}
```

### Interview Tip
> "We use Guard Clauses to fail fast on invalid inputs. Domain entity constructors and value objects validate their arguments immediately using `Guard.AgainstNull()`, `Guard.AgainstZeroOrNegative()`, etc. This keeps validation at the boundary and prevents invalid objects from ever being created."

---

## 14. Soft Delete Pattern

### What It Is
Instead of permanently deleting records from the database (`DELETE FROM`), you set an `IsDeleted = true` flag. The record stays in the database but is hidden from all normal queries.

### Real-World Analogy
**Moving files to the Recycle Bin** instead of permanently deleting them. The files are hidden from your desktop view, but they are still there. You can restore them if needed, and you have an audit trail of what was deleted.

### How We Use It in This Project

| File | Role |
|------|------|
| `src/Core/ERP.SharedKernel/Entities/AuditableEntity.cs` | Has `IsDeleted`, `DeletedAt`, `DeletedBy` properties |
| `src/Infrastructure/ERP.Persistence/Context/AppDbContext.cs` | Applies global query filter: `WHERE IsDeleted = false` |
| `src/Infrastructure/ERP.Persistence/Interceptors/AuditableEntityInterceptor.cs` | Converts `Delete` to `Update` with `IsDeleted = true` |

**How it works end-to-end:**

```
1. Handler calls: _repo.Remove(jobCard);
2. EF Core marks entity as EntityState.Deleted
3. AuditableEntityInterceptor intercepts:
   - Changes state from Deleted to Modified
   - Sets IsDeleted = true, DeletedAt = now, DeletedBy = currentUser
4. SaveChanges generates: UPDATE JobCards SET IsDeleted=1, DeletedAt=..., DeletedBy=... WHERE Id=...
5. All future queries auto-filter: WHERE IsDeleted = 0 (via global query filter)
```

### Code Example

**Global query filter in AppDbContext:**
```csharp
// src/Infrastructure/ERP.Persistence/Context/AppDbContext.cs
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    foreach (var entityType in modelBuilder.Model.GetEntityTypes())
    {
        if (typeof(AuditableEntity).IsAssignableFrom(entityType.ClrType))
        {
            modelBuilder.Entity(entityType.ClrType)
                .HasQueryFilter(CreateSoftDeleteFilter(entityType.ClrType));
                // Creates: entity => entity.IsDeleted == false
        }
    }
}
```

**Interceptor converts hard delete to soft delete:**
```csharp
// src/Infrastructure/ERP.Persistence/Interceptors/AuditableEntityInterceptor.cs
case EntityState.Deleted:
    entry.State = EntityState.Modified;  // Don't actually delete!
    entry.Entity.IsDeleted = true;
    entry.Entity.DeletedAt = now;
    entry.Entity.DeletedBy = userId;
    break;
```

### Interview Tip
> "We use Soft Delete with EF Core global query filters. When `Remove()` is called, our interceptor converts it to an update that sets `IsDeleted = true`. A global query filter on `AuditableEntity` automatically excludes deleted records from all queries. This preserves data for audit compliance and prevents broken foreign keys. To see deleted records, we use `IgnoreQueryFilters()`."

---

## 15. Layer Supertype Pattern

### What It Is
A base class that provides common fields and behavior shared by all entities in a layer. Instead of repeating `Id`, `CreatedAt`, `CreatedBy` in every entity, define them once in a base class.

### Real-World Analogy
A **government ID card template**. Every citizen's card has the same format: name field, photo area, ID number, issue date. Individual cards fill in different data, but the template (base class) is shared.

### How We Use It in This Project

```
BaseEntity (Id, DomainEvents)
    |
    +-- AuditableEntity (CreatedAt, CreatedBy, LastModifiedAt, LastModifiedBy, IsDeleted, DeletedAt, DeletedBy)
            |
            +-- JobCard (specific entity with its own properties)
            +-- WorkOrder
            +-- Department
            +-- ... every domain entity
```

| File | Role |
|------|------|
| `src/Core/ERP.SharedKernel/Entities/BaseEntity.cs` | Id + domain event collection |
| `src/Core/ERP.SharedKernel/Entities/AuditableEntity.cs` | Audit fields + soft delete |

### Code Example

```csharp
// src/Core/ERP.SharedKernel/Entities/BaseEntity.cs
public abstract class BaseEntity
{
    public Guid Id { get; protected set; } = Guid.NewGuid();

    private readonly List<IDomainEvent> _domainEvents = [];
    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    public void AddDomainEvent(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);
    public void ClearDomainEvents() => _domainEvents.Clear();
}

// src/Core/ERP.SharedKernel/Entities/AuditableEntity.cs
public abstract class AuditableEntity : BaseEntity
{
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? LastModifiedAt { get; set; }
    public string? LastModifiedBy { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }
    public string? DeletedBy { get; set; }
}
```

### Interview Tip
> "We use the Layer Supertype pattern with `BaseEntity` and `AuditableEntity`. Every domain entity inherits from `AuditableEntity`, which gives it an Id, audit fields, soft delete, and domain event support -- all automatically. This eliminates repetition and ensures consistency. The `IRepository<T>` constraint `where T : BaseEntity` guarantees only entities can be stored."

---

## 16. Singleton, Scoped, and Transient (DI Lifetimes)

### What It Is
When you register a service in the DI container, you choose how long it lives:

| Lifetime | How Long It Lives | Created | Example |
|----------|------------------|---------|---------|
| **Singleton** | Entire application lifetime | Once, shared by all requests | `ICacheService`, `ITenantStore`, `ITokenService` |
| **Scoped** | One HTTP request | Once per request, disposed at end | `AppDbContext`, `IRepository<T>`, `IUnitOfWork`, `ICurrentUserService` |
| **Transient** | Each injection | New every time it is requested | Lightweight, stateless helpers |

### Real-World Analogy
- **Singleton** = A town's **water tower**. One for the entire town, shared by all houses.
- **Scoped** = A **waiter** assigned to your table. One per table (request), serves only you, leaves when you are done.
- **Transient** = **Disposable napkins**. New one every time you need it, thrown away immediately.

### How We Use It in This Project

**From `PersistenceServiceExtensions.cs`:**
```csharp
// Scoped: one per request (tied to DbContext lifecycle)
services.AddScoped<AuditableEntityInterceptor>();
services.AddScoped<DomainEventDispatcherInterceptor>();
services.AddScoped(typeof(IRepository<>), typeof(Repository<>));
services.AddScoped<IUnitOfWork, UnitOfWork>();
```

**From `InfrastructureServiceExtensions.cs`:**
```csharp
// Singleton: one for entire app (must be thread-safe!)
services.AddSingleton<ITenantStore, ConfigurationTenantStore>();
services.AddSingleton<ICacheService, MemoryCacheService>();

// Scoped: one per request
services.AddScoped<ITenantProvider, HttpContextTenantProvider>();
services.AddScoped<ICurrentUserService, CurrentUserService>();
```

**From `IdentityServiceExtensions.cs`:**
```csharp
// Singleton: token service is stateless, reads config once
services.AddSingleton<ITokenService, TokenService>();
```

### Important Rule: Never inject a shorter-lived service into a longer-lived one

```
WRONG: Singleton depends on Scoped
  MemoryCacheService (Singleton) injecting ICurrentUserService (Scoped) -- BREAKS!

RIGHT: Scoped depends on Singleton
  Repository (Scoped) injecting ICacheService (Singleton) -- fine
```

### Interview Tip
> "In ASP.NET Core, there are three DI lifetimes. Singleton is created once for the entire app -- we use it for caching and configuration. Scoped is created once per HTTP request -- we use it for DbContext, repositories, and the current user service. Transient is created every time it is requested. The key rule: never inject a scoped service into a singleton, because the scoped service would be held forever and become stale."

---

## 17. Extension Method Pattern

### What It Is
An extension method adds new methods to an existing type without modifying it. In our project, each infrastructure project defines an `AddXxx()` extension method on `IServiceCollection` to register its own services.

### Real-World Analogy
**Phone accessories**. Your phone (IServiceCollection) comes as-is, but you can "extend" it with a case, a ring holder, or a screen protector. Each accessory snaps on without modifying the phone itself.

### How We Use It in This Project

| File | Extension Method | What It Registers |
|------|-----------------|-------------------|
| `src/Infrastructure/ERP.Persistence/Extensions/PersistenceServiceExtensions.cs` | `AddPersistence()` | Repositories, UoW, Interceptors |
| `src/Infrastructure/ERP.Infrastructure/Extensions/InfrastructureServiceExtensions.cs` | `AddInfrastructure()` | Tenancy, Caching, CurrentUser |
| `src/Infrastructure/ERP.Identity/Extensions/IdentityServiceExtensions.cs` | `AddIdentityServices()` | JWT auth, Token service |

**In `Program.cs`, they are called cleanly:**
```csharp
// src/Host/ERP.Api/Program.cs
builder.Services.AddPersistence();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddIdentityServices(builder.Configuration);
```

### Code Example

```csharp
// src/Infrastructure/ERP.Persistence/Extensions/PersistenceServiceExtensions.cs
public static class PersistenceServiceExtensions
{
    public static IServiceCollection AddPersistence(this IServiceCollection services)
    {
        services.AddScoped<AuditableEntityInterceptor>();
        services.AddScoped<DomainEventDispatcherInterceptor>();
        services.AddScoped(typeof(IRepository<>), typeof(Repository<>));
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        return services;
    }
}
```

### Interview Tip
> "We use extension methods to modularize DI registration. Each project exposes an `AddXxx()` method that registers its own services. `Program.cs` calls them all, keeping the composition root clean and each project self-contained. If we add a new module (e.g., `ERP.Reporting`), we just add `builder.Services.AddReporting()` -- no existing code changes."

---

## 18. Options Pattern

### What It Is
The Options pattern binds a section of `appsettings.json` to a strongly-typed C# class. Instead of reading configuration values as loose strings, you inject a typed object.

### Real-World Analogy
A **form with labeled fields** vs a **bag of sticky notes**. With the Options pattern, your settings have structure: `JwtSettings.SecretKey`, `JwtSettings.Issuer`. Without it, you are pulling random strings from `Configuration["JwtSettings:SecretKey"]` -- no IntelliSense, no compile-time checking.

### How We Use It in This Project

| File | Role |
|------|------|
| `src/Infrastructure/ERP.Identity/Configuration/JwtSettings.cs` | The strongly-typed options class |
| `src/Infrastructure/ERP.Identity/Extensions/IdentityServiceExtensions.cs` | Binds config and registers with `services.Configure<JwtSettings>(...)` |

### Code Example

**The options class:**
```csharp
// src/Infrastructure/ERP.Identity/Configuration/JwtSettings.cs
public class JwtSettings
{
    public const string SectionName = "JwtSettings";

    public string SecretKey { get; set; } = string.Empty;
    public string Issuer { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;
    public int AccessTokenExpirationMinutes { get; set; } = 60;
    public int RefreshTokenExpirationDays { get; set; } = 7;
}
```

**Binding in DI:**
```csharp
// src/Infrastructure/ERP.Identity/Extensions/IdentityServiceExtensions.cs
services.Configure<JwtSettings>(configuration.GetSection(JwtSettings.SectionName));
```

**Using it in a service:**
```csharp
public class TokenService : ITokenService
{
    private readonly JwtSettings _settings;

    public TokenService(IOptions<JwtSettings> options)
    {
        _settings = options.Value;  // Strongly typed!
    }

    public string GenerateToken(AppUser user)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_settings.SecretKey));
        // ... use _settings.Issuer, _settings.Audience, etc.
    }
}
```

**Corresponding `appsettings.json`:**
```json
{
  "JwtSettings": {
    "SecretKey": "your-very-long-secret-key-at-least-32-chars",
    "Issuer": "ERP.Api",
    "Audience": "ERP.Angular",
    "AccessTokenExpirationMinutes": 60,
    "RefreshTokenExpirationDays": 7
  }
}
```

### Interview Tip
> "We use the Options pattern to bind configuration sections to strongly-typed classes. `JwtSettings` maps to the `JwtSettings` section in `appsettings.json`. Services inject `IOptions<JwtSettings>` to get type-safe access with IntelliSense and compile-time checking. For hot-reload support, use `IOptionsMonitor<T>` instead."

---

## 19. Interceptor Pattern

### What It Is
Interceptors hook into a framework's processing pipeline to add behavior before or after an operation. In EF Core, `SaveChangesInterceptor` lets you run code before/after `SaveChanges` -- without modifying the code that calls `SaveChanges`.

### Real-World Analogy
A **security camera system** at a building entrance. People walk in and out normally (SaveChanges). The camera (interceptor) silently records who enters and when, without anyone changing how they enter the building.

### How We Use It in This Project

| File | When It Runs | What It Does |
|------|-------------|-------------|
| `src/Infrastructure/ERP.Persistence/Interceptors/AuditableEntityInterceptor.cs` | BEFORE save (`SavingChangesAsync`) | Sets `CreatedAt/By`, `ModifiedAt/By`, converts deletes to soft deletes |
| `src/Infrastructure/ERP.Persistence/Interceptors/DomainEventDispatcherInterceptor.cs` | AFTER save (`SavedChangesAsync`) | Collects domain events from entities and publishes them via MediatR |

```
  Handler calls SaveChangesAsync()
       |
       v
  [AuditableEntityInterceptor]  -- BEFORE SAVE
  Sets audit fields, converts deletes to soft deletes
       |
       v
  [Database write happens]
       |
       v
  [DomainEventDispatcherInterceptor]  -- AFTER SAVE
  Publishes domain events via MediatR
```

### Code Example

**AuditableEntityInterceptor (before save):**
```csharp
// src/Infrastructure/ERP.Persistence/Interceptors/AuditableEntityInterceptor.cs
public sealed class AuditableEntityInterceptor : SaveChangesInterceptor
{
    private readonly ICurrentUserService _currentUser;

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct)
    {
        foreach (var entry in eventData.Context.ChangeTracker.Entries<AuditableEntity>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.Entity.CreatedAt = DateTime.UtcNow;
                    entry.Entity.CreatedBy = _currentUser.UserId ?? "system";
                    break;
                case EntityState.Modified:
                    entry.Entity.LastModifiedAt = DateTime.UtcNow;
                    entry.Entity.LastModifiedBy = _currentUser.UserId ?? "system";
                    break;
                case EntityState.Deleted:
                    entry.State = EntityState.Modified;  // Convert to soft delete
                    entry.Entity.IsDeleted = true;
                    entry.Entity.DeletedAt = DateTime.UtcNow;
                    entry.Entity.DeletedBy = _currentUser.UserId ?? "system";
                    break;
            }
        }
        return base.SavingChangesAsync(eventData, result, ct);
    }
}
```

**DomainEventDispatcherInterceptor (after save):**
```csharp
// src/Infrastructure/ERP.Persistence/Interceptors/DomainEventDispatcherInterceptor.cs
public sealed class DomainEventDispatcherInterceptor : SaveChangesInterceptor
{
    private readonly IPublisher _publisher;

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken ct)
    {
        var domainEvents = eventData.Context.ChangeTracker
            .Entries<BaseEntity>()
            .SelectMany(entry =>
            {
                var events = entry.Entity.DomainEvents.ToList();
                entry.Entity.ClearDomainEvents();
                return events;
            }).ToList();

        foreach (var domainEvent in domainEvents)
            await _publisher.Publish(domainEvent, ct);

        return result;
    }
}
```

### Interview Tip
> "We use EF Core SaveChanges interceptors for cross-cutting concerns. `AuditableEntityInterceptor` runs before save to fill audit fields and convert hard deletes to soft deletes. `DomainEventDispatcherInterceptor` runs after save to publish domain events. The beauty is that handlers never need to manually set `CreatedBy` or dispatch events -- it happens automatically."

---

## 20. Composition Root

### What It Is
The Composition Root is the single place in the application where all dependencies are wired together. It is the only place that knows about every project and every interface-to-implementation mapping.

### Real-World Analogy
A **general contractor** building a house. The electrician, plumber, and carpenter each work independently. The general contractor (Composition Root) hires them all, tells the plumber which pipes to use, tells the electrician which outlets to install, and assembles everything into a working house.

### How We Use It in This Project

| File | Role |
|------|------|
| `src/Host/ERP.Api/Program.cs` | The one and only Composition Root |

**`Program.cs` assembles the entire application:**
```csharp
// src/Host/ERP.Api/Program.cs
var builder = WebApplication.CreateBuilder(args);

// 1. Logging
builder.Host.UseSerilog();

// 2. CQRS + Pipeline Behaviors
builder.Services.AddMediatR(cfg =>
{
    cfg.RegisterServicesFromAssembly(typeof(ValidationBehavior<,>).Assembly);
    cfg.AddBehavior(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
    cfg.AddBehavior(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));
});

// 3. Each project registers its own services
builder.Services.AddPersistence();                          // Repos, UoW, Interceptors
builder.Services.AddInfrastructure(builder.Configuration);  // Tenancy, Caching
builder.Services.AddIdentityServices(builder.Configuration); // JWT, Auth

// 4. Build and configure middleware pipeline
var app = builder.Build();
app.UseMiddleware<GlobalExceptionMiddleware>();  // First: catches all exceptions
app.UseCors("AllowAngular");
app.UseAuthentication();                         // Validates JWT
app.UseAuthorization();                          // Checks [Authorize]
app.UseMiddleware<TenantMiddleware>();           // Resolves tenant
app.MapControllers();
app.MapHub<ErpNotificationHub>("/hubs/notifications");
app.Run();
```

### Interview Tip
> "Our `Program.cs` is the Composition Root -- the one place where the entire application is assembled. It references all projects, registers all services in the DI container, and configures the middleware pipeline. No other project references more than two or three other projects. This keeps the dependency graph clean and enforces Clean Architecture boundaries."

---

## Summary: How Patterns Connect

```
+---------------------------------------------------------------------+
|                        PROGRAM.CS (Composition Root #20)             |
|  Registers all services, configures pipeline                         |
|  Uses: Extension Methods (#17), Options Pattern (#18)               |
|  DI Lifetimes: Singleton/Scoped/Transient (#16)                    |
+---------------------------------------------------------------------+
         |
         | HTTP Request arrives
         v
+---------------------------------------------------------------------+
|                     MIDDLEWARE PIPELINE (#7)                          |
|  GlobalExceptionMiddleware --> TenantMiddleware                      |
|  Chain of Responsibility: each can short-circuit                    |
+---------------------------------------------------------------------+
         |
         v
+---------------------------------------------------------------------+
|                     CONTROLLER                                       |
|  Sends ICommand/IQuery via _mediator.Send() (Mediator #4)          |
+---------------------------------------------------------------------+
         |
         v
+---------------------------------------------------------------------+
|                   MEDIATR PIPELINE (#8)                              |
|  ValidationBehavior --> LoggingBehavior --> Handler                  |
+---------------------------------------------------------------------+
         |
         v
+---------------------------------------------------------------------+
|                   COMMAND/QUERY HANDLER (#3 CQRS)                   |
|  Uses: IRepository (#1), IUnitOfWork (#2), ICacheService (#6)       |
|  Uses: Guard Clauses (#13) in domain entities                       |
|  Returns: Result<T> (#9)                                            |
|  Domain entities use: BaseEntity/AuditableEntity (#15)              |
|  Domain entities contain: ValueObjects (#10)                        |
|  Domain entities raise: DomainEvents (#11)                          |
+---------------------------------------------------------------------+
         |
         v
+---------------------------------------------------------------------+
|                    PERSISTENCE LAYER                                  |
|  Repository<T> --> AppDbContext (created by Factory #5)              |
|  Interceptors (#19): Audit + DomainEventDispatcher                  |
|  Soft Delete (#14): Global query filter                             |
|  All wired via DIP (#12): interfaces in App, impls in Infra        |
+---------------------------------------------------------------------+
         |
         v
+---------------------------------------------------------------------+
|                      DATABASE                                        |
|  SQL Server or PostgreSQL (per tenant, via Factory #5)              |
+---------------------------------------------------------------------+
```

---

## Interview Cheat Sheet

If asked "What design patterns do you use?", here is a quick answer:

> "Our ERP API uses Clean Architecture with .NET 8. We use the **Repository** and **Unit of Work** patterns to abstract data access. **CQRS** with **MediatR** separates commands and queries, with **Pipeline Behaviors** handling validation and logging as cross-cutting concerns. The **Result pattern** replaces exceptions for business failures. Our entities inherit from a **Layer Supertype** (`AuditableEntity`) that provides audit fields and **Soft Delete**. **Domain Events** decouple side effects. EF Core **Interceptors** handle audit stamps and event dispatching automatically. The **Factory pattern** enables multi-tenant database connections, and the **Strategy pattern** lets us swap cache providers. Everything is wired together in `Program.cs` (the **Composition Root**) using the **Options pattern** for configuration and **Extension Methods** for modular DI registration, following the **Dependency Inversion Principle** throughout."
