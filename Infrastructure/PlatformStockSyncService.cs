using CafePOS.Api.Data;
using CafePOS.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace CafePOS.Api.Infrastructure;

public interface IPlatformStockSyncService
{
    /// <summary>Queues "this item is now available / unavailable" for every aggregator outlet it's
    /// mapped to. No-op for a menu item nobody has mapped, which is the normal case. Does not
    /// save — the caller saves, so the queue row and the availability change land in one
    /// transaction and a rolled-back edit can't leave a phantom instruction behind.</summary>
    Task EnqueueAvailabilityAsync(CafePosDbContext db, int menuItemId, bool available, CancellationToken ct = default);
}

/// <summary>
/// Turns a POS availability toggle into work for the Dyno bridge to push to Zomato/Swiggy.
///
/// Only ever enqueues. It deliberately does not call anything outward: the aggregator APIs are
/// reachable solely from the cafe's own Dyno box, and even if they weren't, making a staff member
/// wait on a third party to flip a switch behind the counter would be the wrong trade. The bridge
/// drains the queue on its own 30s poll (see DynoWebhookController.GetItemCommands).
/// </summary>
public class PlatformStockSyncService : IPlatformStockSyncService
{
    public async Task EnqueueAvailabilityAsync(CafePosDbContext db, int menuItemId, bool available, CancellationToken ct = default)
    {
        var mappings = await db.PlatformMenuMappings
            .Where(m => m.MenuItemId == menuItemId && !m.IsCategory)
            .ToListAsync(ct);
        if (mappings.Count == 0) return;

        foreach (var m in mappings)
        {
            // Supersede anything still queued for the same entity: only the latest intent matters,
            // and leaving both would have the bridge flip the item twice for no reason. Marked
            // processed rather than deleted so the audit trail stays continuous.
            var stale = await db.PlatformStockChanges
                .Where(p => p.Provider == m.Provider && p.ResId == m.ResId
                    && p.PlatformEntityId == m.PlatformEntityId && !p.IsCategory && p.ProcessedAt == null)
                .ToListAsync(ct);
            foreach (var s in stale)
            {
                s.ProcessedAt = DateTime.UtcNow;
                s.FailureReason = "Superseded by a newer availability change";
            }

            db.PlatformStockChanges.Add(new PlatformStockChange
            {
                TenantId = m.TenantId,
                Provider = m.Provider,
                ResId = m.ResId,
                PlatformEntityId = m.PlatformEntityId,
                IsCategory = false,
                DesiredInStock = available,
            });
        }
    }
}
