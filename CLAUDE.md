# ERP API — Project Memory

> This file is automatically read by Claude Code. It preserves all architectural decisions,
> conventions, and project context so work can continue seamlessly on any system.

## Project Overview

Enterprise Resource Planning (ERP) API built with ASP.NET Core (.NET 10 STS).
Supports multi-tenant, multi-database-provider architecture for manufacturing companies.

**Stack:** .NET 10 | EF Core 10 | MediatR | FluentValidation | JWT | SignalR | Serilog
**Frontend:** Angular (separate repository, integrated via REST API + SignalR)
**Databases:** SQL Server **and** PostgreSQL are first-class, chosen **per customer deployment**
(Master/Identity DB *and* Tenant DBs use the same provider). Mixed providers within one
deployment are not a primary requirement. Oracle is **not** in the approved scope.
> **Provider selection and creation work; full parity is partially open.** Status
> 2026-10-10. `Database:Provider` (`SqlServer` or `PostgreSql`, env override
> `Database__Provider`) is resolved by `DatabaseProviderResolver` for **both** Master runtime
> and all three design-time factories; a missing or unrecognised value **throws at startup**
> and is never silently treated as SQL Server (`DB-01`, `DB-04` — closed, PRs #16–#19).
> `Tenant.Code` is canonicalized by the application as `Trim()` + `ToLowerInvariant()`, so
> tenant identity does not depend on the engine's collation (`DB-10` — closed, PR #20).
> PostgreSQL has its own migration baselines for Master and tenant DBs (`DB-02` — closed,
> PRs #24–#25). Provider parity is verified by `ProviderParityTests` and `tools/ERP.ParityCheck`
> (PR #26) but `DB-03` stays **PARTIALLY OPEN**: incremental-upgrade parity (`L-10`) is deferred.
> ⚠️ **Still open:** `DB-09` `Plant.Code` provider divergence (documented, unresolved);
> `tools/ERP.DbMigrator` remains a stub (`DB-08`); and a **read-only `Tenant.Code`
> canonicality check must be run before deploying to any unverified Master database**
> (`DB-11` — runbook in `docs/TENANT_PROVISIONING.md`).
> All tracked in `docs/SATY_ERP_MASTER_EXECUTION_PLAN.md` §6.2 (frontend repo), which is
> authoritative for status.
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
│   ├── ERP.Persistence.PostgreSql/                ← PostgreSQL provider + migrations (MasterDb + AppDb baselines)
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
│   ├── ERP.DbMigrator/                            ← Console app for applying migrations
│   └── ERP.ParityCheck/                           ← SQL Server/PostgreSQL parity harness (see "Parity harness" below)
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
- **Tenant provisioning:** `SUPERSEDED by PR #14.` There is **no HTTP provisioning channel**.
  `POST /tenants`, `DELETE /tenants/{id}` and `POST /tenants/{id}/migrate` return a fixed `405`
  and have **no replacement**. Tenants are created today from the `Tenants` section of
  `appsettings` via `IdentitySeeder.SeedTenantsAsync`. See `docs/TENANT_PROVISIONING.md`.
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

## Third-Party Library License Policy

**Rule:** No third-party library may be added to either repo without prior license verification. Only MIT, Apache 2.0, BSD-2, or BSD-3 licensed libraries are approved. Libraries with revenue thresholds, seat limits, SaaS restrictions, or commercial-use gates are **rejected**.

### Verification Checklist (run before every `npm install` / `dotnet add package`)

1. Check `LICENSE` file in the GitHub repo — must be MIT / Apache 2.0 / BSD
2. Confirm the license text contains **no revenue thresholds, seat caps, or commercial restrictions**
3. Check the npm/NuGet page for any separate "Community vs Enterprise" edition split
4. If a runtime license warning or watermark appears at any point → **stop, reject, find alternative**
5. Document the result in the table below before merging

### Approved Libraries (verified — full audit 2026-05-05)

**Angular Frontend (`myc-erp-demo`)**

| Library | License | Notes |
|---|---|---|
| All `@angular/*` | MIT | ✅ Google Open Source |
| `@ngrx/signals`, `@ngrx/store`, `@ngrx/operators` | MIT | ✅ |
| `@angular/cdk` | MIT | ✅ Grid fallback option |
| `rxjs` | Apache 2.0 | ✅ |
| `@ngx-translate/core`, `http-loader` | MIT | ✅ |
| `echarts` | Apache 2.0 | ✅ Apache Software Foundation |
| `ngx-echarts` | MIT | ✅ |
| `lucide-angular` | ISC | ✅ Permissive |
| Tailwind CSS | MIT | ✅ |
| `husky`, `prettier`, `eslint`, `jest`, `postcss` | MIT | ✅ Dev tools only |
| `@swc/core` | Apache 2.0 | ✅ |
| `semantic-release` | MIT | ✅ |
| `@module-federation/enhanced` | MIT | ✅ |
| `@testing-library/*`, `jest-preset-angular` | MIT | ✅ Test tools only |
| `@nx/*` | MIT | ✅ |
| `exceljs` | MIT | ✅ Grid export to .xlsx |
| `pdfmake` | MIT | ✅ Grid export to PDF (declarative table API) |
| `file-saver` | MIT | ✅ Trigger browser file downloads |

**Grid library — status: pending decision**

| Library | License | Notes |
|---|---|---|
| `ngx-cerious-widgets` | MIT | ✅ No revenue threshold. v1.0.15, peerDeps `>=16.0.0`. Requires `write-excel-file >=1.4.25` (MIT) as peer dep. ⚠️ Must install with `--legacy-peer-deps` and build-test against Angular 20 before committing. If compat fails → use Angular CDK fallback. |

**ERP API (`erp_api`) — .NET NuGet packages**

| Library | License | Notes |
|---|---|---|
| All `Microsoft.*` / `System.*` | MIT | ✅ |
| `Microsoft.EntityFrameworkCore.*` | MIT | ✅ |
| `Npgsql.EntityFrameworkCore.PostgreSQL` | PostgreSQL License (permissive) | ✅ |
| `FluentValidation` | Apache 2.0 | ✅ Jeremy Skinner |
| `Serilog.*` | Apache 2.0 | ✅ |
| `BCrypt.Net-Next` | MIT | ✅ |
| `Swashbuckle.AspNetCore` | MIT | ✅ |
| `Asp.Versioning.*` | MIT | ✅ |
| `AspNetCore.HealthChecks.*` | MIT | ✅ |
| `System.IdentityModel.Tokens.Jwt` | MIT | ✅ Microsoft |
| `NetArchTest.Rules` | MIT | ✅ Test tool only |
| `xunit`, `Moq`, `coverlet.collector` | Apache 2.0 / BSD-3 / MIT | ✅ Test tools only |
| `Mediator.SourceGenerator` + `Mediator.Abstractions` | MIT | ✅ **Active replacement for MediatR** — v3.0.2, source-generated, same IRequest/IRequestHandler API, handlers return ValueTask, behaviors use MessageHandlerDelegate |
| `MailKit` + `MimeKit` | MIT | ✅ SMTP email sending with attachments. jstedfast/MailKit. Replaces System.Net.Mail (obsolete). |

### Rejected / Conditional Libraries

| Library | License | Reason |
|---|---|---|
| `MediatR` / `MediatR.Contracts` | RPL 1.5 + Commercial | 🚫 **REMOVED 2026-05-05.** RPL 1.5 = viral copyleft: deploying software using MediatR requires releasing full source code OR purchasing a commercial license. Replaced by `Mediator` (MIT). Do not re-add. |
| AG Grid Enterprise | Commercial | Revenue-based pricing; watermark on free tier |
| Kendo UI for Angular | Commercial | Per-developer seat license |
| Syncfusion | Community | Free only if individual revenue <$1M |
| DevExtreme | Dual (MIT/Commercial) | MIT only for non-commercial use |

### Grid Fallback Strategy

If `ngx-cerious-widgets` fails Angular 20 compatibility test:
- **Use `@angular/cdk/table`** (already in project, MIT, zero install needed)
- Build `libs/ui/src/lib/grid/` wrapper with identical `ColumnDef / PaginationEvent` API
- Consumer components require zero changes — same inputs/outputs contract

---

## Phase Status

- [x] Phase 0: Solution structure, SharedKernel, Persistence, Infrastructure, Identity, Notifications, API Host
- [x] Phase 1: Identity APIs (login, register, role/permission management) + Workflow engine (ProcessTemplate, ProcessStep CRUD)
- [~] Phase 2: Production module — WorkOrder & JobCard entities, CRUD commands/queries, controllers — **PARTIAL: see "Phase 2 Audit Gaps" below**
- [x] Phase 2.5A: Master DB architecture (MasterDbContext, redesigned identity entities, DesignTimeDbContextFactories)
- [x] Phase 2.5B: Tenant provisioning API (TenantsController, TenantManagementService, TenantProvisioningService, MasterDbTenantStore) — **`SUPERSEDED` by PR #14 (merge `1f42ec6`):** Create / Deactivate / Migrate were **withdrawn** at the HTTP boundary (fixed `405`, no replacement); `GET`/`PUT` are now tenant-scoped. `TenantProvisioningService` remains in the codebase with **zero production callers** (tracked as `SEC-18`)
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
- [x] **Phase 2 Completion:** All 10 audit-gap items closed. API: AuditLogInterceptor, PlantAuthorizationBehavior, UserPreferences, Plants CRUD. Angular: Skeleton loader, @defer rollout, PreferenceStore (API-backed + localStorage), sidebar collapsed-expand fix, multi-hierarchy menu seed (3-level), component folder structure audit.
- [x] **Phase 2 UI Polish:** Sidebar brand/footer alignment (host height chain), collapsed section icons with primary color + CSS tooltip, full-height flyout (host + `.myc-flyout { height: 100% }`), real user data in header profile dropdown (AuthStore computed signals), `NavSection.icon` propagated via MenuStore, convergence-based IdentitySeeder (INSERT/UPDATE/SOFT-DELETE/RE-ACTIVATE by Code — idempotent, runs every boot).
- [x] **License Policy Checkpoint:** Full audit completed 2026-05-05. All Angular deps clean (MIT/Apache). MediatR identified as RPL 1.5 (viral copyleft — must replace). Replacement: `Mediator` by martinothamar (MIT). Policy documented in "Third-Party Library License Policy" section.
- [x] **MediatR Migration:** Replaced `MediatR` (RPL-1.5) + `MediatR.Contracts` with `Mediator.SourceGenerator` + `Mediator.Abstractions` v3.0.2 (MIT). All 20 handlers migrated (Task→ValueTask), 3 behaviors updated (MessageHandlerDelegate), DomainEventDispatcherInterceptor (IPublisher→IMediator), Program.cs (AddMediatR→AddMediator). Build: 0 errors. Tests: 111/111 + 13/13 ✅
- [x] **Phase 2.9 — Admin Foundations:**
  - [x] **2.9A — Shared UI Components:** Modal/Dialog (`libs/ui/src/lib/modal/`), ConfirmDialog, CheckboxDropdown (`libs/ui/src/lib/checkbox-dropdown/`), ToastService + ToastContainer (`libs/ui/src/lib/toast/`). All standalone, OnPush, BEM, design tokens.
  - [x] **2.9B — User Management:** UsersPage + RolesPage refactored to use shared Modal, ConfirmDialog, CheckboxDropdown, ToastService. Delete uses ConfirmDialog instead of `window.confirm`. Toast notifications on all CRUD operations.
  - [x] **2.9C — Email Service + Forgot/Reset Password:** `IEmailService` abstraction in `ERP.Application.Abstractions.Email`, `SmtpEmailService` (MailKit) in `ERP.Infrastructure.Email`, `EmailSettings` config. `ForgotPasswordAsync` sends email in production, returns token in dev. Angular pages already existed (ForgotPasswordComponent, ResetPasswordComponent).
  - [x] **2.9D — Grid Export:** `GridExportService` in `libs/ui/src/lib/grid-export/` — ExcelJS (.xlsx), CSV (built-in), pdfmake (PDF). Grid component gets `[exportable]` and `[exportFileName]` inputs. Export buttons appear in grid footer.
- [x] **Phase 2.9E — UI Polish & Admin Completion:**
  - [ ] **2.9E-0 — Nx Serve Warnings:** Fix 3 warnings: (1) update `baseline-browser-mapping`, (2) update `caniuse-lite` / Browserslist DB, (3) set `tsconfig.json` target to ES2022 to suppress Angular CLI warning.
  - [x] **2.9E-1 — UI Alignment & Spacing:** Modal button order (primary action first, Cancel last, right-aligned with gap). `[modal-footer]` flex styles in consumer components. Tab spacing `margin-bottom: 1.25rem`. Files: `users-page.ts`, `roles-page.ts`, `confirm-dialog.ts`.
  - [x] **2.9E-2 — Tenant Mapping for Users:** API: `IUserRepository.GetUserTenantsAsync/AddUserTenantAsync/RemoveUserTenants`, `UserService.GetUserTenantsAsync/AssignTenantsAsync`, `UsersController GET/PUT /users/{id}/tenants`. Angular: `TenantApiService`, "Tenants" tab with CheckboxDropdown in users-page modal. 5 new unit tests. Tests: 117/117 + 13/13 ✅
  - [x] **2.9E-3 — Permissions Tree UI:** API: `MenuService.GetPermissionTreeAsync()` returns Module → Menu → Permissions tree, `MenusController GET /menus/permission-tree`. Angular: `TreeCheckbox` component (`libs/ui/src/lib/tree-checkbox/`) with expand/collapse, parent toggle, indeterminate state. `roles-page.ts` refactored to use TreeCheckbox + `MenuApiService.getPermissionTree()` instead of flat category list.
  - [x] **2.9E-4 — Tenant Management Screen:** `tenants-page.ts` in `libs/feature/user-management/`. Grid: Name, Code, DB Provider, Status, Users, Created. Modal: create (Code/Name/Provider/ConnectionString) and edit (Name/Active). Actions: Edit, Migrate DB, Deactivate. Route wired in `user-management.routes.ts`. `TenantApiService` full CRUD + migrate. — **`SUPERSEDED`:** this UI calls endpoints the backend now refuses (Create/Deactivate/Migrate ⇒ `405`; toggling Active ⇒ `400 Tenant.ActivationImmutable`). Scheduled for removal by frontend Tenant reconciliation **F-1…F-5** (`TEN-FE-01..05`), **not yet implemented**.
  - [x] **2.9E-5 — Login Redirect Bug:** `isStoredTokenValid()` in AuthStore decodes JWT + checks `exp` with 30s clock-skew. `LoginComponent.submit()` resets `hasNavigated = false`. Removed debug console.log.
  - [x] **2.9E-6 — User Management Unit Tests:** 77 tests across UsersPage (30), RolesPage (25), TenantsPage (22). Covers: CRUD, form validation, roles/tenants tabs, delete confirmation, toggle active, error handling. Removed `passWithNoTests` workaround.
  - [x] **2.9E-7 — CI Lint Fixes:** Added missing `.eslintrc.json` for user-management lib, missing peerDependencies in `libs/ui/package.json`, `label-has-associated-control` accessibility fixes (for/id pairs on all form labels). CI green: PR #17 merged.
- [~] **Phase 2.9F — Grid Library Decision:**
  - [ ] **`ngx-cerious-widgets` compatibility test:** v1.0.15, MIT license, peerDeps `>=16.0.0` (should work with Angular 20). Also requires `write-excel-file` (MIT) as peer dependency. Install with `--legacy-peer-deps` and build-test. If compat fails → CDK table fallback.
- [~] **Database provider parity foundation (master-plan Phase 1) — P1-b – P1-k MERGED; P1-l (documentation reconciliation) IN PROGRESS.** Merged backend slices, newest last: **P1-b** provider-selection resolver + fail-closed `Database:Provider` (PR #16, `5a290a8`) · **P1-c** Master runtime provider selection (PR #17, `34fcf46`) · **P1-d** Master design-time provider selection (PR #18, `4d26645`) — closes `DB-01` · **P1-e** provider-default divergence, tenant design-time + provisioning fail closed (PR #19, `727a6577`) — closes `DB-04` · **P1-f** `Tenant.Code` canonicalization (PR #20, `5650c0b`) — closes `DB-10` · **P1-g** `WorkOrder.DueDate` → `DateOnly?` (PR #22, `c9791f6`) · **P1-h** UTC `DateTime` persistence (PR #23, `0cad99c`) · **P1-i** PostgreSQL MasterDb baseline (PR #24, `0f183b0`) · **P1-j** PostgreSQL tenant baseline (PR #25, `08db90b`) — closes `DB-02` · **P1-k** provider parity verification (PR #26, `fac345c`) — `DB-03` **PARTIALLY OPEN** (`L-10` upgrade parity deferred). Still open: `DB-09` `Plant.Code` divergence (documented, unresolved); `DB-11` precheck must be **run** per environment (runbook in `docs/TENANT_PROVISIONING.md`). ⛔ `DB-08` (`ERP.DbMigrator`) is **not** Phase 1 — do not modify `Program.cs`. Slice ledger and approval gates: `docs/SATY_ERP_MASTER_EXECUTION_PLAN.md` §8 Phase 1 (frontend repo).
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

**All Angular work complete.** `PreferenceStore` wired, sidebar-collapsed persisted, @defer + skeleton, component folders, 3-level menu seed.

### B. Dynamic Menu — Hierarchy Display & Collapsed-Click (Phase 2.5D) ✅ DONE

**API:** ✅ Fully supports unlimited depth.
**Seed data:** ✅ IdentitySeeder now seeds 3-level Production hierarchy (Module → Work Orders → Create/List/Approvals; Job Cards → Create/List).
**Sidebar collapsed-icon click:** ✅ `MainLayoutComponent.toggleFlyout()`: first click on parent while collapsed = expand sidebar only; second click = open flyout.

### C. UI Standards & Reusability ✅ DONE

1. ✅ **Skeleton Loader** — `libs/ui/src/lib/skeleton/skeleton.ts` — rectangle/circle/text shapes, pulse/wave CSS animations. No Kendo dependency.
2. ✅ **`@defer` rollout** — Dashboard all 4 sections use `@defer (when !store.loading())` / `@defer (when store.stats().length > 0)` with skeleton placeholders.
3. ✅ **Component folder structure** — 5 flat layout components moved to own subfolders. `index.ts` added to 20+ components across `libs/feature/dashboard/` and `libs/ui/`.
4. ✅ **CSS naming convention is BEM** (`myc-{block}__{element}--{modifier}`) — confirmed standard.
5. ⏳ **Graphify (HotChocolate + Apollo Angular)** — deferred to post-Phase 3. Start with read queries (Menus, WorkOrders, JobCards).

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

**Phase 2 is CLOSED. All 10 audit-gap items completed. Next: Phase 3 Prep.**

**Phase 2 final status:**

1. ✅ **AuditLogInterceptor** — `AuditLogInterceptor.cs` in `ERP.Persistence/Interceptors/`. `AuditLog` moved to `ERP.SharedKernel.Entities`.
2. ✅ **UserPlantAccess enforcement** — `PlantAuthorizationBehavior` + `IPlantScopedRequest`. `WorkOrder.PlantId` + migration.
3. ✅ **UserPreference store (API)** — `UserPreferencesController` + upsert command + queries.
4. ✅ **Plants CRUD** — `PlantsController` + `PlantCommands.cs` + `PlantQueries.cs`.
5. ✅ **Skeleton loader component** — `libs/ui/src/lib/skeleton/` (rectangle/circle/text, pulse/wave animations, CSS custom properties).
6. ✅ **`@defer` rollout** — Dashboard converted: hero-banner, stat-cards-row, charts-row, transactions-table all use `@defer` with skeleton placeholders.
7. ✅ **Multi-hierarchy menu seed** — IdentitySeeder now seeds Production module with Work Orders (parent) → List/Create/Approvals (children), Job Cards (parent) → List/Create (children). 3-level hierarchy.
8. ✅ **Sidebar collapsed-icon expand** — `toggleFlyout()` in `MainLayoutComponent`: if `sidebarCollapsed()`, expand only (no flyout). Second click then opens flyout.
9. ✅ **Component folder structure** — Moved 5 flat layout components into own subfolders (`layout/flyout/`, `layout/header/`, `layout/main-layout/`, `layout/sidebar/`, `layout/submenu/`). Added `index.ts` to 20+ dashboard + ui components.
10. ✅ **Angular PreferenceStore** — `PreferenceStore` in `libs/data-access/identity/`. API-backed (`PUT /preferences/{key}`) + localStorage fallback. `sidebarCollapsed` computed signal drives `MainLayoutComponent`. Auto-loads on auth, clears on logout.

**Graphify (HotChocolate + Apollo Angular)** — deferred; not started yet.

**Phase 2.9A–D — COMPLETED.** Shared UI components, User/Role management, Email service, Grid export all shipped.

**Phase 2.9E — UI Polish & Admin Completion — DONE:**
- 2.9E-1 through 2.9E-7: ✅ All done (including 77 unit tests + CI lint fixes)
- 2.9E-0 (Nx Serve Warnings): ⏳ Deferred — low priority, cosmetic only

**Bug fix (2026-05-09):** `AuditLog` entity was accidentally included in `MasterDbContext` model via assembly scanning (`AuditLogConfiguration` lives in `ERP.Persistence`, same assembly `MasterDbContext` scans). Fixed by adding predicate filter to `ApplyConfigurationsFromAssembly`. Migration regenerated — `AuditLogs` table no longer created in Master DB.

**Docs (2026-05-09):** Added "Concrete Example: Admin User Flow" section to `docs/DATABASE_ARCHITECTURE.md` — traces admin user through all tables for login, sidebar, business requests, permission tree, role comparison, and multi-tenant assignment.

**Angular CI fixes (2026-05-13):** Added `.eslintrc.json` for user-management lib (was missing, causing "all files ignored" error). Added peerDependencies to `libs/ui/package.json` (`@angular/forms`, `exceljs`, `file-saver`, `pdfmake`). Fixed `label-has-associated-control` accessibility errors with `for`/`id` pairs. PR #17 merged, CI green.

**Start here next: Phase 2.9F (Grid Library Decision), then Phase 3 Prep.**

**Phase 2.9F — Grid Library Decision:**
- `ngx-cerious-widgets` v1.0.15 (MIT, peerDeps `>=16.0.0`). Requires `write-excel-file` (MIT) as peer dep.
- Install on a feature branch with `--legacy-peer-deps`, run `nx build erp-shell` + `nx test ui` to verify Angular 20 compat.
- If compat fails → fallback to `@angular/cdk/table` wrapper in `libs/ui/src/lib/grid/`.
- The project already has a custom `Grid` component (`libs/ui/src/lib/grid/grid.ts`) — evaluate whether `ngx-cerious-widgets` replaces it or complements it.

**Phase 3 Prep (after grid decision):**
- Sample seed users (Operator/Inspector/Viewer multi-tenant) — already partially done (sample users in IdentitySeeder for Dev), needs multi-tenant cross-assignment
- ~~Tenant dropdown UX — public list-tenants endpoint → Angular tenant selector at login~~
  **WITHDRAWN 2026-09-22 — DO NOT IMPLEMENT.** `SUPERSEDED`. Cross-tenant enumeration via
  `GET /tenants` is exactly the vulnerability closed by PR #14; a *public* variant would
  reintroduce it unauthenticated. `GET /tenants` now returns the caller's own tenant only.
  The login tenant is supplied by the client via `X-Tenant-Id`. Any future tenant-selector
  UX must be designed against `docs/SATY_ERP_MASTER_EXECUTION_PLAN.md` §4 (frontend repo).
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
- Identity seeder runs on startup — creates default admin (user `admin`; password from `SeedSettings:DefaultAdminPassword` — not documented), 4 roles, 28 permissions
- Workflow engine is table-based (no Elsa) — clients modify flow via CRUD on ProcessTemplate/ProcessStep
- `AuditLog` entity lives in `ERP.SharedKernel.Entities` (not Production.Domain). Its EF config is in `ERP.Persistence/Configurations/AuditLogConfiguration.cs`. `AuditLogInterceptor` populates it — wired in `TenantDbContextFactory` only (not MasterDbContext, since AuditLogs are tenant-DB only). `MasterDbContext.OnModelCreating` explicitly excludes `AuditLogConfiguration` via predicate filter on `ApplyConfigurationsFromAssembly` to prevent the tenant-only table from appearing in Master DB migrations.
- Pipeline behavior execution order: `ValidationBehavior` → `PlantAuthorizationBehavior` → `LoggingBehavior` → Handler. Plant auth only fires for commands implementing `IPlantScopedRequest` (in `ERP.Application.Abstractions.CQRS`).
- `ICurrentUserService` now exposes `IpAddress` and `UserAgent` (added for `AuditLogInterceptor`).
- **AppDbContext migrations must use `tools/ERP.DbMigrator` as the startup project** — `ERP.Api` fails at design time because `HttpContext` is unavailable. Command: `dotnet ef migrations add <Name> --context AppDbContext --project src/Infrastructure/ERP.Persistence.SqlServer --startup-project tools/ERP.DbMigrator --output-dir Migrations/AppDb`. For PostgreSQL use `--project src/Infrastructure/ERP.Persistence.PostgreSql` with `Database__Provider=PostgreSql` (and `ConnectionStrings__MasterDb` / `Tenants__0__ConnectionString` as env overrides); output dirs are `Migrations/MasterDb` and `Migrations/AppDb`.
- **Parity harness** (`tools/ERP.ParityCheck`): `--self-test` / `--simulate` are fully offline. `--live` requires explicit authorization **plus** `ERP_PARITY_SQLSERVER` / `ERP_PARITY_POSTGRESQL` server-level endpoints, and only ever creates/drops the approved `ERP_P1k_Parity_*_Throwaway` databases (protected names and pre-existing targets are refused). Never point it at a real database; never commit its evidence. Results and accepted differences: `docs/DATABASE_ARCHITECTURE.md` → *Provider Mapping*.

## CI/CD Status

- **API (.NET 10):** GitHub Actions workflow — `dotnet build ERP.slnx` → `dotnet test ERP.slnx`. NuGet cache enabled. Permissions `contents: read`. Currently green: 117/117 unit tests + 13/13 architecture guards.
- **UI (Angular):** GitHub Actions workflow — `npm ci` → `npx nx run-many --target lint --all` → `npx nx run-many --target test --all`. npm cache enabled. Currently green: 130+ tests across 18 projects (77 in user-management alone).
- **`.gitattributes`** enforces LF line endings repo-wide (eliminates CRLF noise on Windows contributors).
- **Recommended next:** Branch protection on `main` requiring CI green + 1 review.

## Design Patterns (20 Used)

Repository, Unit of Work, CQRS, Mediator, Factory, Strategy, Middleware/Chain of Responsibility,
Pipeline, Result Pattern, Value Object, Domain Events, Dependency Inversion, Guard Clause,
Soft Delete, Layer Supertype, DI Lifetimes (Singleton/Scoped/Transient), Extension Method,
Options Pattern, Interceptor, Composition Root

See `docs/DESIGN_PATTERNS.md` for detailed explanations with interview tips.
See `docs/ARCHITECTURE.md` for concept deep-dives with code examples.
