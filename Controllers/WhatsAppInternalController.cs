using CafePOS.Api.Contracts;
using CafePOS.Api.Data;
using CafePOS.Api.Domain;
using CafePOS.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CafePOS.Api.Controllers;

/// <summary>
/// Called only by the standalone Node whatsapp-service (see WhatsAppNodeClient's mirror-image
/// on the other side, and ServiceApiKeyAuthenticationHandler for the auth scheme) — never by
/// the RN app. Not part of the public Swagger surface. Every query IgnoreQueryFilters()s and
/// matches TenantId explicitly: the ServiceOnly principal carries no tenantId claim, so the
/// normal global query filter would silently fall back to Tenant.DefaultTenantId and leak/hide
/// data across tenants if this weren't done consistently — see ITenantContext.TenantIdOrDefault.
/// </summary>
[ApiController]
[Route("api/internal/whatsapp")]
[Authorize(Policy = Policies.ServiceOnly)]
[ApiExplorerSettings(IgnoreApi = true)]
public class WhatsAppInternalController(CafePosDbContext db, ReceiptTokenService receiptTokens) : ControllerBase
{
    /// <summary>Resolves tenant+order from an inbound "TRACK &lt;id&gt;" before the caller
    /// knows which tenant's Baileys connection it arrived on is even relevant — used as a
    /// defense-in-depth check that a TrackingId actually belongs to the connection's own
    /// tenant (a mismatch means a typo'd/spoofed id from a different cafe's QR).</summary>
    [HttpGet("resolve-tenant")]
    public async Task<ActionResult<WhatsAppResolveTenantDto>> ResolveTenant([FromQuery] string trackingId)
    {
        var row = await db.WhatsAppTracking.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.TrackingId == trackingId);
        return row is null ? NotFound() : new WhatsAppResolveTenantDto(row.TenantId, row.OrderId, row.TrackingId);
    }

    [HttpGet("order-by-tracking/{trackingId}")]
    public async Task<ActionResult<WhatsAppOrderStatusDto>> GetOrderByTracking(string trackingId)
    {
        var row = await db.WhatsAppTracking.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.TrackingId == trackingId);
        if (row is null) return NotFound();
        return await BuildOrderStatusDtoAsync(row);
    }

    /// <summary>For the STATUS command when the sender's number is already linked but the
    /// message carries no TrackingId — most recent still-active QSR order for that phone.</summary>
    [HttpGet("order-by-phone")]
    public async Task<ActionResult<WhatsAppOrderStatusDto>> GetOrderByPhone([FromQuery] int tenantId, [FromQuery] string phone)
    {
        var row = await db.WhatsAppTracking.IgnoreQueryFilters()
            .Where(t => t.TenantId == tenantId && t.WhatsAppNumberE164 == phone)
            .OrderByDescending(t => t.CreatedAt)
            .FirstOrDefaultAsync();
        if (row is null) return NotFound();
        return await BuildOrderStatusDtoAsync(row);
    }

    private async Task<ActionResult<WhatsAppOrderStatusDto>> BuildOrderStatusDtoAsync(WhatsAppOrderTracking row)
    {
        var order = await db.Orders.IgnoreQueryFilters().FirstOrDefaultAsync(o => o.Id == row.OrderId && o.TenantId == row.TenantId);
        if (order is null) return NotFound();

        var businessName = await db.Settings.IgnoreQueryFilters()
            .Where(s => s.TenantId == row.TenantId)
            .Select(s => s.BusinessName)
            .FirstOrDefaultAsync() ?? "PrabandhOS";

        return new WhatsAppOrderStatusDto(
            row.TenantId, order.Id, OrderNumberFormat.Bill(order), order.TokenNumber,
            WhatsAppOrderStatusDto.ToStatusLabel(order.Status), order.Paid, businessName, row.TrackingId);
    }

    /// <summary>Idempotent upsert of the phone <-> tracking mapping (the TRACK command
    /// handler) — first writer wins, never overwrites an already-linked different number.
    /// See WhatsAppOrderTracking's doc comment.</summary>
    [HttpPost("tracking-mapping")]
    public async Task<ActionResult<WhatsAppTrackingMappingDto>> UpsertTrackingMapping(WhatsAppTrackingMappingRequest req)
    {
        var row = await db.WhatsAppTracking.IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.TrackingId == req.TrackingId && t.TenantId == req.TenantId);
        if (row is null) return NotFound();

        if (row.WhatsAppNumberE164 is null)
        {
            row.WhatsAppNumberE164 = req.WhatsAppNumberE164;
            row.LinkedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return new WhatsAppTrackingMappingDto(row.TrackingId, row.OrderId, row.TenantId, row.WhatsAppNumberE164, false);
        }

        var differentNumber = row.WhatsAppNumberE164 != req.WhatsAppNumberE164;
        return new WhatsAppTrackingMappingDto(row.TrackingId, row.OrderId, row.TenantId, row.WhatsAppNumberE164, differentNumber);
    }

    /// <summary>Looks up the tracking row (and linked phone, if any) for an order — used by
    /// the order-status-changed/bill-generated event listeners, which only know the orderId
    /// from the CafePosApi webhook payload, not the TrackingId. WhatsAppNumberE164 is null
    /// (nothing to send to) whenever no customer has scanned/linked yet.</summary>
    [HttpGet("tracking-by-order/{orderId:int}")]
    public async Task<ActionResult<WhatsAppTrackingMappingDto>> GetTrackingByOrder(int orderId)
    {
        var row = await db.WhatsAppTracking.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.OrderId == orderId);
        return row is null ? NotFound() : new WhatsAppTrackingMappingDto(row.TrackingId, row.OrderId, row.TenantId, row.WhatsAppNumberE164, false);
    }

    /// <summary>
    /// Atomically claims up to 10 due jobs for this tenant (Postgres-backed replacement for
    /// BullMQ+Redis — see IWhatsAppEventPublisher's doc comment). A job is claimable when
    /// either: Status=Pending and NextAttemptAt has passed, OR Status=Processing but stuck
    /// there for 2+ minutes (the whatsapp-service crashed mid-send after claiming it last
    /// time — this reclaims it rather than leaking the job forever).
    ///
    /// The claim itself is a single atomic SQL statement:
    /// `UPDATE ... WHERE Id IN (SELECT ... FOR UPDATE SKIP LOCKED) RETURNING *`. This is the
    /// standard Postgres "queue table" idiom — FOR UPDATE SKIP LOCKED means that if two poll
    /// requests ever ran concurrently (e.g. during a future multi-instance deploy), each row
    /// is claimed by exactly one of them; the other silently skips it instead of blocking or
    /// double-claiming. This is what "atomic row claiming" means here — a plain SELECT
    /// followed by a separate UPDATE would have a race window between the two statements.
    /// </summary>
    [HttpGet("queue/pending")]
    public async Task<ActionResult<List<WhatsAppQueueJobDto>>> GetPendingQueueJobs([FromQuery] int tenantId)
    {
        // IgnoreQueryFilters() is required here, not just the usual convention: WhatsAppMessageLog
        // carries a global tenant query filter (ITenantScoped), and EF tries to compose that
        // filter on top of whatever FromSqlRaw returns. That works fine for a plain SELECT, but
        // this raw SQL is an UPDATE...RETURNING — EF can't wrap a subquery filter around a DML
        // statement and throws "non-composable SQL". Safe to skip here since the raw SQL already
        // filters WHERE "TenantId" = {0} explicitly, same as every other query in this controller.
        var claimed = await db.WhatsAppMessageLogs.FromSqlRaw(
            """
            UPDATE "WhatsAppMessageLogs"
            SET "Status" = 'Processing', "UpdatedAt" = NOW()
            WHERE "Id" IN (
                SELECT "Id" FROM "WhatsAppMessageLogs"
                WHERE "TenantId" = {0}
                  AND "Direction" = 'Outbound'
                  AND (
                    ("Status" = 'Pending' AND "NextAttemptAt" <= NOW())
                    OR ("Status" = 'Processing' AND "UpdatedAt" < NOW() - INTERVAL '2 minutes')
                  )
                ORDER BY "NextAttemptAt" NULLS FIRST
                LIMIT 10
                FOR UPDATE SKIP LOCKED
            )
            RETURNING *
            """, tenantId).IgnoreQueryFilters().ToListAsync();

        return claimed.Select(m => new WhatsAppQueueJobDto(m.Id, m.OrderId, m.TrackingId, m.Type.ToString(), m.ToOrFromPhoneE164, m.Body, m.RetryCount)).ToList();
    }

    /// <summary>Mark a claimed job as sent, failed-and-retrying, permanently failed, or
    /// skipped. Called by the Node worker exactly once after processing each job returned by
    /// GetPendingQueueJobs — this is what moves a row out of Processing. "Pending" schedules
    /// a retry with exponential backoff (5s, 10s, 20s... capped at 10min) and gives up
    /// (Status -> Failed) after 8 attempts.</summary>
    [HttpPatch("queue/{jobId:int}")]
    public async Task<IActionResult> UpdateQueueJob(int jobId, [FromBody] UpdateQueueJobRequest req)
    {
        if (!Enum.TryParse<WhatsAppMessageStatus>(req.Status, out var requestedStatus))
            throw new ApiValidationException($"'{req.Status}' isn't a valid queue job status.");

        var job = await db.WhatsAppMessageLogs.IgnoreQueryFilters().FirstOrDefaultAsync(m => m.Id == jobId);
        if (job is null) return NotFound();

        job.UpdatedAt = DateTime.UtcNow;

        if (requestedStatus == WhatsAppMessageStatus.Pending)
        {
            // Retry: increment count and schedule next attempt with exponential backoff.
            job.RetryCount += 1;
            if (job.RetryCount >= 8)
            {
                job.Status = WhatsAppMessageStatus.Failed;
                job.FailureReason = "Exhausted 8 retry attempts: " + (req.FailureReason ?? "unknown");
                job.NextAttemptAt = null;
            }
            else
            {
                job.Status = WhatsAppMessageStatus.Pending;
                var delaySeconds = Math.Min(600, 5 * (int)Math.Pow(2, job.RetryCount - 1));
                job.NextAttemptAt = DateTime.UtcNow.AddSeconds(delaySeconds);
            }
        }
        else
        {
            job.Status = requestedStatus;
            job.FailureReason = req.FailureReason;
            job.NextAttemptAt = null;
        }

        await db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>Mints (or re-mints — it's a deterministic HMAC, cheap either way) the same
    /// short receipt token OrdersController.GetReceiptToken already exposes to staff, reused
    /// as-is for the automatic bill-PDF WhatsApp send. No new PDF logic anywhere.</summary>
    [HttpGet("receipt-token/{orderId:int}")]
    public async Task<ActionResult<object>> GetReceiptToken(int orderId)
    {
        var exists = await db.Orders.IgnoreQueryFilters().AnyAsync(o => o.Id == orderId);
        return exists ? new { token = receiptTokens.Encode(orderId) } : NotFound();
    }

    [HttpGet("sessions/{tenantId:int}")]
    public async Task<ActionResult<WhatsAppSessionDto>> GetSession(int tenantId)
    {
        var session = await db.WhatsAppSessions.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.TenantId == tenantId);
        return session is null ? NotFound() : WhatsAppSessionDto.From(session);
    }

    [HttpPut("sessions/{tenantId:int}")]
    public async Task<ActionResult<WhatsAppSessionDto>> UpsertSession(int tenantId, WhatsAppSessionUpsertRequest req)
    {
        if (!Enum.TryParse<WhatsAppSessionStatus>(req.Status, out var status))
            throw new ApiValidationException($"'{req.Status}' isn't a valid WhatsApp session status.");

        var session = await db.WhatsAppSessions.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.TenantId == tenantId);
        if (session is null)
        {
            session = new WhatsAppSession { TenantId = tenantId };
            db.WhatsAppSessions.Add(session);
        }

        session.Status = status;
        session.ConnectedAt = req.ConnectedAt;
        session.PhoneNumberE164 = req.PhoneNumberE164;
        session.LastQrGeneratedAt = req.LastQrGeneratedAt;
        session.LastDisconnectReason = req.LastDisconnectReason;
        session.UpdatedAt = DateTime.UtcNow;

        // Keep the generic Integrations Hub grid (IntegrationsController/IntegrationsHubScreen)
        // in sync — it has no idea Baileys/Node exists, it just reads Integration.Status like
        // every other card (Zomato, Stripe, ...). See SeedData for the seeded "WhatsApp
        // Business" row this mirrors.
        var integration = await db.Integrations.IgnoreQueryFilters()
            .FirstOrDefaultAsync(i => i.TenantId == tenantId && i.Name == "WhatsApp Business");
        if (integration is not null)
        {
            integration.Status = status == WhatsAppSessionStatus.Connected ? IntegrationStatus.Connected : IntegrationStatus.Disconnected;
            integration.ConnectedAt = status == WhatsAppSessionStatus.Connected ? req.ConnectedAt ?? DateTime.UtcNow : null;
        }

        await db.SaveChangesAsync();
        return WhatsAppSessionDto.From(session);
    }

    /// <summary>Every tenant with a completed Baileys auth-state directory that whatsapp-service
    /// should reconnect to on process boot — see BaileysSessionManager.bootstrapAllSessions.</summary>
    [HttpGet("sessions")]
    public async Task<ActionResult<IEnumerable<int>>> GetReconnectableTenantIds() =>
        Ok(await db.WhatsAppSessions.IgnoreQueryFilters()
            .Where(s => s.Status != WhatsAppSessionStatus.Disconnected)
            .Select(s => s.TenantId)
            .ToListAsync());

    [HttpPost("message-log")]
    public async Task<IActionResult> LogMessage(WhatsAppMessageLogCreateRequest req)
    {
        if (!Enum.TryParse<WhatsAppMessageDirection>(req.Direction, out var direction))
            throw new ApiValidationException($"'{req.Direction}' isn't a valid message direction.");
        if (!Enum.TryParse<WhatsAppMessageType>(req.Type, out var type))
            throw new ApiValidationException($"'{req.Type}' isn't a valid message type.");
        if (!Enum.TryParse<WhatsAppMessageStatus>(req.Status, out var status))
            throw new ApiValidationException($"'{req.Status}' isn't a valid message status.");

        int? trackingRowId = null;
        if (req.TrackingId is not null)
        {
            trackingRowId = await db.WhatsAppTracking.IgnoreQueryFilters()
                .Where(t => t.TrackingId == req.TrackingId)
                .Select(t => (int?)t.Id)
                .FirstOrDefaultAsync();
        }

        db.WhatsAppMessageLogs.Add(new WhatsAppMessageLog
        {
            TenantId = req.TenantId,
            OrderId = req.OrderId,
            TrackingId = trackingRowId,
            Direction = direction,
            Type = type,
            ToOrFromPhoneE164 = req.ToOrFromPhoneE164,
            Body = req.Body,
            Status = status,
            FailureReason = req.FailureReason,
        });
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpGet("settings/{tenantId:int}")]
    public async Task<ActionResult<WhatsAppTenantSettingsDto>> GetTenantSettings(int tenantId)
    {
        var settings = await db.Settings.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.TenantId == tenantId);
        return settings is null ? NotFound() : new WhatsAppTenantSettingsDto(settings.BusinessName, settings.WhatsAppOrderUpdatesEnabled);
    }

    /// <summary>Batch-fetch: exactly the keys Baileys' SignalKeyStore.get() asked for (plus the
    /// single "creds" key at connect time) — see WhatsAppAuthState's doc comment. A POST
    /// because the key list stands in for a query and can run long (a busy tenant accumulates
    /// one Signal session key per contact), which would risk exceeding URL length limits as a
    /// query string.</summary>
    [HttpPost("auth-state/{tenantId:int}/get")]
    public async Task<ActionResult<Dictionary<string, string>>> GetAuthStateEntries(int tenantId, WhatsAppAuthStateGetRequest req)
    {
        if (req.Keys.Count == 0) return new Dictionary<string, string>();
        var rows = await db.WhatsAppAuthStates.IgnoreQueryFilters()
            .Where(a => a.TenantId == tenantId && req.Keys.Contains(a.Key))
            .ToListAsync();
        return rows.ToDictionary(a => a.Key, a => a.Value);
    }

    /// <summary>Upserts/deletes a batch of auth-state entries in one round trip — backs both
    /// Baileys' saveCreds() (a single "creds" entry) and SignalKeyStore.set() (however many
    /// key ids changed in that call).</summary>
    [HttpPut("auth-state/{tenantId:int}")]
    public async Task<IActionResult> SetAuthState(int tenantId, WhatsAppAuthStateSetRequest req)
    {
        if (req.Entries.Count == 0) return NoContent();

        var keys = req.Entries.Keys.ToList();
        var existing = await db.WhatsAppAuthStates.IgnoreQueryFilters()
            .Where(a => a.TenantId == tenantId && keys.Contains(a.Key))
            .ToDictionaryAsync(a => a.Key, a => a);

        foreach (var (key, value) in req.Entries)
        {
            existing.TryGetValue(key, out var row);
            if (value is null)
            {
                if (row is not null) db.WhatsAppAuthStates.Remove(row);
                continue;
            }

            if (row is null)
                db.WhatsAppAuthStates.Add(new WhatsAppAuthState { TenantId = tenantId, Key = key, Value = value });
            else
            {
                row.Value = value;
                row.UpdatedAt = DateTime.UtcNow;
            }
        }

        await db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>Wipes every stored auth-state key for a tenant — called on explicit logout and
    /// on a loggedOut disconnect (see BaileysSessionManager), matching useMultiFileAuthState's
    /// old rmSync(sessionDir) behaviour: the next connect() needs a fresh QR pairing.</summary>
    [HttpDelete("auth-state/{tenantId:int}")]
    public async Task<IActionResult> ClearAuthState(int tenantId)
    {
        var rows = await db.WhatsAppAuthStates.IgnoreQueryFilters().Where(a => a.TenantId == tenantId).ToListAsync();
        if (rows.Count == 0) return NoContent();
        db.WhatsAppAuthStates.RemoveRange(rows);
        await db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>Every tenant with a persisted "creds" row — what whatsapp-service reconnects to
    /// automatically on process boot (see BaileysSessionManager.bootstrapAllSessions). "creds"
    /// is the one key every completed pairing always has, so it stands in for "has an auth
    /// state at all" without needing a separate flag.</summary>
    [HttpGet("auth-state/tenants")]
    public async Task<ActionResult<IEnumerable<int>>> ListTenantsWithAuthState() =>
        Ok(await db.WhatsAppAuthStates.IgnoreQueryFilters()
            .Where(a => a.Key == "creds")
            .Select(a => a.TenantId)
            .ToListAsync());
}
