using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CafePOS.Api.Contracts;
using CafePOS.Api.Data;
using CafePOS.Api.Domain;
using CafePOS.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CafePOS.Api.Controllers;

/// <summary>
/// The cafe-facing half of the Zomato/Swiggy bridge: everything the Dyno desktop app calls.
///
/// Dyno runs on a Windows box logged into one cafe's aggregator accounts. It does NOT expose an
/// API we can reach — its own server binds 127.0.0.1 — so the traffic runs the other way: Dyno
/// polls the aggregators every 40s and POSTs new orders here, and every 30s it asks here for
/// instructions to carry out (accept/ready/reject, item stock). That inversion is why this is a
/// webhook controller and not an outbound client, and why no cafe needs a static IP or an open
/// port for the integration to work.
///
/// AUTHENTICATION. Every route hangs off an opaque per-tenant token in the path, and that is the
/// only credential available: Dyno's webhook client sends no headers of its own, it simply POSTs
/// to whatever base URL it was configured with. So this cannot use the ServiceOnly /
/// X-Service-Api-Key scheme the WhatsApp bridge uses (see ServiceApiKeyAuthenticationHandler) —
/// and shouldn't anyway, since that key is one global secret while this one sits on a machine at
/// a cafe. Same shape as the Borzo callback token (see CafeSettings.BorzoCallbackToken), for the
/// same reason: the third party has no signature convention, so the server controls both what is
/// configured and what is checked.
///
/// Because the resolved principal carries no tenant, every query here runs IgnoreQueryFilters()
/// and matches TenantId by hand — identical to WhatsAppInternalController, and for the identical
/// reason: the global filter would otherwise fall back to the default tenant and quietly read
/// somebody else's data.
///
/// RESPONSE BODIES ARE PART OF THE CONTRACT. Dyno inspects what we return and logs a failure (and
/// in some paths refuses to advance) unless it sees the exact `{"status": n}` shape it expects —
/// see DynoAck. Returning a bare 200 with no body is a silent breakage, so every handler answers
/// with one.
/// </summary>
[ApiController]
[Route("api/dyno/{bridgeToken}")]
[AllowAnonymous]
[ApiExplorerSettings(IgnoreApi = true)]
public class DynoWebhookController(
    CafePosDbContext db,
    IOrderBuildingService orderBuilder,
    IRealtimeNotifier realtime,
    ILogger<DynoWebhookController> logger) : ControllerBase
{
    /// <summary>Dyno's own vocabulary for what it wants done, echoed back in acks. Named
    /// constants because the numbers are meaningless on sight and appear in several places.</summary>
    private const int CommandAccept = 1;
    private const int CommandMarkReady = 3;
    private const int CommandReject = -1;
    private const int OutcomeAccepted = 2;
    private const int OutcomeReady = 4;
    private const int OutcomeRejected = -2;

    // Order.PlatformStatus values — our side of the state machine.
    private const string PlatformNew = "NEW";
    private const string PlatformAccepted = "ACCEPTED";
    private const string PlatformReady = "READY";
    private const string PlatformRejected = "REJECTED";

    // ---------- Orders in ----------

    /// <summary>New/updated aggregator orders. Dyno keeps re-sending a batch until it gets a
    /// 2xx, so this must be idempotent — the unique index on
    /// (TenantId, PlatformProvider, PlatformOrderId) is the backstop, and an order we already
    /// hold is simply acknowledged again.</summary>
    [HttpPost("orders")]
    public async Task<IActionResult> IngestOrders(string bridgeToken, [FromBody] DynoOrdersEnvelope body, CancellationToken ct)
    {
        var settings = await ResolveTenantAsync(bridgeToken, ct);
        if (settings is null) return NotFound();

        var tenantId = settings.TenantId;
        await TouchContactAsync(settings, ct);

        var ingested = 0;
        foreach (var incoming in body.Orders ?? [])
        {
            if (string.IsNullOrWhiteSpace(incoming.OrderId)) continue;

            var provider = ParseProvider(incoming.Vendor);
            if (provider is null)
            {
                logger.LogWarning("Dyno: tenant {TenantId} sent order {OrderId} for unknown vendor {Vendor}",
                    tenantId, incoming.OrderId, incoming.Vendor);
                continue;
            }

            // The outlet must be one this tenant actually claims. Defence in depth behind the
            // token: a misconfigured Dyno pointed at the wrong cafe's URL would otherwise inject
            // its orders here.
            if (!OutletBelongsToTenant(settings, provider.Value, incoming.ResId))
            {
                logger.LogWarning("Dyno: tenant {TenantId} sent order {OrderId} for unconfigured {Provider} outlet {ResId}",
                    tenantId, incoming.OrderId, provider, incoming.ResId);
                continue;
            }

            var providerName = provider.Value.ToString().ToLowerInvariant();
            var already = await db.Orders.IgnoreQueryFilters().AnyAsync(
                o => o.TenantId == tenantId && o.PlatformProvider == providerName && o.PlatformOrderId == incoming.OrderId, ct);
            if (already) continue;

            try
            {
                await IngestOneAsync(settings, provider.Value, incoming, ct);
                ingested++;
            }
            catch (DbUpdateException ex)
            {
                // Two Dyno posts of the same order raced past the AnyAsync check above. The
                // index did its job; this is the expected outcome, not a failure to report.
                logger.LogInformation(ex, "Dyno: duplicate order {OrderId} for tenant {TenantId} rejected by index",
                    incoming.OrderId, tenantId);
            }
        }

        if (ingested > 0) await realtime.NotifyOrdersChangedAsync(new HashSet<int> { tenantId });
        return Ok(new DynoAck(200));
    }

    private async Task IngestOneAsync(CafeSettings settings, PlatformProviderKind provider, DynoOrder incoming, CancellationToken ct)
    {
        var tenantId = settings.TenantId;
        var providerName = provider.ToString().ToLowerInvariant();
        var parsed = DynoOrderParser.Parse(incoming.Data);

        var mappings = await db.PlatformMenuMappings.IgnoreQueryFilters()
            .Where(m => m.TenantId == tenantId && m.Provider == provider && m.ResId == incoming.ResId && !m.IsCategory)
            .ToListAsync(ct);
        var byPlatformId = mappings
            .Where(m => m.MenuItemId != null)
            .ToDictionary(m => m.PlatformEntityId, m => m.MenuItemId!.Value);

        var lines = new List<CreateOrderItemDto>();
        var unmapped = new List<DynoParsedLine>();
        foreach (var line in parsed.Lines)
        {
            if (line.PlatformItemId is not null && byPlatformId.TryGetValue(line.PlatformItemId, out var menuItemId))
                lines.Add(new CreateOrderItemDto(menuItemId, line.Qty, Modifier: null));
            else
                unmapped.Add(line);
        }

        // Anything we couldn't match still has to reach the kitchen and the bill. Dropping a line
        // would under-produce the order and under-charge for it, and the cafe would find out from
        // an angry customer rather than from us. Unmatched lines (and a wholly unreadable payload)
        // become open-price lines on a per-tenant placeholder item, carrying the aggregator's own
        // price, and the floor gets told.
        if (unmapped.Count > 0 || lines.Count == 0)
        {
            var placeholderId = await GetOrCreatePlaceholderItemAsync(tenantId, ct);
            if (unmapped.Count > 0)
            {
                foreach (var line in unmapped)
                    lines.Add(new CreateOrderItemDto(placeholderId, line.Qty, Modifier: Describe(line), OpenPrice: line.UnitPrice ?? 0m));
            }
            else
            {
                // Nothing readable at all — one line carrying the order's total so the money is
                // right even though the dishes have to be read off the aggregator's own screen.
                lines.Add(new CreateOrderItemDto(placeholderId, 1,
                    Modifier: $"Unreadable {providerName} order — check the {providerName} dashboard",
                    OpenPrice: parsed.GrossAmount ?? 0m));
            }
        }

        var order = await orderBuilder.BuildOrderAsync(
            db, "DELIVERY", tableCode: null,
            guestName: parsed.CustomerName ?? $"{Capitalise(providerName)} order",
            items: lines, discountPct: 0, user: null, explicitTenantId: tenantId,
            guestPhone: DigitsOrNull(parsed.CustomerPhone));

        order.PlatformProvider = providerName;
        order.PlatformOrderId = incoming.OrderId;
        order.PlatformStatus = PlatformNew;
        order.PlatformGrossAmount = parsed.GrossAmount;

        // Auto-accept fires straight to the kitchen: an aggregator cancels an unaccepted order
        // within minutes, and not needing anyone to watch a second screen is the point of the
        // bridge. With it off, the order waits in the same staff-confirmation queue QR orders use.
        if (settings.DynoAutoAccept) await orderBuilder.FireUnfiredItemsAsync(db, order, tenantId);
        else orderBuilder.MarkPendingConfirmation(db, order, tenantId);

        db.PlatformOrderPayloads.Add(new PlatformOrderPayload
        {
            TenantId = tenantId,
            OrderId = order.Id,
            Provider = provider,
            PlatformOrderId = incoming.OrderId!,
            RawJson = incoming.Data.ValueKind == JsonValueKind.Undefined ? "null" : incoming.Data.GetRawText(),
            LinesParsed = parsed.Lines.Count > 0 && unmapped.Count == 0,
        });

        if (unmapped.Count > 0 || parsed.Lines.Count == 0)
        {
            db.Notifications.Add(new AppNotification
            {
                TenantId = tenantId,
                Title = $"{Capitalise(providerName)} order needs checking",
                Body = parsed.Lines.Count == 0
                    ? $"Order {incoming.OrderId} arrived but its items couldn't be read. It's on the bill at its total — check the {providerName} dashboard for what to cook."
                    : $"Order {incoming.OrderId} has {unmapped.Count} item(s) not linked to your menu: {string.Join(", ", unmapped.Take(4).Select(Describe))}.",
                Category = NotificationCategory.Order,
            });
        }

        await db.SaveChangesAsync(ct);
    }

    // ---------- Commands out ----------

    /// <summary>What Dyno should do next for this outlet. Polled every 30s.</summary>
    [HttpGet("{resId}/orders/status")]
    public async Task<ActionResult<DynoOrderCommandFeed>> GetOrderCommands(string bridgeToken, string resId, CancellationToken ct)
    {
        var settings = await ResolveTenantAsync(bridgeToken, ct);
        if (settings is null) return NotFound();
        await TouchContactAsync(settings, ct);

        // Scope the feed to the aggregator that owns this outlet. Dyno polls this route once per
        // outlet and executes what comes back against THAT platform's API, so handing a Zomato
        // poll a Swiggy order id makes it call the wrong aggregator with an id that platform has
        // never heard of. An unrecognised outlet gets nothing rather than everything.
        var provider = ProviderForOutlet(settings, resId);
        if (provider is null) return new DynoOrderCommandFeed();
        var providerName = provider.Value.ToString().ToLowerInvariant();

        var tenantId = settings.TenantId;
        // Only orders still mid-flight on the aggregator's side. PlatformStatus tracks the
        // platform, not the kitchen: an order can be served in the cafe while its accept call is
        // still being retried, and vice versa.
        var live = await db.Orders.IgnoreQueryFilters()
            .Where(o => o.TenantId == tenantId
                && o.PlatformOrderId != null
                && o.PlatformProvider == providerName
                && (o.PlatformStatus == PlatformNew || o.PlatformStatus == PlatformAccepted))
            .Select(o => new { o.PlatformOrderId, o.PlatformStatus, o.Status, o.Cancelled })
            .ToListAsync(ct);

        var commands = new List<DynoOrderCommand>();
        foreach (var o in live)
        {
            if (o.PlatformStatus == PlatformNew)
            {
                // Cancelled before it was ever accepted — tell the aggregator to reject rather
                // than accepting something the cafe has already refused. Zomato only; Dyno has no
                // Swiggy reject call, so a Swiggy order just stops being advanced.
                commands.Add(new DynoOrderCommand
                {
                    OrderId = o.PlatformOrderId!,
                    Status = o.Cancelled ? CommandReject : CommandAccept,
                    PrepTime = settings.DynoDefaultPrepTimeMins,
                });
            }
            else if (o.PlatformStatus == PlatformAccepted && IsReadyForPickup(o.Status))
            {
                commands.Add(new DynoOrderCommand { OrderId = o.PlatformOrderId!, Status = CommandMarkReady });
            }
        }

        return new DynoOrderCommandFeed { Orders = commands, OrderHistory = false };
    }

    /// <summary>Dyno reporting that it carried out a command. The response body must echo the
    /// same code back or Dyno records the step as failed and will retry it.</summary>
    [HttpPost("orders/{platformOrderId}/status")]
    public async Task<ActionResult<DynoAck>> AckOrderStatus(
        string bridgeToken, string platformOrderId, [FromBody] DynoOrderStatusAck body, CancellationToken ct)
    {
        var settings = await ResolveTenantAsync(bridgeToken, ct);
        if (settings is null) return NotFound();

        var tenantId = settings.TenantId;
        var order = await db.Orders.IgnoreQueryFilters()
            .FirstOrDefaultAsync(o => o.TenantId == tenantId && o.PlatformOrderId == platformOrderId, ct);

        if (order is not null)
        {
            order.PlatformStatus = body.StatusCode switch
            {
                OutcomeAccepted => PlatformAccepted,
                OutcomeReady => PlatformReady,
                OutcomeRejected => PlatformRejected,
                _ => order.PlatformStatus,
            };
            await db.SaveChangesAsync(ct);
        }
        else
        {
            logger.LogWarning("Dyno: status ack {Code} for unknown order {OrderId}, tenant {TenantId}",
                body.StatusCode, platformOrderId, tenantId);
        }

        // Echoed even when the order is unknown: the ack is Dyno telling us what it already did on
        // the aggregator, so answering "failed" would only make it repeat a completed action.
        return new DynoAck(body.StatusCode);
    }

    // ---------- Stock / menu ----------

    /// <summary>Availability changes waiting to go out to this outlet, drained oldest-first.</summary>
    [HttpGet("{resId}/items")]
    public async Task<ActionResult<DynoItemFeed>> GetItemCommands(string bridgeToken, string resId, CancellationToken ct)
    {
        var settings = await ResolveTenantAsync(bridgeToken, ct);
        if (settings is null) return NotFound();
        await TouchContactAsync(settings, ct);

        var tenantId = settings.TenantId;
        var pending = await db.PlatformStockChanges.IgnoreQueryFilters()
            .Where(p => p.TenantId == tenantId && p.ResId == resId && p.ProcessedAt == null)
            .OrderBy(p => p.CreatedAt)
            .Take(50)
            .ToListAsync(ct);

        // Ask for the outlet's menu only while we have nothing to map against — it costs Dyno a
        // full aggregator menu fetch, and the request stops as soon as a dump lands.
        var haveCatalog = await db.PlatformCatalogEntries.IgnoreQueryFilters()
            .AnyAsync(c => c.TenantId == tenantId && c.ResId == resId, ct);

        return new DynoItemFeed
        {
            Items = pending.Where(p => !p.IsCategory)
                .Select(p => new DynoStockEntry { Id = p.PlatformEntityId, StockStatus = p.DesiredInStock }).ToList(),
            Categories = pending.Where(p => p.IsCategory)
                .Select(p => new DynoStockEntry { Id = p.PlatformEntityId, StockStatus = p.DesiredInStock }).ToList(),
            GetAllItems = !haveCatalog,
        };
    }

    [HttpPost("{resId}/items/status")]
    public Task<ActionResult<DynoAck>> AckItemStock(string bridgeToken, string resId, [FromBody] DynoStockAck body, CancellationToken ct)
        => AckStockAsync(bridgeToken, resId, body, isCategory: false, ct);

    [HttpPost("{resId}/categories/status")]
    public Task<ActionResult<DynoAck>> AckCategoryStock(string bridgeToken, string resId, [FromBody] DynoStockAck body, CancellationToken ct)
        => AckStockAsync(bridgeToken, resId, body, isCategory: true, ct);

    private async Task<ActionResult<DynoAck>> AckStockAsync(
        string bridgeToken, string resId, DynoStockAck body, bool isCategory, CancellationToken ct)
    {
        var settings = await ResolveTenantAsync(bridgeToken, ct);
        if (settings is null) return NotFound();

        var tenantId = settings.TenantId;
        if (!string.IsNullOrWhiteSpace(body.EntityId))
        {
            // Close every outstanding row for this entity, not just one: several toggles of the
            // same item queue several rows, and one confirmed push settles all of them — they all
            // asked for a state this one just achieved.
            var rows = await db.PlatformStockChanges.IgnoreQueryFilters()
                .Where(p => p.TenantId == tenantId && p.ResId == resId && p.IsCategory == isCategory
                    && p.PlatformEntityId == body.EntityId && p.ProcessedAt == null
                    && p.DesiredInStock == body.StockStatus)
                .ToListAsync(ct);
            foreach (var row in rows) row.ProcessedAt = DateTime.UtcNow;
            if (rows.Count > 0) await db.SaveChangesAsync(ct);
        }

        return new DynoAck(200);
    }

    /// <summary>The outlet's full aggregator menu, sent in reply to our GetAllItems request.
    /// Replaces whatever catalog we held: the aggregator is the authority on its own menu, and
    /// merging would leave deleted dishes behind forever.</summary>
    [HttpPost("{resId}/items")]
    public async Task<ActionResult<DynoAck>> ReceiveCatalog(string bridgeToken, string resId, [FromBody] DynoBulkPayload body, CancellationToken ct)
    {
        var settings = await ResolveTenantAsync(bridgeToken, ct);
        if (settings is null) return NotFound();
        await TouchContactAsync(settings, ct);

        var tenantId = settings.TenantId;
        var provider = ProviderForOutlet(settings, resId);
        if (provider is null) return new DynoAck(200);

        var entries = DynoCatalogParser.Parse(body.StatusResponse);
        if (entries.Count == 0)
        {
            logger.LogWarning("Dyno: empty/unreadable catalog dump for tenant {TenantId} outlet {ResId}", tenantId, resId);
            return new DynoAck(200);
        }

        var existing = await db.PlatformCatalogEntries.IgnoreQueryFilters()
            .Where(c => c.TenantId == tenantId && c.ResId == resId && c.Provider == provider.Value)
            .ToListAsync(ct);
        db.PlatformCatalogEntries.RemoveRange(existing);

        foreach (var e in entries)
        {
            db.PlatformCatalogEntries.Add(new PlatformCatalogEntry
            {
                TenantId = tenantId,
                Provider = provider.Value,
                ResId = resId,
                PlatformEntityId = e.Id,
                Name = e.Name,
                IsCategory = e.IsCategory,
                PlatformPrice = e.Price,
            });
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Dyno: stored {Count} catalog entries for tenant {TenantId} outlet {ResId}",
            entries.Count, tenantId, resId);
        return new DynoAck(200);
    }

    /// <summary>Order history dumps. Acknowledged so Dyno's loop stays healthy, but not stored —
    /// the orders that matter arrive through the live feed, and history would duplicate them.</summary>
    [HttpPost("{resId}/orders/history")]
    public async Task<ActionResult<DynoAck>> ReceiveOrderHistory(string bridgeToken, string resId, [FromBody] DynoBulkPayload body, CancellationToken ct)
    {
        var settings = await ResolveTenantAsync(bridgeToken, ct);
        if (settings is null) return NotFound();
        await TouchContactAsync(settings, ct);
        return new DynoAck(200);
    }

    // ---------- Helpers ----------

    /// <summary>Resolves the URL's token to a tenant, or null. Also the integration's on/off
    /// switch: a disabled tenant looks exactly like an unknown one from outside.</summary>
    private async Task<CafeSettings?> ResolveTenantAsync(string bridgeToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(bridgeToken) || bridgeToken.Length < 16) return null;

        var settings = await db.Settings.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.DynoBridgeToken == bridgeToken && s.DynoEnabled, ct);
        if (settings?.DynoBridgeToken is null) return null;

        // The lookup above already matched, so this only guards against a provider-level
        // case-insensitive or trailing-space comparison letting a near-miss through.
        var a = Encoding.UTF8.GetBytes(settings.DynoBridgeToken);
        var b = Encoding.UTF8.GetBytes(bridgeToken);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b) ? settings : null;
    }

    /// <summary>Records that the bridge is alive. The owner-facing screen reads this to say
    /// "connected" or "not heard from since ..." — the only way a cafe can tell that its Windows
    /// box has stopped or its aggregator login has expired, since Dyno cannot report that itself.</summary>
    private async Task TouchContactAsync(CafeSettings settings, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        // Every call would otherwise write to Settings twice a minute forever.
        if (settings.DynoLastContactAt is { } last && now - last < TimeSpan.FromMinutes(1)) return;

        settings.DynoLastContactAt = now;

        // Keep the generic Integrations Hub grid in sync — it reads Integration.Status and knows
        // nothing about Dyno, exactly as it knows nothing about Baileys for WhatsApp
        // (WhatsAppInternalController.UpsertSession does the same thing for the same reason).
        foreach (var name in ConfiguredIntegrationNames(settings))
        {
            var integration = await db.Integrations.IgnoreQueryFilters()
                .FirstOrDefaultAsync(i => i.TenantId == settings.TenantId && i.Name == name, ct);
            if (integration is null) continue;
            integration.Status = IntegrationStatus.Connected;
            integration.ConnectedAt ??= now;
        }

        await db.SaveChangesAsync(ct);
    }

    private static IEnumerable<string> ConfiguredIntegrationNames(CafeSettings s)
    {
        if (!string.IsNullOrWhiteSpace(s.ZomatoRestaurantId)) yield return "Zomato";
        if (!string.IsNullOrWhiteSpace(s.SwiggyRestaurantId)) yield return "Swiggy";
    }

    private static PlatformProviderKind? ParseProvider(string? vendor) => vendor?.Trim().ToLowerInvariant() switch
    {
        "zomato" => PlatformProviderKind.Zomato,
        "swiggy" => PlatformProviderKind.Swiggy,
        _ => null,
    };

    private static bool OutletBelongsToTenant(CafeSettings s, PlatformProviderKind provider, string? resId)
    {
        var configured = provider == PlatformProviderKind.Zomato ? s.ZomatoRestaurantId : s.SwiggyRestaurantId;
        if (string.IsNullOrWhiteSpace(configured)) return false;
        // A blank resId means the single-outlet form of Dyno's API, which doesn't echo one back.
        if (string.IsNullOrWhiteSpace(resId)) return true;
        return string.Equals(configured.Trim(), resId.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static PlatformProviderKind? ProviderForOutlet(CafeSettings s, string resId)
    {
        if (string.Equals(s.ZomatoRestaurantId?.Trim(), resId.Trim(), StringComparison.OrdinalIgnoreCase))
            return PlatformProviderKind.Zomato;
        if (string.Equals(s.SwiggyRestaurantId?.Trim(), resId.Trim(), StringComparison.OrdinalIgnoreCase))
            return PlatformProviderKind.Swiggy;
        return null;
    }

    /// <summary>An order counts as ready for the aggregator once the kitchen has finished it —
    /// Ready, or Served for cafes whose flow skips straight past Ready. Order.Status is derived
    /// from the line states by RecomputeTotals, so this needs no separate flag of its own.</summary>
    private static bool IsReadyForPickup(OrderStatus status) =>
        status is OrderStatus.Ready or OrderStatus.Served;

    /// <summary>The per-tenant catch-all item unmatched aggregator lines are billed on. Created on
    /// first need rather than seeded, so cafes that never use the bridge don't get a stray item on
    /// their menu. Open-price so each line can carry the aggregator's own rate.
    ///
    /// Available MUST stay true even though nobody is meant to ring this up by hand:
    /// BuildOrderAsync rejects an unavailable line outright, so an unavailable placeholder would
    /// throw on exactly the orders it exists to rescue — and since a 400 makes Dyno redeliver the
    /// same order every 40s, the cafe would get a retry storm instead of the order. Being
    /// open-price already keeps it off the customer QR menu (see MenuItem.IsOpenPrice); its own
    /// category keeps it out of the way on the POS grid.</summary>
    private async Task<int> GetOrCreatePlaceholderItemAsync(int tenantId, CancellationToken ct)
    {
        const string name = "Online order item";
        var existing = await db.MenuItems.IgnoreQueryFilters()
            .Where(m => m.TenantId == tenantId && m.Name == name)
            .Select(m => (int?)m.Id)
            .FirstOrDefaultAsync(ct);
        if (existing is not null) return existing.Value;

        var stationId = await db.Stations.IgnoreQueryFilters()
            .Where(s => s.TenantId == tenantId && s.Active)
            .OrderBy(s => s.SortOrder).ThenBy(s => s.Id)
            .Select(s => (int?)s.Id)
            .FirstOrDefaultAsync(ct);

        var item = new MenuItem
        {
            TenantId = tenantId,
            Name = name,
            Category = "Online Orders",
            Price = 0m,
            Available = true,
            IsOpenPrice = true,
            StationId = stationId ?? 0,
            Description = "Auto-created for Zomato/Swiggy lines that aren't linked to a menu item yet.",
        };
        db.MenuItems.Add(item);
        await db.SaveChangesAsync(ct);
        return item.Id;
    }

    private static string Describe(DynoParsedLine line) =>
        line.Name ?? line.PlatformItemId ?? "unknown item";

    private static string Capitalise(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static string? DigitsOrNull(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return null;
        var digits = new string(phone.Where(char.IsDigit).ToArray());
        // Aggregators hand out masked/relay numbers of varying shapes; keep the last 10 so the
        // CRM link behaves like every other 10-digit Indian mobile the rest of the app stores.
        return digits.Length >= 10 ? digits[^10..] : null;
    }
}
