# ERP Database Architecture

> Complete reference for all database tables, relationships, and multi-tenant design.
> Updated: 2026-05-09

## Table of Contents

1. [Multi-Tenant Database Design](#multi-tenant-database-design)
2. [Master DB Tables](#master-db-tables-erp_master)
3. [Tenant DB Tables](#tenant-db-tables-erp_tenantcode)
4. [Provider Mapping: SQL Server and PostgreSQL](#provider-mapping-sql-server-and-postgresql)
5. [Seeded Data](#seeded-data)
6. [Entity Relationship Diagrams](#entity-relationship-diagrams)
7. [Data Flow](#data-flow)
8. [Concrete Example: Admin User Flow](#concrete-example-admin-user-flow)

---

## Multi-Tenant Database Design

### Architecture: Hybrid (Master + Per-Tenant Databases)

```
                        ┌─────────────────────────────────────┐
                        │         MASTER DATABASE              │
                        │          (ERP_Master)                │
                        │                                     │
                        │  Identity & Access Control:         │
                        │    Tenants, Users, UserTenants,     │
                        │    UserSessions, UserPasswordHistory│
                        │    Roles, Permissions, UserRoles,   │
                        │    RolePermissions                  │
                        │                                     │
                        │  Navigation & Features:             │
                        │    Modules, Menus, MenuPermissions, │
                        │    FeatureFlags, TenantFeatureFlags │
                        │                                     │
                        │  Audit:                             │
                        │    IdentityAuditLogs                │
                        └──────────┬──────────┬───────────────┘
                                   │          │
                    ┌──────────────┘          └──────────────┐
                    ▼                                        ▼
        ┌───────────────────────┐            ┌───────────────────────┐
        │   TENANT DB           │            │   TENANT DB           │
        │   (ERP_ABC)           │            │   (ERP_XYZ)           │
        │                       │            │                       │
        │  Business:            │            │  Business:            │
        │    Plants,            │            │    Plants,            │
        │    UserPlantAccess,   │            │    UserPlantAccess,   │
        │    UserPreferences    │            │    UserPreferences    │
        │                       │            │                       │
        │  Workflow:            │            │  Workflow:            │
        │    ProcessTemplates,  │            │    ProcessTemplates,  │
        │    ProcessSteps       │            │    ProcessSteps       │
        │                       │            │                       │
        │  Production:          │            │  Production:          │
        │    WorkOrders,        │            │    WorkOrders,        │
        │    JobCards           │            │    JobCards           │
        │                       │            │                       │
        │  Audit:               │            │  Audit:               │
        │    AuditLogs          │            │    AuditLogs          │
        └───────────────────────┘            └───────────────────────┘
```

### Why This Split?

| Decision | Reason |
|----------|--------|
| Users in Master DB | Single login across tenants (SSO), no duplicate users, centralized auth |
| Roles/Permissions in Master DB | FK constraints to Users work; system roles shared, tenant roles via TenantId |
| Menus in Master DB | Product defines navigation; visibility controlled by permissions + feature flags |
| Business data in Tenant DB | Complete isolation, independent scaling, data sovereignty |
| AuditLogs split | Identity audits with identity data; business audits with business data |

### Cross-Database Reference Rule

Master DB tables have full FK constraints (same database).
Tenant DB tables that reference Master DB users (e.g., `UserPlantAccess.UserId`) store the `UserId` as UNIQUEIDENTIFIER **without FK constraint** — validated in application code. This is the industry-standard pattern for cross-database references.

---

## Master DB Tables (ERP_Master)

### Base Columns

All tables inheriting `AuditableEntity` include these columns:

| Column | Type | Constraint | Purpose |
|--------|------|------------|---------|
| `Id` | `UNIQUEIDENTIFIER` | PK, NOT NULL | App-generated GUID |
| `IsActive` | `BIT` | NOT NULL, DEFAULT 1 | Business-level enable/disable |
| `IsDeleted` | `BIT` | NOT NULL, DEFAULT 0 | System-level soft delete (EF global filter) |
| `CreatedAt` | `DATETIME2` | NOT NULL | UTC creation timestamp |
| `CreatedBy` | `UNIQUEIDENTIFIER` | NULL | FK to Users (who created) |
| `LastModifiedAt` | `DATETIME2` | NULL | UTC last modification timestamp |
| `LastModifiedBy` | `UNIQUEIDENTIFIER` | NULL | FK to Users (who modified) |
| `DeletedAt` | `DATETIME2` | NULL | UTC soft-delete timestamp |
| `DeletedBy` | `UNIQUEIDENTIFIER` | NULL | FK to Users (who deleted) |

> `IsActive = false` means "suspended/disabled" (reversible by admin).
> `IsDeleted = true` means "logically removed" (hidden from all queries by EF global filter).
> Never physically delete records in an ERP system.

---

### `Companies`

**Purpose:** The organisational grouping above Tenant (approved model: Company 1:N Tenant).
Master DB only — a Company has no business database of its own. Added by migration
`AddCompanyFoundation` (P2-a, `ARCH-IMPL-01`).

| Column | Type | Constraint | Purpose |
|--------|------|------------|---------|
| `Id` | `UNIQUEIDENTIFIER` | PK (`PK_Companies`), application-assigned | Company internal ID |
| `Code` | `NVARCHAR(50)` | NOT NULL, UNIQUE (`IX_Companies_Code`) | Company code |
| `Name` | `NVARCHAR(200)` | NOT NULL | Display name |
| `IsActive` | `BIT` | NOT NULL (no database default) | Soft-disable flag — **stored only; nothing enforces it yet** |
| `CreatedAt` | `DATETIME2` | NOT NULL | When the company was created (UTC) |

**Notes:**
- **No rows are created by the schema.** The migration adds the table empty; no default or
  placeholder Company exists, and no application code writes Companies yet.
- **`Code` contract:** the same canonical form as `Tenant.Code` — `Trim()` + `ToLowerInvariant()`,
  enforced by the application. The unique index is plain (no filter, no collation), so on its own
  it does **not** make codes case-insensitive. The application-level normalizer does not exist yet;
  it arrives with the first approved slice that writes Companies.
- Company membership does **not** grant Tenant access; Tenant access remains `UserTenants`.

---

### `Tenants`

**Purpose:** Registry of all tenant organizations. Each tenant gets its own business database.

| Column | Type | Constraint | Purpose |
|--------|------|------------|---------|
| `Id` | `UNIQUEIDENTIFIER` | PK | Tenant internal ID |
| `Code` | `NVARCHAR(50)` | UNIQUE, NOT NULL | Short code sent in X-Tenant-Id header. **Stored canonically: trimmed, lowercase** (e.g. `"dev-tenant"`) — see the canonical-form note below |
| `Name` | `NVARCHAR(200)` | NOT NULL | Display name (e.g., "ABC Manufacturing Ltd") |
| `ConnectionString` | `NVARCHAR(500)` | NOT NULL | Encrypted connection string to tenant DB |
| `DbProvider` | `NVARCHAR(50)` | NOT NULL | "SqlServer" or "PostgreSql" |
| `IsActive` | `BIT` | NOT NULL, DEFAULT 1 | Disabled tenants cannot log in |
| `CreatedAt` | `DATETIME2` | NOT NULL | When tenant was provisioned |
| `CompanyId` | `UNIQUEIDENTIFIER` | **NULL**, FK → Companies(Id) `ON DELETE RESTRICT` (`FK_Tenants_Companies_CompanyId`), indexed (`IX_Tenants_CompanyId`, non-unique) | Owning company. **NULL = no company association** — existing tenants keep NULL; nothing assigns them. Added by `AddCompanyFoundation` (P2-a, `ARCH-IMPL-02`) |

**Notes:**
- This is the first table queried on every request (cached 30 min by TenantMiddleware)
- ConnectionString should be encrypted at rest in production
- DbProvider determines which EF Core provider to use

#### `Code` — canonical form (`DB-10`, merged 2026-09-27)

**`Tenant.Code` is a logically CASE-INSENSITIVE identifier whose canonical persisted
representation is `Trim()` + `ToLowerInvariant()`** (invariant culture, so the result cannot
depend on the server's locale).

Every application write stores the canonical form and every application lookup compares the
canonical form, so **tenant identity does not depend on the database's collation**. This
matters because the two supported engines disagree: SQL Server's default
`SQL_Latin1_General_CP1_CI_AS` is case-**insensitive** and was silently making mixed-case
codes work, while PostgreSQL compares case-**sensitively** and the same request would not have
resolved at all. The unique index on `Code` also only means what it should once writes are
canonical — on a case-sensitive engine it would otherwise accept `acme` **and** `ACME` as two
different tenants.

> ⚠️ **Enforced by the application, NOT by the database.** There is deliberately **no**
> `CHECK` constraint, **no** `NormalizedCode` column, **no** `citext`, and **no**
> provider-specific collation. A row written outside the application — direct SQL, an import,
> a manual fix — can therefore still hold a non-canonical code, and because lookups
> canonicalize, **such a row is unreachable**. This is why the `DB-11` deployment precheck in
> `TENANT_PROVISIONING.md` is mandatory.

Narrative examples elsewhere in this document still show an uppercase `X-Tenant-Id: ABC`.
Those remain **valid as request examples** — lookups normalize, so `ABC` resolves a stored
`abc` — but they do **not** describe the stored value. Reconciling the example casing
throughout is documentation debt for **P1-l**; it is not a defect in `DB-10`.

**Separate, unfixed debt:** `Plant.Code` (tenant databases) has the *opposite* and
*inconsistent* convention — `PlantCommands.cs` persists `ToUpperInvariant()` with **no
trim**, while its duplicate check compares the **raw** request value. SQL Server's
case-insensitive collation masks this today; **PostgreSQL would expose duplicate semantics**.
Tracked as `DB-09`; **deliberately not fixed** by `DB-10` and awaiting its own scope decision.
Do not copy the `Tenant.Code` helper onto it — the direction and the trim behaviour differ.

---

### `Users` (AuditableEntity)

**Purpose:** Global user identity. A person, not a tenant-scoped record. Same user can access multiple tenants.

| Column | Type | Constraint | Purpose |
|--------|------|------------|---------|
| `UserName` | `NVARCHAR(100)` | UNIQUE, NOT NULL | Login username |
| `NormalizedUserName` | `NVARCHAR(100)` | UNIQUE, NOT NULL | Uppercase for case-insensitive lookup |
| `Email` | `NVARCHAR(200)` | UNIQUE, NOT NULL | User email |
| `NormalizedEmail` | `NVARCHAR(200)` | UNIQUE, NOT NULL | Uppercase for case-insensitive lookup |
| `PasswordHash` | `NVARCHAR(500)` | NOT NULL | BCrypt hash |
| `SecurityStamp` | `NVARCHAR(100)` | NOT NULL | Random GUID, changes on password/security change; invalidates all tokens |
| `ConcurrencyStamp` | `NVARCHAR(100)` | NOT NULL | Optimistic concurrency; prevents simultaneous edits |
| `FirstName` | `NVARCHAR(100)` | NOT NULL | |
| `LastName` | `NVARCHAR(100)` | NOT NULL | |
| `DisplayName` | `NVARCHAR(200)` | NULL | Optional display name (falls back to FirstName + LastName) |
| `PhoneNumber` | `NVARCHAR(20)` | NULL | |
| `IsLocked` | `BIT` | NOT NULL, DEFAULT 0 | Account locked due to failed attempts |
| `LockoutEnd` | `DATETIME2` | NULL | When lockout expires (NULL = not locked) |
| `AccessFailedCount` | `INT` | NOT NULL, DEFAULT 0 | Consecutive failed login attempts |
| `LastLoginAt` | `DATETIME2` | NULL | Last successful login |
| `LastPasswordChangedAt` | `DATETIME2` | NULL | Last password change timestamp |
| `MustChangePassword` | `BIT` | NOT NULL, DEFAULT 0 | Force password change on next login |
| + AuditableEntity columns | | | See base columns above |

**Indexes:** `NormalizedUserName` (unique), `NormalizedEmail` (unique), `IsDeleted`

**Lockout flow:**
1. Failed login → increment `AccessFailedCount`
2. If `AccessFailedCount >= 5` → set `IsLocked = true`, `LockoutEnd = now + 15 min`
3. Successful login → reset `AccessFailedCount = 0`, `IsLocked = false`
4. Admin can manually unlock

---

### `UserTenants`

**Purpose:** Maps users to tenants (many-to-many). A CEO can access multiple tenants with different roles.

| Column | Type | Constraint | Purpose |
|--------|------|------------|---------|
| `Id` | `UNIQUEIDENTIFIER` | PK | |
| `UserId` | `UNIQUEIDENTIFIER` | NOT NULL, FK → Users(Id) | |
| `TenantId` | `UNIQUEIDENTIFIER` | NOT NULL, FK → Tenants(Id) | |
| `IsDefault` | `BIT` | NOT NULL, DEFAULT 0 | Default tenant on login (user can switch) |
| `HasGlobalPlantAccess` | `BIT` | NOT NULL, DEFAULT 0 | Can access ALL plants in this tenant |
| `IsActive` | `BIT` | NOT NULL, DEFAULT 1 | Disable access without removing record |
| `JoinedAt` | `DATETIME2` | NOT NULL | When user was granted tenant access |

**Unique constraint:** `(UserId, TenantId)`

---

### `UserSessions`

**Purpose:** Tracks active login sessions. Supports multi-device login, token revocation, session management.

| Column | Type | Constraint | Purpose |
|--------|------|------------|---------|
| `Id` | `UNIQUEIDENTIFIER` | PK | |
| `UserId` | `UNIQUEIDENTIFIER` | NOT NULL, FK → Users(Id) | |
| `TenantId` | `UNIQUEIDENTIFIER` | NOT NULL, FK → Tenants(Id) | Which tenant context |
| `RefreshToken` | `NVARCHAR(500)` | NOT NULL | Cryptographic refresh token |
| `RefreshTokenExpiry` | `DATETIME2` | NOT NULL | When refresh token expires |
| `AccessTokenJti` | `NVARCHAR(100)` | NOT NULL | JWT unique ID; enables token blacklisting |
| `DeviceInfo` | `NVARCHAR(500)` | NULL | User-Agent / device description |
| `IpAddress` | `NVARCHAR(100)` | NULL | Client IP at login |
| `Location` | `NVARCHAR(200)` | NULL | Geo-location (optional, from IP) |
| `IsRevoked` | `BIT` | NOT NULL, DEFAULT 0 | Manually or automatically revoked |
| `RevokedAt` | `DATETIME2` | NULL | When session was revoked |
| `RevokedReason` | `NVARCHAR(200)` | NULL | "Logout", "PasswordChanged", "AdminRevoked", "Expired" |
| `ExpiresAt` | `DATETIME2` | NOT NULL | Absolute session expiry (independent of refresh token) |
| `CreatedAt` | `DATETIME2` | NOT NULL | Session start |
| `LastAccessedAt` | `DATETIME2` | NULL | Updated on each token refresh |

**Indexes:** `UserId`, `RefreshToken` (unique), `AccessTokenJti`

**Token refresh flow:**
1. Client sends expired access token + refresh token
2. Server finds session by refresh token
3. Validates: not revoked, not expired, user active
4. Issues new access token + new refresh token (rotation)
5. Updates `LastAccessedAt`, replaces `RefreshToken` and `AccessTokenJti`

---

### `UserPasswordHistory`

**Purpose:** Prevent password reuse. Check last N password hashes on password change.

| Column | Type | Constraint | Purpose |
|--------|------|------------|---------|
| `Id` | `UNIQUEIDENTIFIER` | PK | |
| `UserId` | `UNIQUEIDENTIFIER` | NOT NULL, FK → Users(Id) | |
| `PasswordHash` | `NVARCHAR(500)` | NOT NULL | BCrypt hash of previous password |
| `ChangedAt` | `DATETIME2` | NOT NULL | When this password was set |

**Index:** `UserId`

**Rule:** On password change, check last 5 hashes. If match found, reject with "Cannot reuse recent passwords."

---

### `Roles` (AuditableEntity)

**Purpose:** Defines user roles. System roles are shared across all tenants. Tenants can create custom roles.

| Column | Type | Constraint | Purpose |
|--------|------|------------|---------|
| `Name` | `NVARCHAR(100)` | NOT NULL | Role name |
| `NormalizedName` | `NVARCHAR(100)` | NOT NULL | Uppercase for case-insensitive lookup |
| `Description` | `NVARCHAR(500)` | NULL | Human-readable description |
| `IsSystemRole` | `BIT` | NOT NULL, DEFAULT 0 | System roles cannot be deleted/modified by tenants |
| `TenantId` | `UNIQUEIDENTIFIER` | NULL, FK → Tenants(Id) | NULL = system role (all tenants), GUID = tenant-specific |
| + AuditableEntity columns | | | See base columns above |

**Unique constraint:** `(NormalizedName, TenantId)` — same name allowed across tenants, unique within a tenant

**System roles (seeded, TenantId = NULL):**
- Admin, Production Operator, QA Inspector, Viewer

---

### `Permissions`

**Purpose:** Defines what actions exist in the system. Business-level, not just CRUD.

| Column | Type | Constraint | Purpose |
|--------|------|------------|---------|
| `Id` | `UNIQUEIDENTIFIER` | PK | |
| `Code` | `NVARCHAR(100)` | UNIQUE, NOT NULL | Machine-readable code (e.g., `JOB_CREATE`, `QA_APPROVE`) |
| `Name` | `NVARCHAR(200)` | NOT NULL | Human-readable name (e.g., "Create Job Card") |
| `Category` | `NVARCHAR(100)` | NOT NULL | Grouping for UI (e.g., "Production", "Quality", "Admin") |
| `Description` | `NVARCHAR(500)` | NULL | Detailed explanation |

**Notes:**
- Permissions are system-defined (seeded), not tenant-created
- No AuditableEntity — permissions are immutable reference data
- `Category` replaces the old `Module` column — used to group permissions in the admin UI

---

### `UserRoles`

**Purpose:** Assigns roles to users within a specific tenant context.

| Column | Type | Constraint | Purpose |
|--------|------|------------|---------|
| `Id` | `UNIQUEIDENTIFIER` | PK | |
| `UserId` | `UNIQUEIDENTIFIER` | NOT NULL, FK → Users(Id) | |
| `RoleId` | `UNIQUEIDENTIFIER` | NOT NULL, FK → Roles(Id) | |
| `TenantId` | `UNIQUEIDENTIFIER` | NOT NULL, FK → Tenants(Id) | Role assignment is per-tenant |
| `AssignedAt` | `DATETIME2` | NOT NULL | |
| `AssignedBy` | `UNIQUEIDENTIFIER` | NULL, FK → Users(Id) | Who assigned this role |
| `IsActive` | `BIT` | NOT NULL, DEFAULT 1 | Deactivate without removing |

**Unique constraint:** `(UserId, RoleId, TenantId)`

**Example:**
| User | Tenant | Role |
|------|--------|------|
| John | ABC Mfg | Admin |
| John | XYZ Steel | Viewer |
| Mary | ABC Mfg | Production Operator |
| Mary | ABC Mfg | QA Inspector |

---

### `RolePermissions`

**Purpose:** Maps permissions to roles. Supports deny, conditional (ABAC), and time-bound permissions.

| Column | Type | Constraint | Purpose |
|--------|------|------------|---------|
| `Id` | `UNIQUEIDENTIFIER` | PK | |
| `RoleId` | `UNIQUEIDENTIFIER` | NOT NULL, FK → Roles(Id) | |
| `PermissionId` | `UNIQUEIDENTIFIER` | NOT NULL, FK → Permissions(Id) | |
| `IsAllowed` | `BIT` | NOT NULL, DEFAULT 1 | true = grant, false = explicit deny |
| `ConditionJson` | `NVARCHAR(MAX)` | NULL | ABAC conditions (future use) |
| `ValidFrom` | `DATETIME2` | NULL | Permission starts (NULL = immediately) |
| `ValidTo` | `DATETIME2` | NULL | Permission expires (NULL = never) |
| `CreatedAt` | `DATETIME2` | NOT NULL | |

**Unique constraint:** `(RoleId, PermissionId)`

**ABAC ConditionJson (future, not implemented now):**
```json
{ "PlantId": "User.PlantId" }
{ "Department": "QA" }
{ "MaxAmount": 100000 }
```

---

### `Modules`

**Purpose:** Top-level product modules displayed in the Angular sidebar. Defines the major areas of the ERP.

| Column | Type | Constraint | Purpose |
|--------|------|------------|---------|
| `Id` | `UNIQUEIDENTIFIER` | PK | |
| `Code` | `NVARCHAR(100)` | UNIQUE, NOT NULL | Machine code (e.g., "PRODUCTION", "WORKFLOW", "ADMIN") |
| `Name` | `NVARCHAR(200)` | NOT NULL | Display name (e.g., "Production Management") |
| `Icon` | `NVARCHAR(100)` | NULL | Icon class (e.g., "fas fa-industry", "mat-build") |
| `DisplayOrder` | `INT` | NOT NULL, DEFAULT 0 | Sort order in sidebar |
| `IsActive` | `BIT` | NOT NULL, DEFAULT 1 | Hide entire module |

---

### `Menus`

**Purpose:** Navigation items within modules. Supports unlimited hierarchy via self-referencing ParentMenuId.

| Column | Type | Constraint | Purpose |
|--------|------|------------|---------|
| `Id` | `UNIQUEIDENTIFIER` | PK | |
| `ModuleId` | `UNIQUEIDENTIFIER` | NOT NULL, FK → Modules(Id) | Parent module |
| `Code` | `NVARCHAR(100)` | UNIQUE, NOT NULL | Machine code (e.g., "MENU_JOBCARD_LIST") |
| `Name` | `NVARCHAR(200)` | NOT NULL | Display name (e.g., "Job Cards") |
| `ParentMenuId` | `UNIQUEIDENTIFIER` | NULL, FK → Menus(Id) | NULL = top-level; GUID = child menu |
| `Route` | `NVARCHAR(300)` | NULL | Angular route (e.g., "/production/job-cards") |
| `ComponentName` | `NVARCHAR(200)` | NULL | Angular component (e.g., "JobCardListComponent") |
| `Icon` | `NVARCHAR(100)` | NULL | Menu item icon |
| `DisplayOrder` | `INT` | NOT NULL, DEFAULT 0 | Sort order within parent |
| `IsVisible` | `BIT` | NOT NULL, DEFAULT 1 | Show/hide in navigation |
| `IsEnabled` | `BIT` | NOT NULL, DEFAULT 1 | Visible but greyed out if disabled |
| `FeatureFlag` | `NVARCHAR(100)` | NULL | References FeatureFlags.Code; NULL = always shown |
| `CreatedAt` | `DATETIME2` | NOT NULL | |

**Indexes:** `ModuleId`, `ParentMenuId`

**Hierarchy example:**
```
Module: Production (PRODUCTION)
  ├── Menu: Work Orders (MENU_WO)           route: /production/work-orders
  │     ├── Menu: Create WO (MENU_WO_CREATE)  route: /production/work-orders/create
  │     └── Menu: WO List (MENU_WO_LIST)       route: /production/work-orders
  └── Menu: Job Cards (MENU_JC)              route: /production/job-cards
        ├── Menu: JC Dashboard (MENU_JC_DASH)  route: /production/job-cards/dashboard
        └── Menu: JC List (MENU_JC_LIST)        route: /production/job-cards
```

---

### `MenuPermissions`

**Purpose:** Links menus to required permissions. A menu is visible only if the user has the required permissions.

| Column | Type | Constraint | Purpose |
|--------|------|------------|---------|
| `Id` | `UNIQUEIDENTIFIER` | PK | |
| `MenuId` | `UNIQUEIDENTIFIER` | NOT NULL, FK → Menus(Id) | |
| `PermissionId` | `UNIQUEIDENTIFIER` | NOT NULL, FK → Permissions(Id) | |
| `IsRequired` | `BIT` | NOT NULL, DEFAULT 1 | true = must have this permission to see menu |

**Unique constraint:** `(MenuId, PermissionId)`

**Logic:**
- If `IsRequired = true` for ALL linked permissions → user needs ALL of them (AND logic)
- If `IsRequired = false` for some → user needs at least one required (configurable)
- If menu has NO MenuPermissions → visible to all authenticated users

---

### `FeatureFlags`

**Purpose:** Global feature flag definitions. Controls which features are available.

| Column | Type | Constraint | Purpose |
|--------|------|------------|---------|
| `Id` | `UNIQUEIDENTIFIER` | PK | |
| `Code` | `NVARCHAR(100)` | UNIQUE, NOT NULL | (e.g., "PRODUCTION_V2", "QA_DASHBOARD") |
| `Name` | `NVARCHAR(200)` | NOT NULL | Human-readable name |
| `Description` | `NVARCHAR(500)` | NULL | What this flag controls |
| `IsEnabled` | `BIT` | NOT NULL, DEFAULT 0 | Global default (overridden per tenant) |
| `CreatedAt` | `DATETIME2` | NOT NULL | |

---

### `TenantFeatureFlags`

**Purpose:** Per-tenant feature flag overrides. Enables rolling out features to specific tenants.

| Column | Type | Constraint | Purpose |
|--------|------|------------|---------|
| `Id` | `UNIQUEIDENTIFIER` | PK | |
| `TenantId` | `UNIQUEIDENTIFIER` | NOT NULL, FK → Tenants(Id) | |
| `FeatureFlagId` | `UNIQUEIDENTIFIER` | NOT NULL, FK → FeatureFlags(Id) | |
| `IsEnabled` | `BIT` | NOT NULL | Tenant-level override |
| `EnabledAt` | `DATETIME2` | NOT NULL | |
| `EnabledBy` | `UNIQUEIDENTIFIER` | NULL, FK → Users(Id) | |

**Unique constraint:** `(TenantId, FeatureFlagId)`

**Resolution order:**
1. Check `TenantFeatureFlags` for this tenant + flag → use if exists
2. Fall back to `FeatureFlags.IsEnabled` → global default

---

### `IdentityAuditLogs`

**Purpose:** Audit trail for authentication and identity-related actions.

| Column | Type | Constraint | Purpose |
|--------|------|------------|---------|
| `Id` | `UNIQUEIDENTIFIER` | PK | |
| `TenantId` | `UNIQUEIDENTIFIER` | NULL | NULL for global actions |
| `UserId` | `UNIQUEIDENTIFIER` | NULL | NULL for failed logins with unknown user |
| `Action` | `NVARCHAR(50)` | NOT NULL | Login, LoginFailed, Logout, PasswordChanged, RoleAssigned, PermissionDenied, AccountLocked, AccountUnlocked |
| `EntityType` | `NVARCHAR(200)` | NULL | "User", "Role", "Permission" |
| `EntityId` | `NVARCHAR(100)` | NULL | ID of affected entity |
| `Details` | `NVARCHAR(MAX)` | NULL | JSON with additional context |
| `IpAddress` | `NVARCHAR(100)` | NULL | |
| `UserAgent` | `NVARCHAR(500)` | NULL | Browser/device info |
| `CreatedAt` | `DATETIME2` | NOT NULL | |

**Indexes:** `UserId`, `Action`, `CreatedAt`, `(EntityType, EntityId)`

---

## Tenant DB Tables (ERP_{TenantCode})

Each tenant database contains these tables. They are identical in schema across tenants but hold different data.

### `Plants`

**Purpose:** Physical manufacturing plants/locations belonging to the tenant.

| Column | Type | Constraint | Purpose |
|--------|------|------------|---------|
| `Id` | `UNIQUEIDENTIFIER` | PK | |
| `Code` | `NVARCHAR(50)` | UNIQUE, NOT NULL | Plant code (e.g., "CHN", "BLR") |
| `Name` | `NVARCHAR(200)` | NOT NULL | (e.g., "Chennai Plant") |
| `Location` | `NVARCHAR(200)` | NULL | Address or city |
| `IsActive` | `BIT` | NOT NULL, DEFAULT 1 | |
| `CreatedAt` | `DATETIME2` | NOT NULL | |

**Notes:** No TenantId column needed — the table is already in a tenant-specific database.

---

### `UserPlantAccess`

**Purpose:** Controls which plants a user can access within this tenant. Role = WHAT, Plant = WHERE.

| Column | Type | Constraint | Purpose |
|--------|------|------------|---------|
| `Id` | `UNIQUEIDENTIFIER` | PK | |
| `UserId` | `UNIQUEIDENTIFIER` | NOT NULL | References Master.Users (no FK — cross-DB) |
| `PlantId` | `UNIQUEIDENTIFIER` | NOT NULL, FK → Plants(Id) | |
| `AccessLevel` | `NVARCHAR(50)` | NOT NULL | VIEW, OPERATE, MANAGE, APPROVE |
| `IsDefault` | `BIT` | NOT NULL, DEFAULT 0 | Default plant for this user |
| `IsActive` | `BIT` | NOT NULL, DEFAULT 1 | |
| `GrantedAt` | `DATETIME2` | NOT NULL | |
| `GrantedBy` | `UNIQUEIDENTIFIER` | NULL | References Master.Users (no FK — cross-DB) |

**Unique constraint:** `(UserId, PlantId)`

**Access levels:**

| Level | Meaning | Example |
|-------|---------|---------|
| `VIEW` | Read-only access | Viewer seeing Chennai data |
| `OPERATE` | Create/update own work | Worker creating job cards |
| `MANAGE` | Full CRUD + assign work to others | Floor supervisor |
| `APPROVE` | Approve/reject + all manage rights | Plant manager |

---

### `UserPreferences`

**Purpose:** Per-user, per-tenant settings. Key-value store for UI preferences.

| Column | Type | Constraint | Purpose |
|--------|------|------------|---------|
| `Id` | `UNIQUEIDENTIFIER` | PK | |
| `UserId` | `UNIQUEIDENTIFIER` | NOT NULL | References Master.Users (no FK — cross-DB) |
| `Key` | `NVARCHAR(200)` | NOT NULL | Setting key |
| `Value` | `NVARCHAR(MAX)` | NOT NULL | Setting value (JSON for complex values) |

**Unique constraint:** `(UserId, Key)`

**Common keys:**
| Key | Example Value | Purpose |
|-----|--------------|---------|
| `theme` | `"dark"` | UI theme |
| `language` | `"ta"` | Language preference (Tamil, Hindi, English) |
| `defaultPlant` | `"{plantId}"` | Remembered plant selection |
| `pageSize` | `"50"` | Table rows per page |
| `sidebarCollapsed` | `"true"` | Sidebar state |

---

### `ProcessTemplates` (AuditableEntity)

**Purpose:** Defines manufacturing process flows. Each template is a sequence of steps a product goes through.

| Column | Type | Constraint | Purpose |
|--------|------|------------|---------|
| `Name` | `NVARCHAR(200)` | NOT NULL | Template name (e.g., "CNC Machining Flow") |
| `Description` | `NVARCHAR(1000)` | NULL | |
| `Version` | `INT` | NOT NULL | Version number for revisions |
| `IsActive` | `BIT` | NOT NULL, DEFAULT 1 | |
| + AuditableEntity columns | | | |

**Unique constraint:** `(Name, Version)`

---

### `ProcessSteps` (AuditableEntity)

**Purpose:** Individual steps within a process template. Supports sequential, parallel, and merge flows.

| Column | Type | Constraint | Purpose |
|--------|------|------------|---------|
| `TemplateId` | `UNIQUEIDENTIFIER` | NOT NULL, FK → ProcessTemplates(Id) CASCADE | |
| `DepartmentName` | `NVARCHAR(200)` | NOT NULL | Which department performs this step |
| `Order` | `INT` | NOT NULL | Execution order |
| `StepType` | `NVARCHAR(20)` | NOT NULL | `Single` (sequential), `Parallel` (split), `Merge` (join) |
| `QaRequired` | `BIT` | NOT NULL, DEFAULT 1 | Does this step need QA inspection? |
| `Instructions` | `NVARCHAR(2000)` | NULL | Work instructions |
| `NextStepId` | `UNIQUEIDENTIFIER` | NULL, FK → ProcessSteps(Id) RESTRICT | Self-ref to next step |
| `EstimatedDurationMinutes` | `INT` | NULL | |
| + AuditableEntity columns | | | |

**Composite index:** `(TemplateId, Order)`

---

### `WorkOrders` (AuditableEntity)

**Purpose:** Manufacturing orders. Drives production — each work order generates job cards.

| Column | Type | Constraint | Purpose |
|--------|------|------------|---------|
| `OrderNumber` | `NVARCHAR(50)` | UNIQUE, NOT NULL | Human-readable order number |
| `ProductName` | `NVARCHAR(200)` | NOT NULL | What's being manufactured |
| `Quantity` | `INT` | NOT NULL | Quantity to produce |
| `ProcessTemplateId` | `UNIQUEIDENTIFIER` | NOT NULL | FK → ProcessTemplates(Id) |
| `Status` | `NVARCHAR(20)` | NOT NULL | Draft, Released, InProgress, Completed, Cancelled |
| `DueDate` | `DATETIME2` | NULL | Target completion |
| `Notes` | `NVARCHAR(2000)` | NULL | |
| `ReleasedAt` | `DATETIME2` | NULL | When approved for production |
| `CompletedAt` | `DATETIME2` | NULL | |
| + AuditableEntity columns | | | |

**Index:** `Status`

**Lifecycle:** `Draft → Released → InProgress → Completed` (or `Cancelled`)

---

### `JobCards` (AuditableEntity)

**Purpose:** Individual work items within a work order. One per process step. Tracks production progress.

| Column | Type | Constraint | Purpose |
|--------|------|------------|---------|
| `WorkOrderId` | `UNIQUEIDENTIFIER` | NOT NULL, FK → WorkOrders(Id) CASCADE | Parent work order |
| `JobCardNumber` | `NVARCHAR(17)` | UNIQUE, NOT NULL | Value object: `YYMMDDN###S###R##` |
| `ProcessStepId` | `UNIQUEIDENTIFIER` | NOT NULL | Which process step |
| `DepartmentName` | `NVARCHAR(200)` | NOT NULL | Which department |
| `Quantity` | `INT` | NOT NULL | Quantity to process |
| `Status` | `NVARCHAR(20)` | NOT NULL | Pending, InProgress, AwaitingQa, QaPassed, QaFailed, Completed, OnHold, Cancelled |
| `Instructions` | `NVARCHAR(2000)` | NULL | |
| `StartedAt` | `DATETIME2` | NULL | |
| `CompletedAt` | `DATETIME2` | NULL | |
| `Notes` | `NVARCHAR(2000)` | NULL | |
| `ParentJobCardId` | `UNIQUEIDENTIFIER` | NULL, FK → JobCards(Id) RESTRICT | Self-ref for splits/reworks |
| + AuditableEntity columns | | | |

**Indexes:** `WorkOrderId`, `Status`, `DepartmentName`, `JobCardNumber` (unique)

**JobCardNumber format:** `260322N001S000R00`
- `YYMMDD` = date, `N###` = running number, `S###` = split, `R##` = rework

---

### `AuditLogs` (Tenant — Business Actions)

**Purpose:** Audit trail for all business data changes within the tenant.

| Column | Type | Constraint | Purpose |
|--------|------|------------|---------|
| `Id` | `UNIQUEIDENTIFIER` | PK | |
| `UserId` | `UNIQUEIDENTIFIER` | NULL | References Master.Users (no FK — cross-DB) |
| `Action` | `NVARCHAR(50)` | NOT NULL | Create, Update, Delete, StatusChange, Approve, Reject |
| `EntityType` | `NVARCHAR(200)` | NOT NULL | "WorkOrder", "JobCard", "Plant", etc. |
| `EntityId` | `NVARCHAR(100)` | NULL | ID of affected entity |
| `OldValues` | `NVARCHAR(MAX)` | NULL | JSON snapshot before change |
| `NewValues` | `NVARCHAR(MAX)` | NULL | JSON snapshot after change |
| `IpAddress` | `NVARCHAR(100)` | NULL | |
| `UserAgent` | `NVARCHAR(500)` | NULL | |
| `CreatedAt` | `DATETIME2` | NOT NULL | |

**Indexes:** `(EntityType, EntityId)`, `UserId`, `CreatedAt`

---

## Provider Mapping: SQL Server and PostgreSQL

Both providers run the **same EF Core model**. Column types in the tables above are written in
SQL Server terms; this section is the PostgreSQL translation and the record of where the two
engines behave differently. PostgreSQL has its own migration lineages — one baseline per context
(P1-i `InitialMasterBaseline`, P1-j `InitialTenantBaseline`) — rather than a replay of SQL
Server's history.

**Master migration lineages (current):**

| Provider | Lineage (ordered) |
|---|---|
| SQL Server | `20260407122318_InitialMaster` → `20260509162834_AddPasswordResetToken` → `20261012130942_AddCompanyFoundation` |
| PostgreSQL | `20261007022731_InitialMasterBaseline` → `20261012131005_AddCompanyFoundation` |

`AddCompanyFoundation` (P2-a) is the **first incremental PostgreSQL Master migration**. Both
providers' versions contain the same operations — add nullable `Tenants.CompanyId`, create
`Companies`, create `IX_Tenants_CompanyId` and unique `IX_Companies_Code`, add
`FK_Tenants_Companies_CompanyId` (Restrict) — and no data operations; only store-type spellings
differ. New Master migrations are generated into `Migrations/MasterDb/` for both providers. The
SQL Server Master snapshot remains `Migrations/MasterDbContextModelSnapshot.cs` in the root
`ERP.Persistence.SqlServer.Migrations` namespace, as it has been since `InitialMaster`; there is
exactly one Master snapshot per provider assembly. The tenant (AppDb) lineages are unchanged.

Verified offline by `ProviderParityTests` (59 tests) and live by `tools/ERP.ParityCheck` (P1-k:
29 PASS, 4 EXPECTED DIFFERENCE, 0 FAIL, 0 SKIPPED, 1 DEFERRED; evidence run
`phase2b2-20261010-160321`, retained outside the repository). **The P1-k live run predates
`AddCompanyFoundation`:** it verified the schema before Companies existed. The Company schema is
verified offline (`ProviderParityTests`, `CompanyMasterFoundationTests`, and
`dotnet ef migrations has-pending-model-changes` on both providers) and, **for PostgreSQL Master
only**, live by the P2-a.2.3e `L-10` run (see *Unresolved debt* → `L-10`); it has not been
applied to a live SQL Server database.

### Store-type mapping

| SQL Server | PostgreSQL | Master columns | Tenant columns |
|---|---|---|---|
| `uniqueidentifier` | `uuid` | 44 | 32 |
| `datetime2` | `timestamp with time zone` | 27 | 19 |
| `bit` | `boolean` | 21 | 12 |
| `int` | `integer` | 3 | 5 |
| `nvarchar(max)` | `text` | 3 | 3 |
| `nvarchar(n)` | `character varying(n)` — same `n` | 48 | 24 |
| `date` | `date` | 0 | 1 (`WorkOrders.DueDate`) |

Table, column, key, index and foreign-key names are identical on both engines: quoted
PascalCase on PostgreSQL, no snake_case convention.

### Accepted provider differences (by design)

| Area | SQL Server | PostgreSQL | Why it is accepted |
|---|---|---|---|
| Timestamp precision | `datetime2(7)`: 100 ns ticks preserved | `timestamptz`: microseconds; sub-µs ticks dropped (Δ = 7 ticks observed) | UTC-instant semantics (P1-h) hold on both; µs-aligned values round-trip exactly |
| `DateOnly.MinValue` / `MaxValue` | stored `0001-01-01` / `9999-12-31` | Npgsql stores `-infinity` / `infinity` | The application value round-trips exactly on both |
| `Tenant.Code` comparison | default collation `SQL_Latin1_General_CP1_CI_AS`: case-insensitive | deterministic `en_US.utf8` (libc): case-sensitive | P1-f canonicalization makes the **application lookup** identical on both. Case sensitivity is measured per environment, not assumed (collation-aware `L-8`) |
| Nullable unique index `IX_Roles_NormalizedName_TenantId` | filtered `[TenantId] IS NOT NULL` | no filter; NULLs are distinct | Same application semantics, verified live (`L-6`): duplicate names with a NULL tenant are allowed; a duplicate within one tenant is rejected |

### Unresolved debt (documented, not fixed)

- **`DB-09` — `Plant.Code` provider divergence.** No application canonicalization exists. On SQL
  Server (case-insensitive) `P1K-PLANT-CASE` and `p1k-plant-case` collide under the unique index
  and look each other up; on PostgreSQL both are accepted and lookups are case-sensitive.
  Behaviour therefore depends on the provider. Fixing it needs a separately approved slice.
- **`Tenant.Code` written outside the application** (direct SQL, imports) is not canonicalized on
  either provider. The `DB-11` pre-deployment runbook in `TENANT_PROVISIONING.md` is the control.
- **`L-10` — incremental-upgrade parity is DEFERRED**: no incremental PostgreSQL migration exists
  yet, so only from-empty baselines and Down/Up are verified. `DB-03` remains **PARTIALLY OPEN**.
  *Update (P2-a):* the first incremental PostgreSQL **Master** migration now exists
  (`AddCompanyFoundation`), but its live upgrade from the Phase 1 head with existing rows has
  **not** been run, so Master `L-10` is still DEFERRED *(superseded for PostgreSQL by the
  P2-a.2.3e update below)*. The tenant (AppDb) side still has no incremental PostgreSQL
  migration; AppDb `L-10` remains DEFERRED independently.
  *Update (P2-a.2.3e, owner-closed 2026-10-17):* **PostgreSQL Master `L-10` — VERIFIED, PASS.**
  Scope: PostgreSQL provider, Master context only, backend `89874873c7a58b47b68d4da3629001754f4d9aff`
  (branch `feat/p2a-company-master-foundation`, not merged). One owner-run live execution of
  `tools/ERP.ParityCheck --live --provider postgresql --context master` against a dedicated parity
  cluster, disposable database `ERP_P1k_Parity_PostgreSql_Throwaway`:
  - **`L-10` lifecycle:** `20261007022731_InitialMasterBaseline` → `20261012131005_AddCompanyFoundation`
    → down → re-up; 11 synthetic pre-existing rows unchanged at every stage; `Companies` empty,
    `Tenants.CompanyId` NULL, `FK_Tenants_Companies_CompanyId` RESTRICT; invalid-FK, restrict-delete
    and duplicate-code probes rejected and rolled back; database dropped, absence verified.
  - **From-empty lifecycle:** full lineage applied, checks run, Down→0/Up; dropped, absence verified.
  - **Results:** 8 PASS (`L-1`, `L-2`, `L-5`, `L-6`, `L-7`, `L-9`, `L-10`, `L-11`), 2 approved
    EXPECTED DIFFERENCE (`L-3` timestamp precision, `L-8` `Tenant.Code` comparison), 25 SKIPPED by
    selection (SQL Server; AppDb; cross-provider `L-9`), 1 DEFERRED (AppDb `L-10`), 0 FAIL, 0 safety
    refusals, 0 cleanup failures. Harness exit 2 (expected for this scope); wrapper exit 0; read-only
    guard checks before, immediately after and ≥ 60 s after the run all PASS (throwaway database absent).
  - **Evidence** (retained outside the repository): run `20261017-153028-LiveMasterL10` —
    `parity-report.json` SHA-256 `2b3bd4bf76f1d70487939c47366d992d42d42dcbcd0ed5a6348605a886f5821f`,
    `wrapper.log` `347641a928043fd7348afdf6bf206971becce4c36e964e39ddeccc846775627c`,
    `parity-console.txt` `53068fb90c9d90a5a2fc71ee19e6ed869e79a8ef1c57ed07a8c798b2a2fb6efc`;
    harness `ERP.ParityCheck.dll` `4c48c4a3495cc70814558b35dc43ff0313a104a15cfa5045346e859e0c9679a7`;
    closure record `P2-a.2.3e-PG-L10-CLOSURE-20261017.md`
    `c2e312d20b27e342ac6a8e4abd7d78a1423a4a2326392fe1dadae221b56b512c`.
  - **Operational note:** the operator console paused in Windows QuickEdit *Select* mode between the
    post-run checks; it was resumed with Esc in the same process — the harness was **not** rerun.
  - **Still open:** SQL Server Master `L-10`; cross-provider `L-9` for the new Master lineage; AppDb
    `L-10` (DEFERRED). `DB-03` remains **PARTIALLY OPEN**; `F3` (`XACT_ABORT`, SQL Server), `F4`
    (endpoint aliasing), `DB-09`, `DB-11` and the accepted security/repeatability risks carry forward.
    This is **not** an approval of Phase 2 completion, Company/Tenant production use or production
    readiness.

### `L-9` incident — IdentitySeeder in the live parity harness (P1-k)

- **What happened.** The first SQL Server live run failed `L-9`: the seed produced only
  `Tenants = 1` and `Permissions = 28` — no roles, users, modules or menus.
- **`CreatedAt` initializer gap.** `AuditableEntity.CreatedAt` has no initializer. `AppRole` and
  `AppUser` rely on `AuditableEntityInterceptor` to stamp it; the seeder's other entities either
  default to `DateTime.UtcNow` or set it explicitly.
- **Design-time vs runtime composition.** The runtime `MasterDbContext`
  (`InfrastructureServiceExtensions`) attaches `AuditableEntityInterceptor` and
  `DomainEventDispatcherInterceptor`; the design-time factories (`tools/ERP.DbMigrator`) attach
  none. The harness used a design-time context, so `CreatedAt` stayed at its `Unspecified` default
  and the P1-h UTC guard correctly refused to persist it.
- **Exception swallowing.** `IdentitySeeder` catches every exception, logs it and returns
  normally. With the harness's no-op logger the failure was silent — visible only as missing rows.
- **Correction — harness only, no production change.** The `L-9` seeder context alone adds
  `AuditableEntityInterceptor` with an anonymous system actor, and a capturing logger fails `L-9`
  on any logged Error/Critical entry (redacted). Production already attaches the interceptor and
  was not affected.
- **Result.** SQL Server retry: 16/16 PASS. Cross-provider run: `L-9` PASS on both providers with
  an identical non-secret structure fingerprint.
- **Still open (not fixed):** the seeder's exception swallowing; seeding's dependency on the
  interceptor; and `AuditableEntityInterceptor` overrides only `SavingChangesAsync`, so a
  synchronous `SaveChanges()` would bypass stamping (0 synchronous call sites at the P1-h review).

### Unresolved technical risk — shared-static `ConfigurationAssemblies` race

Recorded at the Phase 1 conditional closure (2026-10-11). **Not fixed; no issue ID assigned.**

- **Where:** `src/Infrastructure/ERP.Persistence/Context/AppDbContext.cs`.
  `AppDbContext.ConfigurationAssemblies` is a shared **static mutable** `List<Assembly>`
  (line 71), and `OnModelCreating` **enumerates** it (line 100). Module service extensions and
  design-time option builders add to it.
- **Failure mode:** if the list is mutated while another thread is enumerating it, enumeration
  throws `InvalidOperationException` (*Collection was modified*).
- **Observed:** the Claude execution session saw this exception in unit-test runs during
  Phase 1 and reproduced it with the P1-i changes excluded, so it **predates P1-i**.
- **Production impact:** **not established.** Registration normally happens at startup, but no
  analysis or runtime evidence has been recorded either way.
- **Relation to the historical 66/67 repeatability failure** (one full-suite run with two
  unidentified failing tests): **plausible but UNPROVEN.** The failing tests were not captured,
  so the root cause of that failure remains **UNKNOWN**.
- **Going forward:** any repeated test verification should retain TRX logs and the failing-test
  identities, so a recurrence can be attributed.

---

## Seeded Data

### Permissions (28 — system-defined, global catalogue)

> **CORRECTED 2026-09-22.** An earlier version of this table listed `SCREAMING_SNAKE`
> codes (`USER_VIEW`, `WORKORDER_CREATE`) and a **Tenant** category containing
> `TENANT_CREATE` / `TENANT_VIEW` / `TENANT_MANAGE`. **None of those codes has ever
> existed in the codebase.** The list below is the actual seeded set from
> `IdentitySeeder.cs`. The count (28) coincidentally matched; the membership did not.
>
> **There are no tenant permissions.** Tenant Management is gated by
> `[Authorize(Roles = "Admin")]` alone, and `Admin` is tenant-qualified. The seeded
> menu mapping records this explicitly: *"MENU_TENANTS: no permission — visible to all
> admins via [Authorize(Roles=\"Admin\")]"*.

Codes are **dot-separated lowercase**. `Permission` carries **no `TenantId`** — it is a
global capability catalogue (approved decision `AUTH-01`). Tenant scope comes from the
owning Role / UserRole / membership, never from duplicating permission rows per tenant.

| Category | Code | Name |
|----------|------|------|
| User | `user.view` | View Users |
| User | `user.create` | Create Users |
| User | `user.update` | Update Users |
| User | `user.delete` | Delete Users |
| User | `user.assignroles` | Assign Roles |
| Role | `role.view` | View Roles |
| Role | `role.create` | Create Roles |
| Role | `role.update` | Update Roles |
| Role | `role.delete` | Delete Roles |
| Role | `role.assignpermissions` | Assign Permissions |
| WorkOrder | `workorder.view` | View Work Orders |
| WorkOrder | `workorder.create` | Create Work Orders |
| WorkOrder | `workorder.update` | Update Work Orders |
| WorkOrder | `workorder.close` | Close Work Orders |
| JobCard | `jobcard.view` | View Job Cards |
| JobCard | `jobcard.create` | Create Job Cards |
| JobCard | `jobcard.split` | Split Job Cards |
| Production | `production.start` | Start Production |
| Production | `production.complete` | Complete Production |
| Production | `production.view` | View Production |
| QA | `qa.inspect` | Perform QA Inspection |
| QA | `qa.approve` | Approve QA |
| QA | `qa.reject` | Reject QA |
| QA | `qa.view` | View QA Results |
| Workflow | `workflow.view` | View Process Templates |
| Workflow | `workflow.create` | Create Process Templates |
| Workflow | `workflow.update` | Update Process Templates |
| Workflow | `workflow.delete` | Delete Process Templates |

### Roles (4 system roles, TenantId = NULL)

| Role | Permissions |
|------|-------------|
| Admin | All 28 permissions |
| Production Operator | WORKORDER_*, JOBCARD_*, PRODUCTION_* |
| QA Inspector | QA_*, all *_VIEW permissions |
| Viewer | All *_VIEW permissions only |

### Default Admin User
- UserName: `admin`
- Password: *(not documented — supplied via `SeedSettings:DefaultAdminPassword`; see "Credentials" below)*
- Assigned: Admin role

### Credentials

Development credentials are deliberately **not reproduced** in this document. The seeded admin
password comes from `SeedSettings:DefaultAdminPassword` (environment configuration or a secret
store). Development secrets tracked in `appsettings.Development.json` are a recorded security
finding (frontend `docs/roadmaps/production-readiness.md`); remediation — rotation and removal
from tracking and history — requires a separately approved security slice.


---

## Entity Relationship Diagrams

### Master DB Relationships

```
Companies ◄── Tenants.CompanyId (nullable, Restrict)
   (no membership table yet — Company never implies Tenant access)

Tenants ◄────────────── UserTenants ──────────────► Users
   │                                                  │
   │         ┌── TenantFeatureFlags                   │
   │         │                                        │
   ▼         ▼                                        ▼
FeatureFlags                               UserSessions
                                           UserPasswordHistory
                                                  │
Roles ◄──────────────── UserRoles ────────────────┘
(TenantId)                (TenantId)
   │
   ▼
RolePermissions ────────────────────────► Permissions
   │                                         │
   │                                         ▼
   │                                   MenuPermissions ──► Menus ──► Modules
   │                                                        │ ▲
   │                                                        └─┘
   │                                                   (ParentMenuId)
   └── ConditionJson (ABAC, future)
```

### Tenant DB Relationships

```
Plants ◄──────── UserPlantAccess ──────── (UserId → Master.Users)

ProcessTemplates ──► ProcessSteps (self-ref → NextStep)

WorkOrders ──► JobCards (self-ref → ParentJobCard)

AuditLogs (references Master.Users by UserId, no FK)

UserPreferences (references Master.Users by UserId, no FK)
```

---

## Data Flow

### Authentication Flow
```
1. Angular → POST /api/v1/auth/login { userName, password, tenantCode }
2. API → Master DB: Find user, verify password, check lockout
3. API → Master DB: Find user's roles + permissions for this tenant
4. API → Master DB: Create UserSession record
5. API → Angular: { accessToken (JWT), refreshToken }

JWT contains:
{
  "sub": "{userId}",
  "tenant_id": "{tenantCode}",
  "roles": ["Production Operator"],
  "permissions": ["JOBCARD_CREATE", "JOBCARD_VIEW", ...],
  "jti": "{unique-token-id}"
}
```

### Menu Loading Flow
```
1. Angular → GET /api/v1/menus/my-menus (after login)
2. API → Master DB: Get user's permissions for current tenant
3. API → Master DB: Get all active modules + menus
4. API → Master DB: Filter menus by user's permissions (via MenuPermissions)
5. API → Master DB: Check feature flags for this tenant
6. API → Angular: Filtered menu tree

[
  {
    "module": "Production",
    "icon": "fas fa-industry",
    "children": [
      { "name": "Job Cards", "route": "/production/job-cards", "icon": "..." },
      { "name": "Work Orders", "route": "/production/work-orders", "icon": "..." }
    ]
  }
]
```

### Business Request Flow
```
1. Angular → GET /api/v1/work-orders
   Headers: Authorization: Bearer {jwt}, X-Tenant-Id: ABC

2. TenantMiddleware → Master DB: Look up "ABC" → get ConnectionString
3. Auth middleware → Validate JWT, extract claims
4. TenantDbContextFactory → Create DbContext pointing to ERP_ABC database
5. Handler → Query work orders from ERP_ABC
6. Response → Angular
```

---

## Concrete Example: Admin User Flow

> This section traces the seeded **admin** user through every database table,
> using actual seed values. Read this to understand how tables connect in practice.

### Step 1 — Login (`POST /api/v1/auth/login`)

**Request:**
```json
{ "userName": "admin", "password": "<admin password>", "tenantCode": "ABC" }
```

**Tables queried (in order):**

| # | Table | Query | Result |
|---|-------|-------|--------|
| 1 | `Users` | `WHERE UserName = 'admin'` | Returns user row (Id, PasswordHash, IsActive, IsLocked, LockoutEnd, SecurityStamp, AccessFailedCount) |
| 2 | — | BCrypt.Verify(password, PasswordHash) | ✅ Match — `AccessFailedCount` stays 0 |
| 3 | `Tenants` | `WHERE Code = 'ABC' AND IsActive = 1` | Returns tenant row (Id, ConnectionString, DbProvider) |
| 4 | `UserTenants` | `WHERE UserId = {adminId} AND TenantId = {abcId}` | Confirms admin is assigned to tenant ABC |
| 5 | `UserRoles` | `WHERE UserId = {adminId}` | Returns role assignment(s) — includes `TenantId` for scoping |
| 6 | `Roles` | `WHERE Id IN (...)` via UserRoles | Returns "Admin" role |
| 7 | `RolePermissions` | `WHERE RoleId = {adminRoleId}` | Returns all 28 permission links |
| 8 | `Permissions` | `WHERE Id IN (...)` via RolePermissions | Returns 28 permission codes (WORKORDER_CREATE, USER_VIEW, etc.) |
| 9 | `UserSessions` | `INSERT` | Creates session: UserId, RefreshToken, DeviceInfo, ExpiresAt, IsRevoked=false |

**Output → JWT token containing:**
```
sub       = {adminId}
tenant_id = ABC
roles     = ["Admin"]
permissions = ["WORKORDER_CREATE", "WORKORDER_VIEW", ..., "TENANT_MANAGE"]  (all 28)
jti       = {unique-session-token-id}
```

**Failed login path:** If password is wrong → `AccessFailedCount++`. At 5 failures → `IsLocked = true`, `LockoutEnd = now + 15min`. Subsequent attempts rejected until lockout expires.

---

### Step 2 — Sidebar Menu Loading (`GET /api/v1/menus/my-menus`)

**Tables queried by `MenuService.GetMyMenusAsync()`:**

| # | Table | Query | Purpose |
|---|-------|-------|---------|
| 1 | `Users` → `UserRoles` → `RolePermissions` → `Permissions` | Chain via Includes | Get admin's distinct permission codes |
| 2 | `FeatureFlags` | `SELECT Code, IsEnabled` | Global flag defaults |
| 3 | `TenantFeatureFlags` | `WHERE TenantId = {abcId}` | Tenant-specific overrides |
| 4 | `Modules` | `WHERE IsActive = 1 ORDER BY DisplayOrder` | Load all active modules |
| 5 | `Menus` | `WHERE IsVisible = 1` (per module) | Load visible menus with hierarchy |
| 6 | `MenuPermissions` → `Permissions` | Per menu | Check required permissions |

**Filtering logic (per menu):**
```
For each menu:
  1. If menu.FeatureFlag is set:
     → Check TenantFeatureFlags first (tenant override)
     → Fall back to FeatureFlags.IsEnabled (global default)
     → If flag disabled → HIDE menu
  2. If menu has MenuPermissions where IsRequired = true:
     → User must have ALL required permission codes
     → Admin has all 28 → always passes
  3. Menu passes both checks → SHOW
```

**Result for admin (has all permissions):**
```
Production (Module)
  ├── Work Orders (Menu, parent)
  │     ├── List Work Orders (child menu)
  │     ├── Create Work Order (child menu)
  │     └── Approvals (child menu)
  └── Job Cards (Menu, parent)
        ├── List Job Cards (child menu)
        └── Create Job Card (child menu)
Admin (Module)
  ├── Users (Menu)
  ├── Roles (Menu)
  └── Tenants (Menu)
```

**Comparison — Production Operator (only has WORKORDER_*, JOBCARD_*, PRODUCTION_* permissions):**
- Sees: Production module → Work Orders, Job Cards (and their children)
- Hidden: Admin module → Users, Roles, Tenants (requires USER_VIEW, ROLE_VIEW, TENANT_VIEW)

---

### Step 3 — Business Request (`GET /api/v1/work-orders`)

**Request headers:**
```
Authorization: Bearer {jwt}
X-Tenant-Id: ABC
```

**Pipeline (table access in order):**

| # | Component | Table | Purpose |
|---|-----------|-------|---------|
| 1 | JWT Middleware | — (no DB hit) | Validates JWT signature, extracts claims (userId, permissions, roles) |
| 2 | `[Authorize]` | — (no DB hit) | Checks claims match required roles/permissions on endpoint |
| 3 | `TenantMiddleware` | `Tenants` (Master DB, cached) | Looks up `ABC` → gets ConnectionString + DbProvider |
| 4 | `TenantDbContextFactory` | — | Creates `AppDbContext` pointing to `ERP_ABC` database |
| 5 | `PlantAuthorizationBehavior` | `UserPlantAccess` (Tenant DB) | If command implements `IPlantScopedRequest`, checks user has access to the target plant |
| 6 | Handler | `WorkOrders` (Tenant DB) | Queries work orders from `ERP_ABC` |
| 7 | `AuditLogInterceptor` | `AuditLogs` (Tenant DB) | On write operations: captures old/new JSON, UserId, IP, UserAgent |

**Key insight — two databases in one request:**
- Steps 1–3 use **Master DB** (ERP_Master) for auth + tenant lookup
- Steps 5–7 use **Tenant DB** (ERP_ABC) for business data

---

### Step 4 — Permission Tree (`GET /api/v1/menus/permission-tree`)

> Used by the Roles page to show which permissions to assign.

**Tables queried by `MenuService.GetPermissionTreeAsync()`:**

| # | Table | Query | Purpose |
|---|-------|-------|---------|
| 1 | `Modules` → `Menus` → `MenuPermissions` → `Permissions` | Chain via Includes | Build Module → Menu → Permission hierarchy |
| 2 | `Permissions` | `SELECT * ORDER BY Category, Code` | Get ALL permissions (some may not be linked to any menu) |

**Tree output:**
```
Production (Module)
  ├── Work Orders (Menu)
  │     ├── Create Work Order (WORKORDER_CREATE)     ← leaf, has permissionId
  │     ├── View Work Orders (WORKORDER_VIEW)        ← leaf, has permissionId
  │     ├── Update Work Order (WORKORDER_UPDATE)
  │     └── Delete Work Order (WORKORDER_DELETE)
  ├── Job Cards (Menu)
  │     ├── Create Job Card (JOBCARD_CREATE)
  │     ├── View Job Cards (JOBCARD_VIEW)
  │     └── ...
  └── ...
Admin (Module)
  ├── Users (Menu)
  │     ├── Create User (USER_CREATE)
  │     └── ...
  └── ...
Other (virtual node — unlinked permissions)
  └── Any permission not linked to a menu via MenuPermissions
```

**Deduplication rule:** If the same permission (e.g., `WORKORDER_VIEW`) is linked to multiple menus via `MenuPermissions`, it only appears under the first menu encountered. This prevents confusing toggle behavior in the UI (unchecking one copy would uncheck all).

---

### Step 5 — Role Comparison

How different roles see the same system:

| Aspect | Admin | Production Operator | QA Inspector | Viewer |
|--------|-------|-------------------|--------------|--------|
| **Permissions** | All 28 | 13 (WORKORDER_*, JOBCARD_*, PRODUCTION_*) | 11 (QA_*, all *_VIEW) | 7 (*_VIEW only) |
| **Sidebar menus** | Production + Admin modules | Production module only | Production + QA menus | Production (read-only routes) |
| **Can create WO?** | ✅ WORKORDER_CREATE | ✅ WORKORDER_CREATE | ❌ | ❌ |
| **Can manage users?** | ✅ USER_CREATE/UPDATE/DELETE | ❌ | ❌ | ❌ |
| **Can approve QA?** | ✅ QA_APPROVE | ❌ | ✅ QA_APPROVE | ❌ |
| **IsSystemRole** | ✅ (cannot delete) | ✅ | ✅ | ✅ |
| **TenantId** | NULL (system-wide) | NULL (system-wide) | NULL (system-wide) | NULL (system-wide) |

**System roles** (`IsSystemRole = true`) are seeded on startup and cannot be deleted via the API. Custom roles can be created per tenant with `TenantId` set to scope them.

---

### Step 6 — Multi-Tenant User Assignment

A single user can be assigned to multiple tenants via `UserTenants`:

```
Users table:
  admin (Id: aaa-111)

UserTenants table:
  ┌──────────────────────────────────────────────────────────┐
  │ UserId    │ TenantId   │ IsDefault │ JoinedAt            │
  │ aaa-111   │ tenant-ABC │ true      │ 2026-01-01 00:00:00 │
  │ aaa-111   │ tenant-XYZ │ false     │ 2026-03-15 10:00:00 │
  └──────────────────────────────────────────────────────────┘

UserRoles table (tenant-scoped — TenantId is NOT NULL):
  ┌──────────────────────────────────────────────────────┐
  │ UserId    │ RoleId         │ TenantId                │
  │ aaa-111   │ role-admin     │ tenant-ABC              │
  │ aaa-111   │ role-viewer    │ tenant-XYZ              │
  └──────────────────────────────────────────────────────┘
```

> **CORRECTED 2026-09-22.** An earlier version of this example showed
> `UserRoles.TenantId = NULL (system role)` and described permission extraction as
> `TenantId IS NULL (system) OR TenantId = {abcId}`. **That row cannot exist and that
> query is not what the code does.** The two nullable-vs-required columns were conflated:
>
> | Column | Nullable? | Meaning |
> |---|---|---|
> | `Roles.TenantId` | **nullable** | `NULL` = system role *definition*, available to all tenants |
> | `UserRoles.TenantId` | **NOT NULL** | The tenant a role *assignment* applies to |
>
> `AuthService.LoginAsync` filters assignments with
> `Where(ur => ur.TenantId == resolvedTenant.Id)` — **there is no `IS NULL` branch**. The
> old wording described a cross-tenant role-leak path that does not exist in the code.

**Login with `tenantCode: "ABC"`:**
- JWT `tenant_id` = ABC (always the tenant **Code**, even if the caller addressed it by GUID)
- All API requests use `X-Tenant-Id: ABC` → routes to `ERP_ABC` database
- Roles and permissions come **only** from `UserRoles` rows where `TenantId = {abcId}`;
  the role *definition* may be a system role (`Roles.TenantId IS NULL`) or a tenant-specific one

**Switch to tenant XYZ:** User logs in again with `tenantCode: "XYZ"` → new JWT, new session, routes to `ERP_XYZ` database. Same user, completely isolated data.

---

### Complete Table Access Map

Summary of which tables each major operation touches:

| Operation | Master DB Tables | Tenant DB Tables |
|-----------|-----------------|------------------|
| **Login** | Users, Tenants, UserTenants, UserRoles, Roles, RolePermissions, Permissions, UserSessions | — |
| **Refresh token** | UserSessions, Users (SecurityStamp check) | — |
| **Load sidebar** | Users→UserRoles→RolePermissions→Permissions, Modules, Menus, MenuPermissions, FeatureFlags, TenantFeatureFlags | — |
| **Permission tree** | Modules, Menus, MenuPermissions, Permissions | — |
| **List users** | Users, UserRoles, Roles | — |
| **Assign tenants** | Users, Tenants, UserTenants | — |
| **Assign permissions** | Roles, RolePermissions, Permissions | — |
| **Create work order** | Tenants (via middleware, cached) | WorkOrders, UserPlantAccess, AuditLogs |
| **View job cards** | Tenants (via middleware, cached) | JobCards, WorkOrders |
| **Change password** | Users, UserPasswordHistory (last 5 check), UserSessions (revoke all) | — |
| **Lockout** | Users (AccessFailedCount, IsLocked, LockoutEnd) | — |
