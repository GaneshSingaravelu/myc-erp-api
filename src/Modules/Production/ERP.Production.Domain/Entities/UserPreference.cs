using ERP.SharedKernel.Entities;

namespace ERP.Production.Domain.Entities;

/// <summary>
/// USER PREFERENCE — Per-user, per-tenant settings as key-value pairs.
///
/// Stored in Tenant DB. Infinitely extensible without schema changes.
///
/// COMMON KEYS:
/// ==============
///   "theme"             → "dark" or "light"
///   "language"          → "en", "ta", "hi"
///   "defaultPlant"      → "{plantId}"
///   "pageSize"          → "50"
///   "sidebarCollapsed"  → "true"
///   "dashboardLayout"   → JSON with widget positions
/// </summary>
public class UserPreference : BaseEntity
{
    /// <summary>References Master DB Users.Id (no FK — cross-database reference)</summary>
    public Guid UserId { get; set; }

    /// <summary>Setting key (e.g., "theme", "language", "defaultPlant"). Max 200.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Setting value (JSON for complex values). Max.</summary>
    public string Value { get; set; } = string.Empty;
}
