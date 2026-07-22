using Microsoft.AspNetCore.SignalR;

namespace CafePOS.Api.Infrastructure;

/// <summary>Thin wrapper around IHubContext&lt;OrdersHub&gt; so CafePosDbContext (which fires
/// the actual notifications, see SaveChangesAsync) doesn't need a direct SignalR dependency.
/// Fire-and-forget by design — a dropped push is fine, the client's next natural refetch
/// (screen focus, pull-to-refresh) still catches up; nothing here is allowed to make a save
/// fail or slow down because a client's socket hiccupped.</summary>
public interface IRealtimeNotifier
{
    Task NotifyOrdersChangedAsync(IReadOnlySet<int> tenantIds);
    Task NotifyAccessChangedAsync(int tenantId, int userId);
}

public class RealtimeNotifier(IHubContext<OrdersHub> hub, ILogger<RealtimeNotifier> logger) : IRealtimeNotifier
{
    public static string TenantGroup(int tenantId) => $"tenant:{tenantId}";
    public static string UserGroup(int tenantId, int userId) => $"user:{tenantId}:{userId}";

    public async Task NotifyOrdersChangedAsync(IReadOnlySet<int> tenantIds)
    {
        foreach (var tenantId in tenantIds)
        {
            try
            {
                await hub.Clients.Group(TenantGroup(tenantId)).SendAsync("ordersChanged");
            }
            catch (Exception ex)
            {
                // A push failing (e.g. transient backplane hiccup) must never surface as a
                // request failure — the mutation that triggered this already saved fine.
                logger.LogWarning(ex, "Failed to push ordersChanged to tenant {TenantId}.", tenantId);
            }
        }
    }

    /// <summary>Lets a staff member's own device pick up a screen-access change within
    /// seconds instead of waiting for useLiveAccessSync's safety-net poll — see
    /// StaffController.UpdateScreenAccess, the only place that calls this.</summary>
    public async Task NotifyAccessChangedAsync(int tenantId, int userId)
    {
        try
        {
            await hub.Clients.Group(UserGroup(tenantId, userId)).SendAsync("accessChanged");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to push accessChanged to user {UserId} in tenant {TenantId}.", userId, tenantId);
        }
    }
}
