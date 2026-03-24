using Microsoft.Extensions.DependencyInjection;

namespace ERP.Notifications.Extensions;

/// <summary>
/// Register SignalR services in DI.
/// </summary>
public static class NotificationServiceExtensions
{
    public static IServiceCollection AddNotificationServices(this IServiceCollection services)
    {
        services.AddSignalR();
        return services;
    }
}
