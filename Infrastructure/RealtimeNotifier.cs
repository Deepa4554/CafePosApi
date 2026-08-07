using Microsoft.AspNetCore.SignalR;

namespace CafePOS.Api.Infrastructure;

/// <summary>Names carried by the "dataChanged" event — each one maps to a group of React
/// Query keys on the client (see ordersRealtime.ts's SCOPE_QUERY_KEYS, which must stay in
/// sync with this list). Deliberately coarse: a scope per screen-family, not per entity, so
/// the client never has to know which of a dozen tables a save actually touched.
///
/// Deliberately does NOT include AppNotification: that entity IS the push-notification
/// payload (see AppNotification.TargetUserId/TargetRolesCsv + IPushNotificationSender), so its
/// delivery already has its own precise, per-user/per-role channel — FCM. Folding it into this
/// tenant-wide "something changed, go refetch" broadcast would mean a notification meant for
/// one staff member also waking every other connected device to refetch the bell, which is
/// both wasteful and the exact "two channels doing the same job differently" mixing this
/// class exists to avoid. useNotifications' query only ever gets invalidated by an incoming
/// push (see usePushNotifications.ts/.web.ts) — never by SignalR — and that boundary should
/// stay that way.
///
/// Tasks/Approvals ARE included, because — unlike AppNotification — StaffTask/ApprovalRequest
/// are shared list data multiple staff view together (the Tasks board, the Approvals queue),
/// structurally the same as Orders/Menu, not a personal delivery.</summary>
public static class RealtimeScopes
{
    public const string Menu = "menu";
    public const string Inventory = "inventory";
    public const string Customers = "customers";
    public const string Staff = "staff";
    public const string Tasks = "tasks";
    public const string Approvals = "approvals";
    public const string Settings = "settings";
}

/// <summary>Thin wrapper around IHubContext&lt;OrdersHub&gt; so CafePosDbContext (which fires
/// the actual notifications, see SaveChangesAsync) doesn't need a direct SignalR dependency.
/// Fire-and-forget by design — a dropped push is fine, the client's next natural refetch
/// (screen focus, pull-to-refresh) still catches up; nothing here is allowed to make a save
/// fail or slow down because a client's socket hiccupped.</summary>

public interface IRealtimeNotifier
{
    Task NotifyOrdersChangedAsync(IReadOnlySet<int> tenantIds);

    /// <summary>Everything outside the order/table family — menu, inventory, staff, ... —
    /// which until now had no push at all. Most of those hooks have no refetchInterval either
    /// (useMenuItems is the clearest case), so a price edit on one till reached the next one
    /// only when that screen happened to remount or regain window focus. Separate event
    /// rather than more "ordersChanged" traffic so already installed app builds, which only
    /// know that one event, keep working untouched.</summary>
    Task NotifyDataChangedAsync(IReadOnlyDictionary<int, IReadOnlySet<string>> scopesByTenant);

    Task NotifyAccessChangedAsync(int tenantId, int userId);

    /// <summary>Same "accessChanged" event as NotifyAccessChangedAsync, but broadcast to
    /// every connected device in the tenant instead of one user's own group — used when a
    /// platform admin edits the CAFE-level screen access (see
    /// SuperAdminController.UpdateTenantScreenAccess), which can change what every login in
    /// the cafe sees, not just one staff member's. The client's handler is already just
    /// "refetch my own access" (see useLiveAccessSync), so every device picking up the same
    /// event is exactly the right behavior — no new client-side wiring needed.</summary>
    Task NotifyTenantAccessChangedAsync(int tenantId);
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

    public async Task NotifyDataChangedAsync(IReadOnlyDictionary<int, IReadOnlySet<string>> scopesByTenant)
    {
        foreach (var (tenantId, scopes) in scopesByTenant)
        {
            if (scopes.Count == 0) continue;
            try
            {
                // One event carrying every scope the save touched, not one per scope — a
                // single order fire can write inventory + customer rows too, and three
                // separate frames would just be three wake-ups for the same instant.
                await hub.Clients.Group(TenantGroup(tenantId)).SendAsync("dataChanged", scopes.ToArray());
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to push dataChanged to tenant {TenantId}.", tenantId);
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

    public async Task NotifyTenantAccessChangedAsync(int tenantId)
    {
        try
        {
            await hub.Clients.Group(TenantGroup(tenantId)).SendAsync("accessChanged");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to push tenant-wide accessChanged to tenant {TenantId}.", tenantId);
        }
    }
}
