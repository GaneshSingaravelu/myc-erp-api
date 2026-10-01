# Tenant Provisioning Guide

> How to create and configure new tenants in the ERP system.
> Updated: 2026-04-05

## Overview

Each tenant in the ERP system gets:
1. A **record** in the Master DB `Tenants` table
2. A **dedicated database** with all business tables (migrations applied)
3. **Default seed data** (plants, preferences)

Provisioning is done from configuration (see Option 1 below) plus manual database steps.

> **⚠️ STATUS 2026-09-22 — READ BEFORE USING THIS DOCUMENT.**
>
> There is **no HTTP tenant-provisioning API**. An earlier version of this guide
> recommended one; that section was factually wrong *and* is now superseded. See
> **§Withdrawn HTTP provisioning API** below. Tenants are created today from
> configuration via `IdentitySeeder` (Option 2). The approved long-term direction is an
> internal secured provisioning tool/job — **not** a public application endpoint.
> Authority: `docs/SATY_ERP_MASTER_EXECUTION_PLAN.md` §4.5 (frontend repo).

---

## Withdrawn HTTP provisioning API — `SUPERSEDED` / `HISTORICAL`

A previous version of this guide documented an "Admin API" as the **recommended**
production provisioning route. That guidance is removed. It was wrong on every point:

| Previously documented | Reality |
| --- | --- |
| `POST /api/admin/tenants` (create) | Path was never `/api/admin/...` — the controller route is `/api/v1/tenants`. **Create now returns a fixed `405`** (PR #14) |
| `PUT /api/admin/tenants/{id}` | Real path `/api/v1/tenants/{id}`. Tenant-scoped to the caller's own tenant. `Name` is editable; a differing `isActive` returns **`400 Tenant.ActivationImmutable`** |
| `GET /api/admin/tenants` "lists all tenants" | Real path `/api/v1/tenants`. Returns **zero or one** tenant — the caller's own. Cross-tenant enumeration was a security defect and is closed |
| `POST /api/admin/tenants/{id}/migrate` | **Returns a fixed `405`** (PR #14) |
| `POST /api/admin/tenants/migrate-all` | **Never existed.** No such endpoint or handler is present in the codebase |
| "Requires `TENANT_CREATE` permission" | **No such permission exists.** No tenant permissions are seeded. Tenant Management is gated by `[Authorize(Roles = "Admin")]` alone, and `Admin` is **tenant-qualified** |
| Create payload with `adminUserName` / `adminEmail` | Not part of any request contract |

**Why the capabilities were withdrawn.** `Admin` means "Admin of the tenant you signed
in to" — there is no platform-administrator model in this system. Creating, deactivating
or migrating an *arbitrary* tenant requires an authority that does not exist, so those
operations were removed at the HTTP boundary rather than granted. **There is no
replacement endpoint, and re-exposing one is not an approved option.**

**Do not restore this section.** If a provisioning channel is ever built, it follows the
approved direction in `SATY_ERP_MASTER_EXECUTION_PLAN.md` §4.5.

---

## Option 1 (current): Configuration + seeder

Tenants are declared in the `Tenants` section of `appsettings` / `appsettings.{Env}.json`
(`Id`, `Identifier` = tenant code, `Name`, `ConnectionString`, `DbProvider`) and inserted
on startup by `IdentitySeeder.SeedTenantsAsync` if absent. Tenant onboarding is therefore
a **deploy-time configuration change** today.

> **The configured `Identifier` is canonicalized on write** — trimmed and lowercased — so the
> seeded `Tenant.Code` is the same value tenant resolution looks up, whatever casing the
> configuration file happens to use (`DB-10`). A configuration entry with **no** `Identifier`
> still seeds the pre-existing `"unknown"` sentinel; that behaviour is unchanged.
>
> Note that the **base** `appsettings.json` template ships `"Identifier": "REPLACE_ME"`, which
> is not canonical. It is a placeholder, not a live tenant: an operator who leaves it would now
> seed `replace_me`. Reconciling the template is configuration debt for **P1-l** — it is not a
> reason to change `DB-10`.

> ⚠️ `Tenant.ConnectionString` is currently stored **in plaintext** in the Master DB
> (`nvarchar(500)`, NOT NULL). The entity comment claiming it is encrypted at rest is
> aspirational — no encryption is implemented. Tracked as `DB-05`.

---

## ⛔ MANDATORY PRE-DEPLOYMENT CHECK — `Tenant.Code` canonicality (`DB-11`)

**Applies to:** any deployment of `DB-10` behaviour (backend `origin/main` `5650c0b` onward)
against a Master database **whose `Tenants` rows have not already been verified**.

**Why it is mandatory.** `DB-10` canonicalizes *reads* as well as writes. A row already stored
non-canonically — say `Code = 'ACME'` — is therefore **unreachable** after deployment on a
case-sensitive engine. The impact is not limited to a `404` at `TenantMiddleware`: token
refresh re-resolves the `tenant_id` claim through the same normalizing lookup and compares by
id, so a failure to resolve **revokes the live session**. Canonical form is enforced by the
application, not the database (no `CHECK`, no `NormalizedCode`, no `citext`, no collation), so
existing data is **not** guaranteed to satisfy the invariant.

**What to check — READ-ONLY. Do not modify any row.**

1. **Non-canonical codes** — any row where `Code` differs from `Trim()` + lowercase of itself.
2. **Whitespace anomalies** — leading or trailing whitespace, including tab, CR, LF and
   non-breaking space.
3. **Canonical collisions** — two or more existing rows that collapse to the *same*
   `Trim().ToLowerInvariant()` value.

**If any of the three is found: STOP the deployment.**

- **Do NOT normalize the data automatically.** Escalate for owner / data-owner review.
- **Canonical collisions are tenant-identity decisions and must not be resolved
  mechanically** — deciding that two rows are "really" one tenant, or which one keeps the
  code, is a business decision with data-migration consequences.

**The procedure must be provider-appropriate.** Compare in a way that is genuinely
case-sensitive on the engine in question, so a case-insensitive collation cannot mask a
difference. ⚠️ **Do not treat SQL Server's `COLLATE Latin1_General_BIN2` form as the universal
procedure** — it was the local development instance's syntax, nothing more. **The invariant is
the check, not any particular SQL.** The full provider-by-provider runbook is owed by **P1-l**.

**Environments verified so far (read-only, 2026-09-27):**

| Environment | Result |
|---|---|
| Local development `ERP_Master` (SQL Server) — `dev-tenant`, `pg-tenant`, `test-co` | **non-canonical rows 0 · whitespace anomalies 0 · canonical collisions 0 · zero rows modified** |
| Local development PostgreSQL | **No Master schema or tenant rows exist** — the PostgreSQL migration lineage is still ZERO (`DB-02`) |

> ⚠️ **This clears those two environments only.** No deployment pipeline, artefact, tag or
> release branch exists in either repository, but **repository evidence cannot prove that no
> hand-deployed UAT / production / shared database exists.** Deployment history outside
> repository-controlled environments remains **UNKNOWN unless independently proven**. Run the
> check; do not reason from the clean local result.

---

## Option 2: Manual Provisioning (Development / Early Stage)

### Step 1: Create the Tenant Database

**SQL Server:**
```sql
CREATE DATABASE ERP_ABC;
GO
```

**PostgreSQL:**
```sql
CREATE DATABASE erp_abc;
```

### Step 2: Apply EF Core Migrations

```bash
# For SQL Server tenant
dotnet ef database update \
  --project src/Infrastructure/ERP.Persistence.SqlServer \
  --startup-project src/Host/ERP.Api \
  --context AppDbContext \
  --connection "Server=localhost;Database=ERP_ABC;Trusted_Connection=true;TrustServerCertificate=true;"

# For PostgreSQL tenant
#   ⚠️ DOES NOT WORK TODAY — see the warning below.
dotnet ef database update \
  --project src/Infrastructure/ERP.Persistence.PostgreSql \
  --startup-project src/Host/ERP.Api \
  --context AppDbContext \
  --connection "Host=localhost;Database=erp_abc;Username=erp_user;Password=yourpassword"
```

> **⚠️ PostgreSQL migrations do not exist (`DB-02`).** `ERP.Persistence.PostgreSql` contains
> **zero migration files** — all four migrations in the repository live under
> `ERP.Persistence.SqlServer`. The PostgreSQL command above would therefore apply nothing.
> `appsettings.Development.json` seeds `pg-tenant` with `DbProvider: "PostgreSql"`: it can be
> resolved and connected to, but its schema cannot be created by this codebase.
>
> PostgreSQL parity for **both** Master and Tenant databases is an **approved product
> requirement** (`ARCH-DB-03`), not an open question. This is an implementation gap.

### Step 3: Register Tenant in Master DB

```sql
INSERT INTO Tenants (Id, Code, Name, ConnectionString, DbProvider, IsActive, CreatedAt)
VALUES (
  NEWID(),
  'ABC',
  'ABC Manufacturing Ltd',
  'Server=localhost;Database=ERP_ABC;Trusted_Connection=true;TrustServerCertificate=true;',
  'SqlServer',
  1,
  GETUTCDATE()
);
```

### Step 4: Create Admin User for the Tenant

```sql
-- In Master DB
DECLARE @UserId UNIQUEIDENTIFIER = NEWID();
DECLARE @TenantId UNIQUEIDENTIFIER = (SELECT Id FROM Tenants WHERE Code = 'ABC');
DECLARE @AdminRoleId UNIQUEIDENTIFIER = (SELECT Id FROM Roles WHERE NormalizedName = 'ADMIN' AND TenantId IS NULL);

-- Create user
INSERT INTO Users (Id, UserName, NormalizedUserName, Email, NormalizedEmail, PasswordHash, SecurityStamp, ConcurrencyStamp, FirstName, LastName, IsLocked, AccessFailedCount, MustChangePassword, IsActive, IsDeleted, CreatedAt)
VALUES (@UserId, 'abc_admin', 'ABC_ADMIN', 'admin@abc-mfg.com', 'ADMIN@ABC-MFG.COM', '<bcrypt-hash>', NEWID(), NEWID(), 'ABC', 'Admin', 0, 0, 1, 1, 0, GETUTCDATE());

-- Link user to tenant
INSERT INTO UserTenants (Id, UserId, TenantId, IsDefault, HasGlobalPlantAccess, IsActive, JoinedAt)
VALUES (NEWID(), @UserId, @TenantId, 1, 1, 1, GETUTCDATE());

-- Assign admin role
INSERT INTO UserRoles (Id, UserId, RoleId, TenantId, AssignedAt, IsActive)
VALUES (NEWID(), @UserId, @AdminRoleId, @TenantId, GETUTCDATE(), 1);
```

### Step 5: Verify

```bash
# Start the API
dotnet run --project src/Host/ERP.Api

# Test login with new tenant
curl -X POST https://localhost:5001/api/v1/auth/login \
  -H "Content-Type: application/json" \
  -H "X-Tenant-Id: ABC" \
  -d '{"userName": "abc_admin", "password": "Admin@123"}'
```

---

## Deploying Schema Updates to Existing Tenants

When you add new tables or columns in a future phase:

### 1. Create the Migration

```bash
dotnet ef migrations add AddFinanceModule \
  --project src/Infrastructure/ERP.Persistence.SqlServer \
  --startup-project src/Host/ERP.Api \
  --context AppDbContext
```

### 2. Apply to All Tenants

> **⚠️ Neither automated route below exists. Only the manual route works today.**

~~**Via Admin API:** `POST /api/admin/tenants/migrate-all`~~
`SUPERSEDED` — **this endpoint never existed**, and per-tenant `migrate` now returns `405`.

~~**Via CLI (DbMigrator tool):** `dotnet run --project tools/ERP.DbMigrator -- --migrate-all`~~
`SUPERSEDED` — **`tools/ERP.DbMigrator` is a stub.** Its `Program.cs` prints four lines and
exits ("Implementation will be added when we create the first migration"). It parses no
arguments, so `--migrate-all` is not a flag. The gap is recorded as `DB-08`.

> ⛔ **Do not modify `tools/ERP.DbMigrator`.** The *direction* of an internal secured
> provisioning tool is approved (`ONB-DIR-02`), but **whether this project is that tool, and
> its exact implementation boundary, is a separate scope decision that has not been taken**
> (owner, 2026-09-26). Provisioning-tool development must not be folded into
> database-provider-parity work. See `SATY_ERP_MASTER_EXECUTION_PLAN.md` §6.2 `DB-08`.

**Manually per tenant (the only working route):**
```bash
dotnet ef database update \
  --project src/Infrastructure/ERP.Persistence.SqlServer \
  --startup-project src/Host/ERP.Api \
  --context AppDbContext \
  --connection "Server=localhost;Database=ERP_ABC;..."
```

---

## Database Naming Convention

| Type | Pattern | Example |
|------|---------|---------|
| Master DB | `ERP_Master` | `ERP_Master` |
| Tenant DB (SQL Server) | `ERP_{TenantCode}` | `ERP_ABC`, `ERP_XYZ` |
| Tenant DB (PostgreSQL) | `erp_{tenant_code}` | `erp_abc`, `erp_xyz` |
| Dev Tenant DB | `ERP_Dev` | `ERP_Dev` |

---

## Tenant Deactivation vs Deletion

| Action | What Happens | Reversible? |
|--------|-------------|-------------|
| **Deactivate** (`IsActive = false`) | Login blocked, API returns 403, data preserved | Yes — set `IsActive = true` |
| **Delete** (not implemented) | Would need to drop database, remove all references | No — avoid in ERP systems |

**Recommendation:** Never delete tenants. Deactivate them. Archive the database after a retention period if needed.
