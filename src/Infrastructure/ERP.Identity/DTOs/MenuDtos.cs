namespace ERP.Identity.DTOs;

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
