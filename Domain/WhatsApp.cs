namespace CafePOS.Api.Domain;

public enum WhatsAppSessionStatus { Disconnected, Connecting, Connected, Error }

/// <summary>One row per tenant — mirrors (not replaces) the generic Integration row so the
/// Integrations Hub grid keeps working unmodified (see SeedData's "WhatsApp Business" row);
/// this table holds the WhatsApp-specific detail (phone number, Baileys connection state)
/// that Integration has no columns for. Written by the Node whatsapp-service via
/// WhatsAppInternalController as its Baileys connection state changes.</summary>
public class WhatsAppSession : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public WhatsAppSessionStatus Status { get; set; } = WhatsAppSessionStatus.Disconnected;
    /// <summary>The connected business number, digits only, e.g. "91XXXXXXXXXX".</summary>
    public string? PhoneNumberE164 { get; set; }
    public DateTime? ConnectedAt { get; set; }
    public DateTime? LastQrGeneratedAt { get; set; }
    public string? LastDisconnectReason { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Baileys' Signal auth state (creds + protocol keys) for a tenant's WhatsApp
/// connection, persisted here instead of local disk — the same fix as DataProtectionKey
/// (see CafePosDbContext's doc comment on it): free-tier hosting's container filesystem is
/// thrown away on every redeploy, so a file-based session forced the Owner to re-scan the
/// QR after every deploy. One row per (TenantId, Key); Key is Baileys' own naming scheme
/// ("creds" for AuthenticationCreds, "session-&lt;id&gt;"/"sender-key-&lt;id&gt;"/etc for Signal
/// protocol keys — see useMultiFileAuthState, which this mirrors one file-per-row). Value is
/// the same BufferJSON-serialized JSON string Baileys would have written to that file.
/// Written/read only by WhatsAppInternalController's auth-state endpoints — see
/// whatsapp-service/src/baileys/authStateStore.ts, the only caller on the Node side.</summary>
public class WhatsAppAuthState : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public required string Key { get; set; }
    public required string Value { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>The idempotent TrackingId &lt;-&gt; Order &lt;-&gt; WhatsApp number mapping. Created
/// (WhatsAppNumberE164 == null) the moment a QSR token slip is about to print (see
/// OrdersController.GetOrCreateWhatsAppTracking); filled in on the customer's first inbound
/// TRACK message (see WhatsAppInternalController.UpsertTrackingMapping) — never a second row
/// per order, never overwritten once linked to a different number.</summary>
public class WhatsAppOrderTracking : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public int OrderId { get; set; }
    /// <summary>Short, message-safe id embedded in the printed QR's wa.me deep link,
    /// e.g. "A9F7D12KX" — generated once, at first print.</summary>
    public required string TrackingId { get; set; }
    /// <summary>Null until the customer's first inbound WhatsApp message links it. Digits
    /// only, matching the existing wa.me/91... convention (e.g. "91XXXXXXXXXX").</summary>
    public string? WhatsAppNumberE164 { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LinkedAt { get; set; }
}

public enum WhatsAppMessageDirection { Outbound, Inbound }
public enum WhatsAppMessageType { StatusUpdate, BillPdf, TrackReply, StatusReply, HelpReply, InboundCommand }

/// <summary>
/// Outbound job queue — the single source of truth for message delivery state. The
/// whatsapp-service polls this table for `Pending` rows due for retry, sends via Baileys,
/// and updates the row to Sent/Failed/Skipped. This is both the audit log AND the queue —
/// a dual-duty that keeps schema simple and ensures "what we tried to send" is permanently
/// recorded without a separate table. For inbound messages (WhatsApp → us), the status is
/// recorded once and never changes (no retry logic — a received message is received).
/// </summary>
public enum WhatsAppMessageStatus { Pending, Processing, Sent, Failed, Skipped }

/// <summary>Audit trail + job queue for outbound/inbound WhatsApp messages. For outbound:
/// Pending = waiting to send (or pending retry), Processing = claimed by a poll cycle and
/// in flight (see WhatsAppInternalController.GetPendingQueueJobs' atomic
/// UPDATE...FOR UPDATE SKIP LOCKED claim — this is what prevents two overlapping polls from
/// double-sending the same row), Sent = delivered, Failed = gave up (exhausted retries),
/// Skipped = nothing to do (updates disabled, or no phone linked yet). A row stuck in
/// Processing for more than 2 minutes (Node crashed mid-send) is reclaimed by the next poll
/// as if it were Pending again. For inbound: always Sent once logged (no retry).</summary>
public class WhatsAppMessageLog : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public int? OrderId { get; set; }
    public int? TrackingId { get; set; }
    public WhatsAppMessageDirection Direction { get; set; }
    public WhatsAppMessageType Type { get; set; }
    public string? ToOrFromPhoneE164 { get; set; }
    public string? Body { get; set; }
    public WhatsAppMessageStatus Status { get; set; } = WhatsAppMessageStatus.Pending;
    /// <summary>Number of send attempts made so far (incremented each retry, capped at 8).</summary>
    public int RetryCount { get; set; } = 0;
    /// <summary>When to next attempt sending this (Pending rows only). NULL if Sent, Skipped,
    /// or Failed with no more retries.</summary>
    public DateTime? NextAttemptAt { get; set; } = DateTime.UtcNow;
    /// <summary>Why the last send attempt failed (only populated if Status = Failed or on a
    /// pending row after a failed attempt, before the row is claimed for retry).</summary>
    public string? FailureReason { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
