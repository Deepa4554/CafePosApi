using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace CafePOS.Api.Infrastructure;

/// <summary>Real-time push for orders/tables/KDS (see useOrders.ts/useTables.ts) and for
/// per-user screen-access changes (see useLiveAccessSync on the client) — both replace what
/// used to be plain polling. Server-to-client only: every connected client is dropped into
/// its tenant's group and its own user group on connect (from the JWT's "tenantId" and
/// NameIdentifier claims, same source CafePosDbContext's tenant filter uses) and the client
/// never calls back into the hub — CafePosDbContext.SaveChangesAsync triggers ordersChanged,
/// StaffController.UpdateScreenAccess triggers accessChanged, so no other caller has to
/// remember to notify.</summary>
[Authorize]
public class OrdersHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        var tenantId = Context.User?.FindFirst("tenantId")?.Value;
        if (tenantId is not null)
            await Groups.AddToGroupAsync(Context.ConnectionId, RealtimeNotifier.TenantGroup(int.Parse(tenantId)));

        // Also joins a per-user group (distinct from the tenant-wide one above) so
        // accessChanged can be targeted at just the one staff member whose screen access
        // changed, instead of waking every connected device in the cafe — see
        // RealtimeNotifier.NotifyAccessChangedAsync.
        var userId = Context.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (tenantId is not null && userId is not null)
            await Groups.AddToGroupAsync(Context.ConnectionId, RealtimeNotifier.UserGroup(int.Parse(tenantId), int.Parse(userId)));

        await base.OnConnectedAsync();
    }
}
