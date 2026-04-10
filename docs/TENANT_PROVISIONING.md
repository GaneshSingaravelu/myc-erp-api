# Tenant Provisioning Guide

> How to create and configure new tenants in the ERP system.
> Updated: 2026-04-05

## Overview

Each tenant in the ERP system gets:
1. A **record** in the Master DB `Tenants` table
2. A **dedicated database** with all business tables (migrations applied)
3. **Default seed data** (plants, preferences)

Provisioning can be done manually (CLI/SQL) or via the Admin API.

---

## Option 1: Admin API (Recommended for Production)

### `POST /api/admin/tenants`

**Authorization:** Requires `TENANT_CREATE` permission (Admin role only)

**Request:**
```json
{
  "code": "ABC",
  "name": "ABC Manufacturing Ltd",
  "dbProvider": "SqlServer",
  "adminUserName": "abc_admin",
  "adminEmail": "admin@abc-mfg.com",
  "adminPassword": "SecureP@ss123"
}
```

**What the API does (automatically):**

```
Step 1: Validate request
  ├── Check tenant code is unique
  ├── Check admin username/email not taken
  └── Validate password strength

Step 2: Create tenant database
  ├── Generate connection string: Server=localhost;Database=ERP_{Code}
  ├── CREATE DATABASE ERP_ABC
  └── Run all EF Core migrations on the new database

Step 3: Register tenant in Master DB
  ├── INSERT INTO Tenants (Code, Name, ConnectionString, DbProvider, IsActive)
  └── Clear tenant cache (so TenantMiddleware picks it up)

Step 4: Create tenant admin user (Master DB)
  ├── INSERT INTO Users (UserName, Email, PasswordHash, ...)
  ├── INSERT INTO UserTenants (UserId, TenantId, IsDefault = true)
  ├── INSERT INTO UserRoles (UserId, RoleId = Admin, TenantId)
  └── Password hash via BCrypt

Step 5: Seed tenant database
  └── Create default plant (optional)

Step 6: Return result
  └── { tenantId, tenantCode, adminUserId, connectionVerified: true }
```

**Response (201 Created):**
```json
{
  "tenantId": "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx",
  "code": "ABC",
  "name": "ABC Manufacturing Ltd",
  "dbProvider": "SqlServer",
  "adminUserId": "yyyyyyyy-yyyy-yyyy-yyyy-yyyyyyyyyyyy",
  "databaseCreated": true,
  "migrationsApplied": true,
  "isActive": true
}
```

### `GET /api/admin/tenants`

Lists all tenants (Admin only). Does NOT expose connection strings.

### `PUT /api/admin/tenants/{id}`

Update tenant name, activate/deactivate. Cannot change code or DB provider.

### `POST /api/admin/tenants/{id}/migrate`

Re-run migrations on a specific tenant database (useful after deploying schema updates).

### `POST /api/admin/tenants/migrate-all`

Run pending migrations on ALL active tenant databases. Used after deploying a new version.

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
dotnet ef database update \
  --project src/Infrastructure/ERP.Persistence.PostgreSql \
  --startup-project src/Host/ERP.Api \
  --context AppDbContext \
  --connection "Host=localhost;Database=erp_abc;Username=erp_user;Password=yourpassword"
```

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

**Via Admin API:**
```
POST /api/admin/tenants/migrate-all
```

**Via CLI (DbMigrator tool):**
```bash
dotnet run --project tools/ERP.DbMigrator -- --migrate-all
```

**Manually per tenant:**
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
