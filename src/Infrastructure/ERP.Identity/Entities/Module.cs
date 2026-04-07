using ERP.SharedKernel.Entities;

namespace ERP.Identity.Entities;

/// <summary>
/// MODULE — Top-level product modules displayed in the Angular sidebar.
///
/// Modules are the major areas of the ERP:
///   PRODUCTION, WORKFLOW, QUALITY, ADMIN, etc.
///
/// Each module contains child menus (Menus table).
/// Modules are system-defined — stored in Master DB, shared across all tenants.
/// Visibility controlled by FeatureFlags + user permissions.
/// </summary>
public class Module : BaseEntity
{
    /// <summary>Machine code (e.g., "PRODUCTION", "WORKFLOW", "ADMIN"). Max 100.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Display name (e.g., "Production Management"). Max 200.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Icon class for the sidebar (e.g., "fas fa-industry", "lucide-factory"). Max 100.</summary>
    public string? Icon { get; set; }

    /// <summary>Sort order in the sidebar navigation</summary>
    public int DisplayOrder { get; set; }

    /// <summary>Hide the entire module from navigation</summary>
    public bool IsActive { get; set; } = true;

    // ── Navigation Properties ──

    /// <summary>Child menu items within this module</summary>
    public ICollection<Menu> Menus { get; set; } = [];
}
