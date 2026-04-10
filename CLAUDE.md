# ERP API — Project Memory

> This file is automatically read by Claude Code. It preserves all architectural decisions,
> conventions, and project context so work can continue seamlessly on any system.

## Project Overview

Enterprise Resource Planning (ERP) API built with ASP.NET Core (.NET 10 STS).
Supports multi-tenant, multi-database-provider architecture for manufacturing companies.

**Stack:** .NET 10 | EF Core 10 | MediatR | FluentValidation | JWT | SignalR | Serilog
**Frontend:** Angular (separate repository, integrated via REST API + SignalR)
**Databases:** SQL Server, PostgreSQL (per-tenant choice), Oracle planned
**Developer:** Solo developer, freshers joining later

## Solution Structure

```
ERP.slnx                              ← .NET 10 SDK format (builds net8.0 targets)
├── src/Core/
│   ├── ERP.SharedKernel/              ← Base entities, ValueObject, Result<T>, Guards, DomainEvents
│   └── ERP.Application.Abstractions/ ← ICommand, IQuery, IRepository, IUnitOfWork, Behaviors
├── src/Infrastructure/
│   ├── ERP.Persistence/              ← AppDbContext, Repository<T>, UnitOfWork, Interceptors
│   ├── ERP.Persistence.SqlServer/    ← SQL Server provider + migrations assembly
│   ├── ERP.Persistence.PostgreSql/   ← PostgreSQL provider + migrations assembly
│   ├── ERP.Infrastructure/           ← TenantMiddleware, TenantDbContextFactory, CacheService
│   ├── ERP.Identity/                 ← JWT TokenService, AppUser, AppRole, Permission entities
│   └── ERP.Notifications/           ← SignalR ErpNotificationHub
├── src/Modules/
│   ├── Workflow/{Domain,Application,Infrastructure,Api}/  ← Process template engine
│   └── Production/{Domain,Application,Infrastructure,Api}/ ← Work orders, job cards
├── src/Host/
│   └── ERP.Api/                      ← Composition root (Program.cs), Middleware, appsettings
├── tests/
│   ├── ERP.UnitTests/
│   └── ERP.Architecture.Tests/
├── tools/
│   └── ERP.DbMigrator/              ← Console app for applying migrations
└── docs/
    ├── DESIGN_PATTERNS.md            ← 20 patterns explained with interview tips
    ├── ARCHITECTURE.md               ← 20 concepts explained with code examples
    ├── DATABASE_ARCHITECTURE.md      ← Complete table definitions, relationships, data flow
    └── TENANT_PROVISIONING.md        ← How to create & migrate tenants (manual + API)
```

## Architecture Decisions (ADRs)

### 1. Clean Architecture + Modular Monolith
- Dependency flows INWARD: Api → Infrastructure → Application → Domain
- Domain and Application have ZERO references to EF Core or ASP.NET
- Each business module (Workflow, Production, Finance) is self-contained with its own Domain/Application/Infrastructure/Api layers
- Can be extracted to microservices later if needed

### 2. Multi-Tenancy: Custom (NOT Finbuckle)
- **Decision:** Custom tenant middleware + TenantDbContextFactory
- **Reason:** Need multi-provider switching (SQL Server/PostgreSQL per tenant), full control, fresher-friendly
- **Architecture:** Master DB (ERP_Master) + Per-Tenant DBs (ERP_{TenantCode})
  - **Master DB:** Tenants, Users, UserTenants, UserSessions, Roles, Permissions, UserRoles, RolePermissions, Modules, Menus, MenuPermissions, FeatureFlags, TenantFeatureFlags, IdentityAuditLogs, UserPasswordHistory
  - **Tenant DB:** Plants, UserPlantAccess, UserPreferences, ProcessTemplates, ProcessSteps, WorkOrders, JobCards, AuditLogs
  - **Two DbContexts:** MasterDbContext (identity/access) + AppDbContext (business data per tenant)
- **How it works:**
  1. `TenantMiddleware` reads `X-Tenant-Id` header, looks up tenant in MasterDbContext (cached), stores in `HttpContext.Items`
  2. `HttpContextTenantProvider` reads tenant from HttpContext
  3. `TenantDbContextFactory` creates AppDbContext with correct provider and connection string
  4. Auth service queries MasterDbContext for user/roles/permissions
  5. Business handlers query AppDbContext (tenant-specific database)
- **Tenant provisioning:** Admin API creates DB, runs migrations, seeds admin user automatically
- **Cross-DB references:** UserPlantAccess.UserId → Master.Users validated in app code (no FK constraint — SQL Server doesn't support cross-DB FKs)

### 3. Multi-Database Provider
- **Shared:** `ERP.Persistence` has AppDbContext, entity configurations, Repository<T>, UnitOfWork
- **Per-Provider:** `ERP.Persistence.SqlServer` and `ERP.Persistence.PostgreSql` have provider-specific config + migration assemblies
- **Switching:** `TenantDbContextFactory.CreateDbContext()` switches provider based on `TenantInfo.DbProvider`
- **Adding Oracle:** Create `ERP.Persistence.Oracle` project, add case in TenantDbContextFactory

### 4. No Elsa Workflow Engine
- **Decision:** Table-based process definitions instead of Elsa
- **Reason:** Simpler, no external dependency, clients modify flow via CRUD on ProcessTemplate/ProcessStep tables
- **Tables:** ProcessTemplate (name, version), ProcessStep (order, department, stepType, nextStepId, qaRequired)
- **Step types:** Single (sequential), Parallel (split to multiple departments), Merge (join parallel paths)

### 5. JobCard Numbering: YYMMDDN###S###R##
- Format: `260322N001S000R00` — Date + Running Number + Split + Rework
- No barcode library (ZXing) — use Code 128 barcode font in reports
- `JobCardNumber` is a Value Object with Create(), CreateSplit(), CreateRework() methods
- Links to WorkOrder via the running number portion

### 6. DB Abstraction: Clear Separation
- `ERP.SharedKernel` → entities (ZERO EF Core reference)
- `ERP.Application.Abstractions` → interfaces (IRepository, IUnitOfWork)
- `ERP.Persistence` → implementations (Repository<T>, UnitOfWork, AppDbContext)
- Business logic NEVER touches DbContext directly

### 7. Authentication: JWT (Custom, NOT ASP.NET Core Identity)
- Custom AppUser/AppRole/Permission entities in ERP.Identity
- BCrypt password hashing
- Access token (60 min) + Refresh token (7 days) via UserSessions table (multi-device)
- Account lockout: 5 failed attempts → 15 min lock (IsLocked, LockoutEnd, AccessFailedCount)
- SecurityStamp: random GUID, changes on password/security change, invalidates all sessions
- Password history: last 5 hashes checked on change (UserPasswordHistory table)
- Permissions: hybrid RBAC + ABAC-ready (RolePermissions.ConditionJson for future)
- Permission naming: `CATEGORY_ACTION` (e.g., `JOBCARD_CREATE`, `QA_APPROVE`)
- Roles are per-tenant: same user can be Admin in Tenant A, Viewer in Tenant B
- System roles (TenantId=NULL): Admin, Production Operator, QA Inspector, Viewer
- Dynamic menus: API-driven sidebar, filtered by user permissions + tenant feature flags

## Key Conventions

### Coding
- All entities inherit `AuditableEntity` (CreatedAt/By, ModifiedAt/By, IsActive, IsDeleted/DeletedAt/DeletedBy)
- Use `Result<T>` for business operations (not exceptions)
- Use FluentValidation for command validation (auto-run via ValidationBehavior)
- Use `Guard.Against*` for constructor validation
- Entity configurations via Fluent API in separate `IEntityTypeConfiguration<T>` classes (not annotations)
- GUIDs for all entity IDs (not auto-increment ints)

### CQRS Pattern
- Commands: `ICommand` / `ICommand<TResponse>` → changes data, returns `Result`
- Queries: `IQuery<TResponse>` → reads data, returns `Result<TResponse>`
- Handlers: one handler per command/query, registered via MediatR
- Pipeline: Request → ValidationBehavior → LoggingBehavior → Handler

### DI Lifetimes
- **Singleton:** ICacheService, ITokenService
- **Scoped:** MasterDbContext, AppDbContext, IRepository<T>, IUnitOfWork, ITenantProvider, ITenantStore, ICurrentUserService, ITenantManagementService, ITenantProvisioningService
- **Transient:** (not used currently)

### Project References (Dependency Rule)
- `Domain` → references only `SharedKernel`
- `Application` → references `Domain` + `Application.Abstractions`
- `Infrastructure` → references `Application` + `Persistence`
- `Api` → references `Application` (controllers send commands/queries)
- `ERP.Api` (host) → references EVERYTHING (composition root)

### Middleware Order (Program.cs)
1. GlobalExceptionMiddleware (catches all errors)
2. Swagger (dev only)
3. HTTPS Redirection
4. CORS (AllowAngular policy)
5. Authentication (JWT validation)
6. Authorization ([Authorize] checks)
7. TenantMiddleware (resolves X-Tenant-Id header)
   - **Intentionally AFTER auth**: JWT uses a global signing key (not per-tenant), so auth doesn't need tenant context. Auth-first rejects unauthenticated requests before tenant lookups.
8. Serilog request logging
9. Controllers + SignalR hubs + Health checks

## Build & Run

```bash
# Build
dotnet build ERP.slnx

# Run API
dotnet run --project src/Host/ERP.Api

# Run tests
dotnet test ERP.slnx
```

## Configuration (appsettings.json)

- `JwtSettings` → Issuer, audience, token expiration (SecretKey is a **placeholder** — override in env vars or user-secrets)
- `Tenants[]` → Tenant configs with **placeholder** connection strings (override in appsettings.Development.json or env vars)
- `AllowedOrigins` → CORS origins for Angular
- `Serilog` → Log levels, sinks
- **Secrets policy:** `appsettings.json` contains only placeholders; real values go in `appsettings.Development.json`, environment variables, or a secret store. Startup fails fast if JWT key is missing/weak (< 32 chars).

## Phase Status

- [x] Phase 0: Solution structure, SharedKernel, Persistence, Infrastructure, Identity, Notifications, API Host
- [x] Phase 1: Identity APIs (login, register, role/permission management) + Workflow engine (ProcessTemplate, ProcessStep CRUD)
- [x] Phase 2: Production module — WorkOrder & JobCard entities, CRUD commands/queries, controllers
- [x] Phase 2.5A: Master DB architecture (MasterDbContext, redesigned identity entities, DesignTimeDbContextFactories)
- [x] Phase 2.5B: Tenant provisioning API (TenantsController, TenantManagementService, TenantProvisioningService, MasterDbTenantStore)
- [x] Phase 2.5C: Refactor auth flow for Master DB (sessions, lockout, password history, SecurityStamp, logout)
- [x] Phase 2.5D: Dynamic menu API (permission-filtered sidebar)
- [x] Phase 2.5E: Tenant DB additions (Plants, UserPlantAccess, UserPreferences, AuditLogs)
- [x] Phase 2.5F: API smoke testing (all auth/authz/multi-tenant scenarios)
- [ ] Phase 2.5G: Angular integration (auth service, interceptor, sidebar, permission directive)
- [ ] Phase 3: SignalR integration (job card status, QA alerts)
- [ ] Phase 4: Reporting (open-source, Angular-compatible)
- [ ] Phase 5: Additional modules (Finance, Sales, HR — process templates)

## Important Notes

- .NET 10 SDK is installed; ALL projects now target `net10.0` (via Directory.Build.props)
- Solution file is `.slnx` format (new in .NET 10 SDK)
- `X-Tenant-Id` header is REQUIRED for all API requests (except /health, /swagger, /auth/login, /hubs/)
- Entity configurations in ERP.Identity are discovered by AppDbContext via assembly scanning
- Module entity configs (Workflow, Production) registered via `AppDbContext.ConfigurationAssemblies` in their ServiceExtensions
- Domain events are dispatched AFTER SaveChanges succeeds (via DomainEventDispatcherInterceptor)
- Soft delete is automatic — EF Core global query filter excludes IsDeleted=true records
- `BaseEntity.Id` uses `init` accessor — settable in object initializers, immutable after construction
- Identity seeder runs on startup — creates default admin (admin/Admin@123), 4 roles, 28 permissions
- Workflow engine is table-based (no Elsa) — clients modify flow via CRUD on ProcessTemplate/ProcessStep

## Design Patterns (20 Used)

Repository, Unit of Work, CQRS, Mediator, Factory, Strategy, Middleware/Chain of Responsibility,
Pipeline, Result Pattern, Value Object, Domain Events, Dependency Inversion, Guard Clause,
Soft Delete, Layer Supertype, DI Lifetimes (Singleton/Scoped/Transient), Extension Method,
Options Pattern, Interceptor, Composition Root

See `docs/DESIGN_PATTERNS.md` for detailed explanations with interview tips.
See `docs/ARCHITECTURE.md` for concept deep-dives with code examples.
