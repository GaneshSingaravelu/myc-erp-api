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
ERP.slnx                              ← .NET 10 SDK format, all projects target net10.0
├── src/Core/
│   ├── ERP.SharedKernel/                          ← Base entities (incl. AuditLog), ValueObject, Result<T>, Guards, DomainEvents
│   └── ERP.Application.Abstractions/              ← ICommand, IQuery, IRepository, IUnitOfWork, Behaviors
│       ├── Identity/                              ← IAuthService, IUserService, IRoleService, IMenuService,
│       │   │                                        ITenantManagementService, ICurrentUserService
│       │   └── Contracts/                         ← AuthDtos, MenuDtos, TenantDtos (request/response records)
│       └── MultiTenancy/                          ← ITenantProvider, ITenantStore, ITenantProvisioningService, TenantInfo
├── src/Infrastructure/
│   ├── ERP.Persistence/                           ← AppDbContext, Repository<T>, UnitOfWork, Interceptors
│   ├── ERP.Persistence.SqlServer/                 ← SQL Server provider + migrations assembly
│   ├── ERP.Persistence.PostgreSql/                ← PostgreSQL provider + migrations assembly
│   ├── ERP.Infrastructure/                        ← TenantMiddleware, TenantDbContextFactory, CacheService
│   ├── ERP.Identity/                              ← Services + repositories + entities (JWT, AppUser, AppRole, Permission)
│   │   ├── Abstractions/Persistence/              ← IUserRepository, IRoleRepository, IPermissionRepository,
│   │   │                                            IMenuRepository, IFeatureFlagRepository, ITenantRepository,
│   │   │                                            IUserSessionRepository, IUserPasswordHistoryRepository,
│   │   │                                            IMasterUnitOfWork
│   │   ├── Persistence/                           ← Concrete repositories + MasterUnitOfWork (only place that touches MasterDbContext)
│   │   └── Services/                              ← AuthService, UserService, RoleService, MenuService,
│   │                                                TenantManagementService, TokenService (zero EF Core refs — guarded)
│   └── ERP.Notifications/                         ← SignalR ErpNotificationHub
├── src/Modules/
│   ├── Workflow/{Domain,Application,Infrastructure,Api}/  ← Process template engine
│   └── Production/{Domain,Application,Infrastructure,Api}/ ← Work orders, job cards
├── src/Host/
│   └── ERP.Api/                                   ← Composition root (Program.cs), Controllers/v1, Middleware, appsettings
├── tests/
│   ├── ERP.UnitTests/
│   │   └── Identity/                              ← AuthServiceTestBase + IdentityServiceTestBase + 99 tests
│   └── ERP.Architecture.Tests/                    ← 13 NetArchTest guards (incl. IdentityServices_Should_Not_Depend_On_EfCore)
├── tools/
│   └── ERP.DbMigrator/                            ← Console app for applying migrations
└── docs/
    ├── DESIGN_PATTERNS.md                         ← 20 patterns explained with interview tips
    ├── ARCHITECTURE.md                            ← 20 concepts explained with code examples
    ├── DATABASE_ARCHITECTURE.md                   ← Complete table definitions, relationships, data flow
    └── TENANT_PROVISIONING.md                     ← How to create & migrate tenants (manual + API)
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
- `ERP.Application.Abstractions` → interfaces (IRepository, IUnitOfWork) + Identity service contracts + DTOs
- `ERP.Persistence` → generic implementations (Repository<T>, UnitOfWork, AppDbContext) for tenant business data
- `ERP.Identity.Abstractions.Persistence` → specialized identity repository interfaces (IUserRepository, IRoleRepository, IMenuRepository, IFeatureFlagRepository, ITenantRepository, IUserSessionRepository, IUserPasswordHistoryRepository, IPermissionRepository, IMasterUnitOfWork)
- `ERP.Identity.Persistence` → concrete implementations — the ONLY place in `ERP.Identity` that touches `MasterDbContext`
- Identity services (AuthService/UserService/RoleService/MenuService/TenantManagementService) depend only on repositories + IMasterUnitOfWork — zero EF Core references, enforced by `IdentityServices_Should_Not_Depend_On_EfCore` architecture test
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
- **Scoped:** MasterDbContext, AppDbContext, IRepository<T>, IUnitOfWork, IMasterUnitOfWork, all Identity repositories (IUserRepository, IRoleRepository, IPermissionRepository, IMenuRepository, IFeatureFlagRepository, ITenantRepository, IUserSessionRepository, IUserPasswordHistoryRepository), ITenantProvider, ITenantStore, ICurrentUserService, IAuthService, IUserService, IRoleService, IMenuService, ITenantManagementService, ITenantProvisioningService
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
- [~] Phase 2: Production module — WorkOrder & JobCard entities, CRUD commands/queries, controllers — **PARTIAL: see "Phase 2 Audit Gaps" below**
- [x] Phase 2.5A: Master DB architecture (MasterDbContext, redesigned identity entities, DesignTimeDbContextFactories)
- [x] Phase 2.5B: Tenant provisioning API (TenantsController, TenantManagementService, TenantProvisioningService, MasterDbTenantStore)
- [x] Phase 2.5C: Refactor auth flow for Master DB (sessions, lockout, password history, SecurityStamp, logout)
- [~] Phase 2.5D: Dynamic menu API (permission-filtered sidebar) — **PARTIAL: API supports unlimited hierarchy, UI submenu renders n-levels recursively, but (a) sidebar collapsed-icon click does NOT expand (only opens flyout), (b) seed data only goes 1 level deep — needs multi-hierarchy demo seed**
- [~] Phase 2.5E: Tenant DB additions (Plants, UserPlantAccess, UserPreferences, AuditLogs) — **INFRASTRUCTURE-ONLY: entities + EF configs + migrations exist, but ZERO integration (see audit below)**
- [x] Phase 2.5F: API smoke testing (all auth/authz/multi-tenant scenarios)
- [x] Phase 2.5G: Angular integration (NgRx Signal Store, AuthStore, MenuStore, JWT/X-Tenant interceptors, dynamic sidebar) — PR #7 merged
- [x] Phase 2.5W: AuthService unit tests (51 tests) — PR #3 merged
- [x] Phase 2.5X: Identity services repository refactor (User/Role/Menu/TenantManagement) + IdentityServiceTestBase + 47 new unit tests + architecture guard — PR #4 merged
- [x] Phase 2.5Y: JWT hardening (base64url padding fix, clock-skew direction correction, MapInboundClaims=false, JTI binding) — merged with Phase 2.5X / Pre-Phase 3
- [x] Phase 2.5Z: Cross-tenant role/permission isolation (IsAssignedToTenantAsync gate, tenant-filtered role/permission extraction at login & refresh) — PR `fix/tenant-scoped-role-permission` (commit e417526)
- [x] Pre-Phase 3: Angular fixes (login UI rebuild, full-page auth layout, stale-token guard, tenant code wiring) — PR #7 merged
- [x] CI/CD: GitHub Actions workflows for both repos (build + test + cache) + .gitattributes LF normalization
- [~] **Phase 2 Completion:** API items 1–4 done (AuditLogInterceptor, PlantAuthorizationBehavior, UserPreferences, Plants CRUD). Angular items remain: skeleton loader, @defer, multi-hierarchy menu seed, sidebar collapse-expand, component folder audit, Graphify, BEM/component-folder standards
- [ ] Phase 3 Preparation: Sample seed users (Operator, Inspector, Viewer with multi-tenant assignments) + Tenant dropdown UX
- [ ] Phase 3: SignalR integration (job card status broadcasts, QA alerts, tenant-scoped hub groups)
- [ ] Phase 4: Reporting (open-source, Angular-compatible)
- [ ] Phase 5: Additional modules (Finance, Sales, HR — process templates)

---

## Phase 2 Audit Gaps (DISCOVERED — must complete before Phase 3)

This section captures gaps surfaced by a deep audit of Phase 2 / 2.5D / 2.5E. The phases were marked complete based on **infrastructure existence**, but business integration is missing. **All items below are required to truly close Phase 2.**

### A. Tenant DB Additions — Not Wired (Phase 2.5E)

| Entity | Status | Gap |
|--------|--------|-----|
| `Plant` | ✅ DONE | `PlantsController` + `PlantCommands.cs` + `PlantQueries.cs` in Production module. Admin-only writes use `[Authorize(Roles = "Admin")]`. |
| `UserPlantAccess` | ✅ DONE | `PlantAuthorizationBehavior` in `ERP.Production.Application/Behaviors/`. Commands implement `IPlantScopedRequest` (in Application.Abstractions.CQRS). `WorkOrder.PlantId` added. Migration `20260502180307_AddPlantIdToWorkOrders`. |
| `UserPreference` | ✅ DONE (API) | `UserPreferencesController` + `SetUserPreferenceCommand` + `GetUserPreferenceQuery`. Upsert on PUT. **Angular PreferenceStore still needed.** |
| `AuditLog` (tenant) | ✅ DONE | `AuditLogInterceptor` in `ERP.Persistence/Interceptors/`. Entity moved to `ERP.SharedKernel.Entities`. Config moved to `ERP.Persistence/Configurations/`. Captures Create/Update/Delete with old/new JSON snapshots, UserId, IP, UserAgent. Wired in `TenantDbContextFactory`. 5 unit tests. |

**Remaining work (Angular):**
- Wire Angular `PreferenceStore` for sidebar-collapsed-state + UI filter persistence.
- See Section C below for skeleton loader, @defer, component folders, Graphify.

### B. Dynamic Menu — Hierarchy Display & Collapsed-Click (Phase 2.5D)

**API:** ✅ Fully supports unlimited depth (`Menu.ParentMenuId`, `MenuService.BuildMenuItem` is recursive, `MenuItemResponse.Children` is recursive list).
**UI fullscreen submenu (`submenu.html`):** ✅ Renders n-level items via `*ngTemplateOutlet` with `depth + 1` recursion.

**Gaps:**
1. **Seed data only goes 1 level deep** — IdentitySeeder needs to seed nested menus (e.g., Production → Work Orders → Create / List / Approvals) to demonstrate the hierarchy
2. **Sidebar collapsed-icon click should EXPAND the sidebar** when user clicks a parent menu icon — currently it opens a flyout instead. UX expectation: clicking a menu icon while sidebar is collapsed should auto-expand the sidebar to that menu (then user can click child)

### C. UI Standards & Reusability (NEW Phase 2 items)

1. **Skeleton Loader Component (build now)** — Create reusable `SkeletonComponent` in `libs/ui/src/lib/skeleton/`:
   - Inputs: `shape: 'rectangle' | 'circle' | 'text'`, `width: string`, `height: string`, `animation: 'pulse' | 'wave' | 'none'`
   - Wraps Kendo `kendo-skeleton` OR rolls its own (Kendo dep already in package-lock)
   - Usage:
     ```html
     <mycodee-skeleton shape="rectangle" width="100%" height="230px" animation="pulse" />
     <mycodee-skeleton shape="text" width="40%" />
     <mycodee-skeleton shape="circle" width="48px" height="48px" />
     ```

2. **`@defer` + Skeleton everywhere data-bound** — Convert data-loading components to use Angular `@defer (when data())` with `@placeholder` rendering a skeleton matching the final UI shape. Don't show the actual UI until the API responds. Pattern:
   ```html
   @defer (when !loading()) {
     <kpi-card [data]="data()" />
   } @placeholder {
     <mycodee-skeleton shape="rectangle" width="100%" height="120px" />
   }
   ```

3. **Component folder structure** — Each component in its own folder: `component-name/{component-name.ts, component-name.html, component-name.scss, component-name.spec.ts, index.ts}`. Audit existing components and refactor any flat files into folders.

4. **CSS naming convention is BEM** (`myc-{block}__{element}--{modifier}`):
   - **Confirmed standard.** `myc-auth__header` is **proper BEM with `myc-` namespace prefix** (Block__Element).
   - Used consistently across `_auth-layout.scss`, `_admin-layout.scss`, `_glassmorphism.scss`, dashboard components, and stat-card.
   - Document this convention explicitly in `myc-erp-demo/CLAUDE.md` so contributors don't invent ad-hoc patterns.

5. **Graphify implementation** (was Phase 2.5Y, promoted into Phase 2):
   - **Goal:** Single shared GraphQL endpoint for both API and UI consumption to reduce REST chatter and over-fetching
   - **API:** Add `HotChocolate.AspNetCore` + `HotChocolate.Data.EntityFramework`; expose schema-first GraphQL at `/graphql`
   - **UI:** Add Apollo Angular (`@apollo/client` + `apollo-angular`); generate typed operations via `graphql-codegen`
   - **Shared schema:** Define `.graphql` schema files in a shared location (e.g. `libs/shared/graphql/schema/` mirrored to API resources) so both sides stay in sync; codegen produces TS types in UI and C# types in API
   - **Auth:** Reuse JWT bearer + X-Tenant-Id header (resolve tenant in GraphQL middleware identical to REST)
   - Start with read queries only (Menus, WorkOrders, JobCards), keep mutations on REST until query story stabilizes

### Pre-Phase 3 Punch List (Angular — `myc-erp-demo`) — ✅ COMPLETED (PR #7 merged)

All four items below shipped:

1. ✅ **Login page rebuilt** — submit button, layout, BEM-style auth scss
2. ⏳ **Tenant field UX** — moved to "Phase 3 Preparation" (dropdown design + public endpoint)
3. ✅ **Stale localStorage token bypasses guard** — `authGuard` now decodes JWT + checks `exp` (with base64url padding fix + 30s clock-skew expire-early)
4. ✅ **Login full-page layout** — login routed outside WebLayoutComponent

### Phase 2.5X — What Landed (uncommitted, fully tested)

**New abstractions** (`src/Core/ERP.Application.Abstractions/Identity/`):
- `Contracts/AuthDtos.cs`, `Contracts/MenuDtos.cs`, `Contracts/TenantDtos.cs` — request/response records moved out of Identity assembly
- `IAuthService.cs`, `IUserService.cs`, `IRoleService.cs`, `IMenuService.cs`, `ITenantManagementService.cs` — service contracts now live with the other Application interfaces

**New abstractions** (`src/Core/ERP.Application.Abstractions/MultiTenancy/`):
- `ITenantProvider.cs`, `ITenantStore.cs`, `ITenantProvisioningService.cs`, `TenantInfo.cs` — multi-tenancy contracts hoisted out of `ERP.Infrastructure`

**New repository layer** (`src/Infrastructure/ERP.Identity/Abstractions/Persistence/` + `Persistence/`):
- `IUserRepository` / `UserRepository` — user lookups w/ Include chains for roles+permissions+tenants
- `IRoleRepository` / `RoleRepository` — role queries + permission-assignment helpers
- `IPermissionRepository` / `PermissionRepository` — permission lookups + ordered-by-category catalog
- `IMenuRepository` / `MenuRepository` — `GetActiveModulesWithVisibleMenusAsync` (Module → Menus filtered by IsVisible → MenuPermissions → Permission)
- `IFeatureFlagRepository` / `FeatureFlagRepository` — `GetGlobalFlagsAsync`, `GetTenantOverridesAsync` returning `IReadOnlyDictionary<string,bool>`
- `ITenantRepository` / `TenantRepository` — extended with `ExistsByCodeAsync`, `GetByIdWithUserTenantsAsync`, `GetAllAsync`, `AddAsync`
- `IUserSessionRepository`, `IUserPasswordHistoryRepository` (already existed for AuthService)
- `IMasterUnitOfWork` / `MasterUnitOfWork` — wraps `MasterDbContext.SaveChangesAsync()`

**Refactored services** (`src/Infrastructure/ERP.Identity/Services/`):
- `UserService`, `RoleService`, `MenuService`, `TenantManagementService` rewritten to depend only on repositories + `IMasterUnitOfWork` — **zero EF Core references**

**Architecture guard** (`tests/ERP.Architecture.Tests/ArchitectureTests.cs`):
- New `IdentityServices_Should_Not_Depend_On_EfCore` test pins `ERP.Identity.Services` namespace to forbid `Microsoft.EntityFrameworkCore` — prevents regression

**Test foundation** (`tests/ERP.UnitTests/Identity/`):
- `IdentityServiceTestBase.cs` — shared base with InMemory `MasterDbContext` + all 6 identity repositories + `MasterUnitOfWork` + `AuditableEntityInterceptor` (mocked `ICurrentUserService`); seed helpers `CreateTestUser/Role/Permission/Tenant` + `PersistAsync<T>`
- `UserServiceTests.cs` (11 tests), `RoleServiceTests.cs` (12), `MenuServiceTests.cs` (8), `TenantManagementServiceTests.cs` (10)

**Test suite status (Phase 2.5X baseline):** 13/13 architecture + 99/99 unit tests passing (was 12/12 + 52/52 before this work). After Phase 2 API items: 111/111 unit + 13/13 architecture (5 new `AuditLogInterceptorTests`).

**Error code convention:** `Error.NotFound("Entity", id)` produces code `"{Entity}.NotFound"` — e.g. `"User.NotFound"`, `"Role.NotFound"`, `"Permission.NotFound"`, `"Tenant.NotFound"`. Tests assert against these full codes.

### Suggested Next Action (when you resume)

**Phase 2 completion — API items done, Angular items remain:**

1. ✅ **AuditLogInterceptor** — done. `AuditLogInterceptor.cs` in `ERP.Persistence/Interceptors/`. `AuditLog` moved to `ERP.SharedKernel.Entities`. Config in `ERP.Persistence/Configurations/`. Wired in `TenantDbContextFactory`.
2. ✅ **UserPlantAccess enforcement** — done. `PlantAuthorizationBehavior` + `IPlantScopedRequest`. `WorkOrder.PlantId` added + migration.
3. ✅ **UserPreference store (API)** — done. `UserPreferencesController` + upsert command + queries.
4. ✅ **Plants CRUD** — done. `PlantsController` + `PlantCommands.cs` + `PlantQueries.cs`.
5. **Skeleton loader component** in `libs/ui/src/lib/skeleton/` — rectangle/circle/text shapes, pulse/wave animation, width/height inputs.
6. **`@defer` rollout** — convert all data-loading components to `@defer (when data())` with skeleton placeholders matching final UI shape.
7. **Multi-hierarchy menu seed** — IdentitySeeder seeds 3-level menu (Module → Parent → Child) so the recursive renderer is exercised.
8. **Sidebar collapsed-icon expand** — clicking a parent menu icon in collapsed mode should auto-expand the sidebar (not open flyout).
9. **Component folder structure audit** — every component in its own folder.
10. **Angular PreferenceStore** — NgRx Signal Store wired to `PUT /api/v1/preferences/{key}` for sidebar-collapsed-state + table filters.
11. **Graphify (HotChocolate + Apollo Angular)** — start with read queries (Menus, WorkOrders, JobCards) sharing schema between API and UI.

**After Phase 2 closes:**
- Phase 3 Prep: sample seed users (Operator/Inspector/Viewer multi-tenant) + tenant dropdown UX + public list-tenants endpoint
- Phase 3: SignalR (job card status broadcasts, QA alerts, tenant-scoped hub groups)

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
- `AuditLog` entity lives in `ERP.SharedKernel.Entities` (not Production.Domain). Its EF config is in `ERP.Persistence/Configurations/AuditLogConfiguration.cs`. `AuditLogInterceptor` populates it — wired in `TenantDbContextFactory` only (not MasterDbContext, since AuditLogs are tenant-DB only).
- Pipeline behavior execution order: `ValidationBehavior` → `PlantAuthorizationBehavior` → `LoggingBehavior` → Handler. Plant auth only fires for commands implementing `IPlantScopedRequest` (in `ERP.Application.Abstractions.CQRS`).
- `ICurrentUserService` now exposes `IpAddress` and `UserAgent` (added for `AuditLogInterceptor`).
- **AppDbContext migrations must use `tools/ERP.DbMigrator` as the startup project** — `ERP.Api` fails at design time because `HttpContext` is unavailable. Command: `dotnet ef migrations add <Name> --context AppDbContext --project src/Infrastructure/ERP.Persistence.SqlServer --startup-project tools/ERP.DbMigrator --output-dir Migrations/AppDb`

## CI/CD Status

- **API (.NET 10):** GitHub Actions workflow — `dotnet build ERP.slnx` → `dotnet test ERP.slnx`. NuGet cache enabled. Permissions `contents: read`. Currently green: 111/111 unit tests + 13/13 architecture guards.
- **UI (Angular):** GitHub Actions workflow — `npm ci` → `npx nx run-many --target lint --all` → `npx nx run-many --target test --all`. npm cache enabled.
- **`.gitattributes`** enforces LF line endings repo-wide (eliminates CRLF noise on Windows contributors).
- **Recommended next:** Branch protection on `main` requiring CI green + 1 review.

## Design Patterns (20 Used)

Repository, Unit of Work, CQRS, Mediator, Factory, Strategy, Middleware/Chain of Responsibility,
Pipeline, Result Pattern, Value Object, Domain Events, Dependency Inversion, Guard Clause,
Soft Delete, Layer Supertype, DI Lifetimes (Singleton/Scoped/Transient), Extension Method,
Options Pattern, Interceptor, Composition Root

See `docs/DESIGN_PATTERNS.md` for detailed explanations with interview tips.
See `docs/ARCHITECTURE.md` for concept deep-dives with code examples.
