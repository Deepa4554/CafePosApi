using CafePOS.Api.Data;
using CafePOS.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace CafePOS.Api.Infrastructure;

/// <summary>Config for reaching the standalone Node whatsapp-service — only used by
/// WhatsAppNodeClient now (the staff pairing/logout/QR-stream proxy in WhatsAppController).
/// WhatsAppEventPublisher itself no longer calls out to Node at all; it just writes to
/// Postgres, which whatsapp-service polls independently. Left blank, pairing is simply
/// unavailable — see WhatsAppController.Pair's IsConfigured check.</summary>
public class WhatsAppServiceOptions
{
    /// <summary>e.g. "http://localhost:4100" — the Node service's base URL.</summary>
    public string BaseUrl { get; set; } = "";
    /// <summary>Shared secret CafePosApi sends to whatsapp-service as X-Service-Api-Key.
    /// Same secret whatsapp-service must send back on its own calls into
    /// api/internal/whatsapp/* — see ServiceApiKeyAuthenticationHandler.</summary>
    public string ApiKey { get; set; } = "";
}

/// <summary>
/// Queues WhatsApp messages for delivery — writes to WhatsAppMessageLog (Postgres) instead of
/// an external queue service (BullMQ+Redis). The whatsapp-service polls the queue table every
/// 2–5 seconds for pending jobs. Same "must never make a save slower or fail" contract as
/// IRealtimeNotifier/IPushNotificationSender (see CafePosDbContext.SaveChangesAsync, the only
/// caller). CafePosApi has no idea whether Baileys or the official WhatsApp Cloud API is
/// running on the other end — this interface is the entire seam that keeps the module swappable.
/// </summary>
public interface IWhatsAppEventPublisher
{
    Task NotifyOrderStatusChangedAsync(int tenantId, int orderId, OrderStatus newStatus, CancellationToken ct = default);
    Task NotifyBillGeneratedAsync(int tenantId, int orderId, CancellationToken ct = default);
}

/// <summary>Queue outbound messages into WhatsAppMessageLog. Opens its own DbContext scope via
/// IServiceScopeFactory so writes don't block the caller's transaction — same pattern as
/// FcmPushNotificationSender. The whatsapp-service independently polls the queue table.</summary>
public class WhatsAppEventPublisher(IServiceScopeFactory scopeFactory, ILogger<WhatsAppEventPublisher> logger)
    : IWhatsAppEventPublisher
{
    public Task NotifyOrderStatusChangedAsync(int tenantId, int orderId, OrderStatus newStatus, CancellationToken ct = default) =>
        EnqueueAsync(tenantId, orderId, WhatsAppMessageType.StatusUpdate, ct);

    public Task NotifyBillGeneratedAsync(int tenantId, int orderId, CancellationToken ct = default) =>
        EnqueueAsync(tenantId, orderId, WhatsAppMessageType.BillPdf, ct);

    private async Task EnqueueAsync(int tenantId, int orderId, WhatsAppMessageType type, CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CafePosDbContext>();

            // Check if enabled for this tenant.
            var enabled = await db.Settings.IgnoreQueryFilters()
                .Where(s => s.TenantId == tenantId)
                .Select(s => s.WhatsAppOrderUpdatesEnabled)
                .FirstOrDefaultAsync(ct);
            if (!enabled) return;

            // A job never carries "which status triggered it" — whatsapp-service always
            // re-reads the order's CURRENT live status at send time (so it never texts a
            // stale status). That's correct for a single transition, but if New->Preparing-
            // >Ready happens faster than one poll interval, each transition enqueues its own
            // job, and by the time both get processed the order is already Ready — both jobs
            // read the same current status and both send it, producing the exact duplicate
            // "Prepared" message seen in testing. Skip enqueueing a second job of the same
            // type for the same order while one is still waiting/being sent — the one
            // already queued will pick up whatever the freshest status is when it runs.
            var alreadyQueued = await db.WhatsAppMessageLogs.IgnoreQueryFilters().AnyAsync(m =>
                m.TenantId == tenantId && m.OrderId == orderId && m.Type == type &&
                m.Direction == WhatsAppMessageDirection.Outbound &&
                (m.Status == WhatsAppMessageStatus.Pending || m.Status == WhatsAppMessageStatus.Processing), ct);
            if (alreadyQueued) return;

            // Queue a Pending job — the whatsapp-service will poll, fetch full details,
            // and send. If no linked phone yet, the service just marks it Skipped.
            db.WhatsAppMessageLogs.Add(new WhatsAppMessageLog
            {
                TenantId = tenantId,
                OrderId = orderId,
                Direction = WhatsAppMessageDirection.Outbound,
                Type = type,
                Status = WhatsAppMessageStatus.Pending,
                NextAttemptAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // Same rule as RealtimeNotifier: the original save already succeeded.
            // A queue write failure must never surface as a request failure.
            logger.LogWarning(ex, "Failed to enqueue WhatsApp {Type} for order {OrderId} (tenant {TenantId}).", type, orderId, tenantId);
        }
    }
}
