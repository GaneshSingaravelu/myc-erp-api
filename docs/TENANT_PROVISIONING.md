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
> seed `replace_me`. Reconciling the template is recorded configuration debt — **P1-l is
> documentation-only and changes no configuration**, so the template fix needs a separately
> approved configuration change. It is not a reason to change `DB-10`.

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
difference. **The invariant is the check, not any particular SQL.** The provider-by-provider
runbook below (P1-l) is how to run it.

### DB-11 runbook — provider by provider

#### The invariant

A stored code is canonical when `Code == TenantCode.Normalize(Code)` under **ordinal**
comparison, where `Normalize` (`ERP.Identity/Entities/TenantCode.cs`) is:

- `string.IsNullOrWhiteSpace(code)` → `""` — an empty canonical form is never a valid stored code;
- otherwise `code.Trim().ToLowerInvariant()`.

**Supported character domain: unrestricted.** No validator, regular expression or
allowed-character rule constrains `Tenant.Code` — only `MaxLength(50)`, `IsRequired()` and the
unique index `IX_Tenants_Code`. This runbook does **not** introduce an ASCII-only (or any other)
character rule; that would be a separate product decision.

#### Why a database query alone is not a complete check

Provider-side `LOWER()` / `TRIM()` are **not** equivalent to the .NET contract over arbitrary
Unicode. Measured offline (.NET 10.0.5, P1-l; no database involved):

| .NET contract | Database primitives |
|---|---|
| `Trim()` removes the **25** characters `char.IsWhiteSpace` accepts in the BMP — tab, LF, CR, VT, FF, U+0085, U+00A0, U+1680, U+2000–U+200A, U+2028, U+2029, U+202F, U+205F, U+3000 and space | SQL Server `LTRIM`/`RTRIM` and PostgreSQL `btrim(x)` remove **only U+0020** by default |
| `ToLowerInvariant()` applies .NET's invariant mappings — e.g. U+0130 (İ) is left **unchanged**, U+212A (Kelvin sign) → `k` | `LOWER()` follows the engine's collation / locale tables, which vary by engine, version and collation (e.g. Turkish rules lower `I` to `ı`) |

Over **printable ASCII (U+0020–U+007E)** the .NET side reduces to: the only whitespace is U+0020,
and the only characters `ToLowerInvariant()` changes are `A`–`Z` (each to its lowercase). Provider
equivalence over that domain is therefore decided by two things checked **at execution time**:
that the stored data lies inside the domain (Step 2) and that the provider primitives behave as
required under an explicitly pinned binary / `C` collation (Step 1). **Equivalence outside that
domain is not claimed** — data outside it must use Step 3b.

> These queries were authored and reviewed statically in P1-l. **They have not been executed
> against any live database.** Step 1 is what validates them on the target engine.

#### Step 0 — environment verification record (read-only)

Record before checking, for the evidence table at the end:

```sql
-- SQL Server (connected to the Master database)
SELECT @@VERSION AS engine_version,
       SERVERPROPERTY('Collation')                  AS server_collation,
       DATABASEPROPERTYEX(DB_NAME(), 'Collation')   AS database_collation,
       (SELECT c.collation_name FROM sys.columns c
         WHERE c.object_id = OBJECT_ID(N'dbo.Tenants') AND c.name = N'Code') AS code_column_collation;
```

```sql
-- PostgreSQL (connected to the Master database)
SELECT version() AS engine_version;
SELECT datcollate, datctype, datlocprovider FROM pg_database WHERE datname = current_database();
SELECT collation_name FROM information_schema.columns
 WHERE table_schema = 'public' AND table_name = 'Tenants' AND column_name = 'Code';  -- NULL = database default
```

#### Step 1 — validate the comparison primitives (literals only; no table access)

Every column must be `1` / `true`, or the SQL screening in Step 3a **must not be used**.

```sql
-- SQL Server: binary collation pinned explicitly — never the database/server default
SELECT
  CASE WHEN LOWER(N'ABCDEFGHIJKLMNOPQRSTUVWXYZ' COLLATE Latin1_General_100_BIN2)
            = N'abcdefghijklmnopqrstuvwxyz' COLLATE Latin1_General_100_BIN2 THEN 1 ELSE 0 END AS lower_ok,
  CASE WHEN N'A' COLLATE Latin1_General_100_BIN2 = N'a' COLLATE Latin1_General_100_BIN2
       THEN 0 ELSE 1 END                                                               AS compare_case_sensitive,
  -- DATALENGTH, not =: SQL Server's = ignores trailing spaces even under a binary collation
  CASE WHEN DATALENGTH(LTRIM(RTRIM(N'  x  '))) = DATALENGTH(N'x') THEN 1 ELSE 0 END AS trim_ok;
```

```sql
-- PostgreSQL: deterministic "C" collation pinned explicitly — never the database default
SELECT lower('ABCDEFGHIJKLMNOPQRSTUVWXYZ' COLLATE "C") = ('abcdefghijklmnopqrstuvwxyz' COLLATE "C") AS lower_ok,
       ('A' COLLATE "C") <> ('a' COLLATE "C")                                                       AS compare_case_sensitive,
       btrim('  x  ') = 'x'                                                                         AS trim_ok;
```

#### Step 2 — domain gate

Count rows containing **any** character outside printable ASCII (U+0020–U+007E), including
tab, CR, LF and every non-ASCII character:

```sql
-- SQL Server
SELECT COUNT(*) AS rows_outside_printable_ascii
  FROM dbo.Tenants
 WHERE Code COLLATE Latin1_General_100_BIN2 LIKE N'%[^ -~]%';
```

```sql
-- PostgreSQL
SELECT count(*) AS rows_outside_printable_ascii
  FROM "Tenants"
 WHERE ("Code" COLLATE "C") ~ '[^ -~]';
```

- **0** and Step 1 all-true → Step 3a is a complete check for this data; Step 3b remains available.
- **Greater than 0**, or any Step 1 failure → **Step 3b is mandatory.** Do not use Step 3a's result.

#### Step 3a — SQL screening (valid only when Step 1 passes and Step 2 = 0)

```sql
-- SQL Server
-- (1) non-canonical or empty. DATALENGTH is required: SQL Server's = / <> ignore TRAILING
--     spaces even under a binary collation, so 'acme ' <> 'acme' is FALSE.
SELECT Id, Code
  FROM dbo.Tenants
 WHERE Code COLLATE Latin1_General_100_BIN2
         <> LOWER(LTRIM(RTRIM(Code)) COLLATE Latin1_General_100_BIN2) COLLATE Latin1_General_100_BIN2
    OR DATALENGTH(Code) <> DATALENGTH(LTRIM(RTRIM(Code)))
    OR DATALENGTH(LTRIM(RTRIM(Code))) = 0;

-- (2) whitespace anomalies (leading/trailing space)
SELECT Id FROM dbo.Tenants WHERE DATALENGTH(Code) <> DATALENGTH(LTRIM(RTRIM(Code)));

-- (3) canonical collision groups
SELECT LOWER(LTRIM(RTRIM(Code)) COLLATE Latin1_General_100_BIN2) AS canonical, COUNT(*) AS rows_in_group
  FROM dbo.Tenants
 GROUP BY LOWER(LTRIM(RTRIM(Code)) COLLATE Latin1_General_100_BIN2)
HAVING COUNT(*) > 1;
```

```sql
-- PostgreSQL (lower() takes its collation from its INPUT, so COLLATE goes inside the call)
-- (1) non-canonical or empty
SELECT "Id", "Code"
  FROM "Tenants"
 WHERE ("Code" COLLATE "C") <> lower(btrim("Code") COLLATE "C")
    OR btrim("Code") = '';

-- (2) whitespace anomalies (leading/trailing space)
SELECT "Id" FROM "Tenants" WHERE octet_length("Code") <> octet_length(btrim("Code"));

-- (3) canonical collision groups
SELECT lower(btrim("Code") COLLATE "C") AS canonical, count(*) AS rows_in_group
  FROM "Tenants"
 GROUP BY lower(btrim("Code") COLLATE "C")
HAVING count(*) > 1;
```

#### Step 3b — application-equivalent verification (authoritative for any data)

When Step 2 finds anything outside printable ASCII, or Step 1 fails — and optionally always:

1. **Read-only export of `Id` and `Code` only** (`SELECT Id, Code FROM dbo.Tenants` /
   `SELECT "Id", "Code" FROM "Tenants"`). No other column is needed; nothing is written back.
2. Evaluate every row with the application's own `TenantCode.Normalize`, from the **same build**
   that is about to be deployed, using **ordinal** comparison:

   ```csharp
   // rows: the read-only (Id, Code) export
   var nonCanonical = rows.Where(r => !string.Equals(TenantCode.Normalize(r.Code), r.Code, StringComparison.Ordinal));
   var whitespace   = rows.Where(r => !string.Equals(r.Code.Trim(), r.Code, StringComparison.Ordinal));
   var empty        = rows.Where(r => TenantCode.Normalize(r.Code).Length == 0);
   var collisions   = rows.GroupBy(r => TenantCode.Normalize(r.Code), StringComparer.Ordinal)
                          .Where(g => g.Count() > 1);
   ```

3. **Limitation, stated:** `ToLowerInvariant()` follows the casing data of the .NET runtime that
   executes it. Run Step 3b on the **same .NET version** as the deployment target and record that
   version; a different runtime could disagree on rare characters.

This is an operator procedure, not new tooling.

#### Step 4 — stop rule and evidence

- **Any** non-canonical row, whitespace anomaly, empty canonical form or collision group →
  **STOP the deployment** and escalate (rules above). **No automatic repair** — not by script,
  migration or seeder.
- Record one row per environment:

| Environment | Engine / version | DB / column collation | Step 1 | Step 2 rows | Method (3a / 3b) | Non-canonical | Whitespace | Empty | Collision groups | Rows modified | Date / operator |
|---|---|---|---|---|---|---|---|---|---|---|---|
| *(fill in)* | | | | | | | | | | **0** | |

**Environments verified so far (read-only):**

| Environment | Result |
|---|---|
| Local development `ERP_Master` (SQL Server), 2026-09-27 — `dev-tenant`, `pg-tenant`, `test-co` | **non-canonical rows 0 · whitespace anomalies 0 · canonical collisions 0 · zero rows modified** (checked before this runbook existed, with a binary comparison; all three codes are printable ASCII) |
| Local development PostgreSQL (`ERP_PG_Dev`), 2026-10-10 | **Contains no tables** (0 relations, read-only P1-k preflight) — there is no PostgreSQL Master database with tenant rows locally. PostgreSQL migration lineages **do** exist now (P1-i / P1-j); this is an absence of deployed data, not of migrations. |

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

# For PostgreSQL tenant — see the note below for the verified command shape
dotnet ef database update \
  --project src/Infrastructure/ERP.Persistence.PostgreSql \
  --startup-project src/Host/ERP.Api \
  --context AppDbContext \
  --connection "Host=localhost;Database=erp_abc;Username=erp_user;Password=yourpassword"
```

> **PostgreSQL migration lineages exist (`DB-02` complete).** `ERP.Persistence.PostgreSql`
> carries one PostgreSQL-native baseline per context — `Migrations/MasterDb/20261007022731_InitialMasterBaseline`
> (P1-i) and `Migrations/AppDb/20261008162932_InitialTenantBaseline` (P1-j) — each representing
> the current model. They deliberately do **not** replay SQL Server's history (2 Master + 3 tenant
> migrations under `ERP.Persistence.SqlServer`).
>
> **Verified command shape** (P1-i / P1-j / P1-k): the design-time factories live in
> `tools/ERP.DbMigrator`, and the provider comes from the deployment setting, supplied through
> environment variables so no tracked configuration is edited:
>
> ```bash
> Database__Provider=PostgreSql \
> Tenants__0__ConnectionString="<tenant connection string>" \
> dotnet ef database update \
>   --project src/Infrastructure/ERP.Persistence.PostgreSql \
>   --startup-project tools/ERP.DbMigrator \
>   --context AppDbContext
> ```
>
> The `--startup-project src/Host/ERP.Api … --connection` form above was **not** the form verified
> in P1-i–P1-k. Both baselines were applied from empty and rolled Down/Up on real PostgreSQL 17.9
> by the P1-k parity harness (`tools/ERP.ParityCheck`). Incremental-upgrade parity (`L-10`) is
> **not** yet verified: no incremental PostgreSQL migration exists.

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
  -d '{"userName": "abc_admin", "password": "<ADMIN_PASSWORD_FROM_SECURE_CONFIGURATION>"}'
```

---

## Deploying Schema Updates to Existing Tenants

When you add new tables or columns in a future phase:

> **Tooling and provider note (2026-10-30).** A schema change needs a migration in **each**
> provider's lineage (`ERP.Persistence.SqlServer` and `ERP.Persistence.PostgreSql`). `AppDbContext`
> migrations must use `tools/ERP.DbMigrator` as the startup project, because `ERP.Api` fails at
> design time; the command is in `CLAUDE.md` *Important Notes*. The
> `--startup-project src/Host/ERP.Api` examples below predate that guidance and are **not** a
> verified form. For PostgreSQL, the verified apply shape is under *Step 2: Apply EF Core
> Migrations* above. A verified SQL Server per-tenant apply command is **not** recorded here and
> remains pending. `tools/ERP.DbMigrator` being the design-time startup project does not make its
> stub `Program.cs` a working migration tool (`DB-08`).

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
