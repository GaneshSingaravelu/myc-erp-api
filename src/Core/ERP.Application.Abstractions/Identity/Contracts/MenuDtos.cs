namespace ERP.Application.Abstractions.Identity.Contracts;

/// <summary>
/// Menu DTOs for the dynamic sidebar API.
/// Angular calls GET /api/v1/menus/my-menus after login to build the sidebar.
/// </summary>

public record ModuleMenuResponse(
    Guid Id,
    string Code,
    string Name,
    string? Icon,
    int DisplayOrder,
    List<MenuItemResponse> Menus);

public record MenuItemResponse(
    Guid Id,
    string Code,
    string Name,
    string? Route,
    string? Icon,
    int DisplayOrder,
    bool IsEnabled,
    List<MenuItemResponse> Children);

// ── Permission Tree (for role-permission assignment UI) ──

/// <summary>
/// Hierarchical tree node: Module → Menu → Permissions.
/// Used by the admin UI to render a tree-style permission selector
/// matching the sidebar menu structure.
/// </summary>
public record PermissionTreeNode(
    string Id,
    string Label,
    string? PermissionId,
    List<PermissionTreeNode> Children);
