# ERP Database Architecture

> Complete reference for all database tables, relationships, and multi-tenant design.
> Updated: 2026-04-05

## Table of Contents

1. [Multi-Tenant Database Design](#multi-tenant-database-design)
2. [Master DB Tables](#master-db-tables-erp_master)
3. [Tenant DB Tables](#tenant-db-tables-erp_tenantcode)
4. [Seeded Data](#seeded-data)
5. [Entity Relationship Diagrams](#entity-relationship-diagrams)
6. [Data Flow](#data-flow)

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

### `Tenants`

**Purpose:** Registry of all tenant organizations. Each tenant gets its own business database.

| Column | Type | Constraint | Purpose |
|--------|------|------------|---------|
| `Id` | `UNIQUEIDENTIFIER` | PK | Tenant internal ID |
| `Code` | `NVARCHAR(50)` | UNIQUE, NOT NULL | Short code sent in X-Tenant-Id header (e.g., "ABC") |
| `Name` | `NVARCHAR(200)` | NOT NULL | Display name (e.g., "ABC Manufacturing Ltd") |
| `ConnectionString` | `NVARCHAR(500)` | NOT NULL | Encrypted connection string to tenant DB |
| `DbProvider` | `NVARCHAR(50)` | NOT NULL | "SqlServer" or "PostgreSql" |
| `IsActive` | `BIT` | NOT NULL, DEFAULT 1 | Disabled tenants cannot log in |
| `CreatedAt` | `DATETIME2` | NOT NULL | When tenant was provisioned |

**Notes:**
- This is the first table queried on every request (cached 30 min by TenantMiddleware)
- ConnectionString should be encrypted at rest in production
- DbProvider determines which EF Core provider to use

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

## Seeded Data

### Permissions (28 — system-defined)

| Category | Code | Name |
|----------|------|------|
| Work Order | `WORKORDER_CREATE` | Create Work Order |
| Work Order | `WORKORDER_VIEW` | View Work Orders |
| Work Order | `WORKORDER_UPDATE` | Update Work Order |
| Work Order | `WORKORDER_DELETE` | Delete Work Order |
| Job Card | `JOBCARD_CREATE` | Create Job Card |
| Job Card | `JOBCARD_VIEW` | View Job Cards |
| Job Card | `JOBCARD_UPDATE` | Update Job Card |
| Job Card | `JOBCARD_DELETE` | Delete Job Card |
| Quality | `QA_VIEW` | View QA Records |
| Quality | `QA_APPROVE` | Approve QA |
| Quality | `QA_REJECT` | Reject QA |
| Production | `PRODUCTION_VIEW` | View Production Data |
| Production | `PRODUCTION_MANAGE` | Manage Production |
| Workflow | `WORKFLOW_CREATE` | Create Process Template |
| Workflow | `WORKFLOW_VIEW` | View Process Templates |
| Workflow | `WORKFLOW_UPDATE` | Update Process Template |
| Workflow | `WORKFLOW_DELETE` | Delete Process Template |
| User | `USER_CREATE` | Create User |
| User | `USER_VIEW` | View Users |
| User | `USER_UPDATE` | Update User |
| User | `USER_DELETE` | Delete User |
| Role | `ROLE_CREATE` | Create Role |
| Role | `ROLE_VIEW` | View Roles |
| Role | `ROLE_UPDATE` | Update Role |
| Role | `ROLE_DELETE` | Delete Role |
| Tenant | `TENANT_CREATE` | Create Tenant |
| Tenant | `TENANT_VIEW` | View Tenants |
| Tenant | `TENANT_MANAGE` | Manage Tenant Settings |

### Roles (4 system roles, TenantId = NULL)

| Role | Permissions |
|------|-------------|
| Admin | All 28 permissions |
| Production Operator | WORKORDER_*, JOBCARD_*, PRODUCTION_* |
| QA Inspector | QA_*, all *_VIEW permissions |
| Viewer | All *_VIEW permissions only |

### Default Admin User
- UserName: `admin`
- Password: `Admin@123` (from appsettings.Development.json)
- Assigned: Admin role

---

## Entity Relationship Diagrams

### Master DB Relationships

```
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
