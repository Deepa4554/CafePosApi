using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace CafePOS.Api.Infrastructure;

/// <summary>Real-time push for orders/tables/KDS — replaces the 5-8s polling those screens
/// used before (see useOrders.ts/useTables.ts on the client). Server-to-client only: every
/// connected client is dropped into its own tenant's group on connect (from the JWT's
/// "tenantId" claim, same source CafePosDbContext's tenant filter uses) and the client never
/// calls back into the hub — CafePosDbContext.SaveChangesAsync is the single place that
/// triggers a broadcast, so no controller has to remember to notify.</summary>
[Authorize]
public class OrdersHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        var tenantId = Context.User?.FindFirst("tenantId")?.Value;
        if (tenantId is not null)
            await Groups.AddToGroupAsync(Context.ConnectionId, RealtimeNotifier.TenantGroup(int.Parse(tenantId)));
        await base.OnConnectedAsync();
    }
}
