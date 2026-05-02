using System.Text.Json;
using ERP.Application.Abstractions.Identity;
using ERP.SharedKernel.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ERP.Persistence.Interceptors;

public sealed class AuditLogInterceptor : SaveChangesInterceptor
{
    private readonly ICurrentUserService _currentUser;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public AuditLogInterceptor(ICurrentUserService currentUser)
    {
        _currentUser = currentUser;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        var context = eventData.Context;
        if (context is null) return base.SavingChangesAsync(eventData, result, cancellationToken);

        var now = DateTime.UtcNow;
        Guid? userGuid = Guid.TryParse(_currentUser.UserId, out var parsed) ? parsed : null;

        var auditEntries = new List<AuditLog>();

        // AuditLog inherits BaseEntity (not AuditableEntity), so it's excluded by the type filter
        foreach (var entry in context.ChangeTracker.Entries<AuditableEntity>().ToList())
        {
            if (entry.State is EntityState.Detached or EntityState.Unchanged)
                continue;

            var auditLog = new AuditLog
            {
                UserId = userGuid,
                EntityType = entry.Entity.GetType().Name,
                EntityId = entry.Entity.Id.ToString(),
                Action = MapAction(entry.State),
                IpAddress = _currentUser.IpAddress,
                UserAgent = _currentUser.UserAgent,
                CreatedAt = now
            };

            switch (entry.State)
            {
                case EntityState.Added:
                    auditLog.NewValues = SerializeProperties(entry, useCurrentValues: true);
                    break;

                case EntityState.Modified:
                    auditLog.OldValues = SerializeChangedOld(entry);
                    auditLog.NewValues = SerializeChangedNew(entry);
                    break;

                case EntityState.Deleted:
                    auditLog.OldValues = SerializeProperties(entry, useCurrentValues: false);
                    break;
            }

            auditEntries.Add(auditLog);
        }

        foreach (var auditLog in auditEntries)
            context.Set<AuditLog>().Add(auditLog);

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private static string MapAction(EntityState state) => state switch
    {
        EntityState.Added => "Create",
        EntityState.Modified => "Update",
        EntityState.Deleted => "Delete",
        _ => state.ToString()
    };

    private static string SerializeProperties(EntityEntry entry, bool useCurrentValues)
    {
        var values = useCurrentValues ? entry.CurrentValues : entry.OriginalValues;
        var dict = new Dictionary<string, object?>();

        foreach (var prop in values.Properties)
        {
            if (prop.IsShadowProperty())
                continue;
            dict[prop.Name] = values[prop];
        }

        return JsonSerializer.Serialize(dict, JsonOptions);
    }

    private static string SerializeChangedOld(EntityEntry entry)
    {
        var dict = new Dictionary<string, object?>();

        foreach (var prop in entry.Properties)
        {
            if (prop.IsModified && !prop.Metadata.IsShadowProperty())
                dict[prop.Metadata.Name] = prop.OriginalValue;
        }

        return JsonSerializer.Serialize(dict, JsonOptions);
    }

    private static string SerializeChangedNew(EntityEntry entry)
    {
        var dict = new Dictionary<string, object?>();

        foreach (var prop in entry.Properties)
        {
            if (prop.IsModified && !prop.Metadata.IsShadowProperty())
                dict[prop.Metadata.Name] = prop.CurrentValue;
        }

        return JsonSerializer.Serialize(dict, JsonOptions);
    }
}
