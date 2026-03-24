using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace ERP.Notifications.Hubs;

/// <summary>
/// ERP NOTIFICATION HUB — Real-time notifications via SignalR WebSocket.
///
/// WHAT IS SIGNALR? (Interview Question!)
/// ========================================
/// SignalR enables real-time, bidirectional communication between server and client.
///
/// Normal HTTP: Client asks → Server responds (one-way, client always initiates)
/// SignalR:     Server can PUSH messages to clients without them asking!
///
/// USE CASES IN OUR ERP:
/// ======================
/// - Job card status changed → Notify production floor display
/// - QA inspection needed → Alert QA inspector in real-time
/// - Production completed → Update dashboard immediately
/// - Work order fulfilled → Notify planning team
///
/// HOW IT WORKS:
/// ==============
/// 1. Angular connects to the hub: new signalR.HubConnectionBuilder().withUrl("/hubs/notifications")
/// 2. Server sends a message: await Clients.Group("tenant-abc").SendAsync("JobCardUpdated", data)
/// 3. Angular receives it: connection.on("JobCardUpdated", (data) => { /* update UI */ })
///
/// GROUPS:
/// ========
/// We organize connections into GROUPS by tenant and department.
/// When a job card updates in Dept A, only users watching Dept A get notified.
///
///   Group: "tenant:abc-manufacturing"         → All users of ABC Manufacturing
///   Group: "tenant:abc-manufacturing:dept:cutting" → Only Cutting dept users
///   Group: "tenant:abc-manufacturing:qa"       → Only QA inspectors
///
/// REGISTERED IN Program.cs:
///   app.MapHub&lt;ErpNotificationHub&gt;("/hubs/notifications");
/// </summary>
[Authorize]
public class ErpNotificationHub : Hub
{
    /// <summary>
    /// Called automatically when a client connects.
    /// We add the connection to the tenant's group so they receive tenant-specific notifications.
    /// </summary>
    public override async Task OnConnectedAsync()
    {
        var tenantId = Context.User?.FindFirst("tenant_id")?.Value;
        if (!string.IsNullOrEmpty(tenantId))
        {
            // Add this connection to the tenant group
            await Groups.AddToGroupAsync(Context.ConnectionId, $"tenant:{tenantId}");
        }

        await base.OnConnectedAsync();
    }

    /// <summary>
    /// Client can join additional groups (e.g., department-specific notifications).
    /// Called from Angular: connection.invoke("JoinGroup", "dept:cutting")
    /// </summary>
    public async Task JoinGroup(string groupName)
    {
        var tenantId = Context.User?.FindFirst("tenant_id")?.Value;
        if (!string.IsNullOrEmpty(tenantId))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"tenant:{tenantId}:{groupName}");
        }
    }

    /// <summary>
    /// Client can leave a group.
    /// Called from Angular: connection.invoke("LeaveGroup", "dept:cutting")
    /// </summary>
    public async Task LeaveGroup(string groupName)
    {
        var tenantId = Context.User?.FindFirst("tenant_id")?.Value;
        if (!string.IsNullOrEmpty(tenantId))
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"tenant:{tenantId}:{groupName}");
        }
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await base.OnDisconnectedAsync(exception);
    }
}
