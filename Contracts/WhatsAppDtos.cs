using CafePOS.Api.Domain;

namespace CafePOS.Api.Contracts;

// ---------- Internal API (whatsapp-service -> CafePosApi, ServiceOnly-authed) ----------

public record WhatsAppResolveTenantDto(int TenantId, int OrderId, string TrackingId);

/// <summary>Live order snapshot for TRACK/STATUS replies and the "New order → Preparing/Ready"
/// automatic push — StatusLabel is already mapped to the customer-facing word
/// (Pending/Preparing/Prepared/Served), never the raw OrderStatus name.</summary>
/// <summary>TokenNumber/TableCode are mutually exclusive (see Order.TokenNumber's doc
/// comment) — exactly one is non-null depending on OrderType, so whatsapp-service's
/// message templates can label the order correctly either way.</summary>
public record WhatsAppOrderStatusDto(int TenantId, int OrderId, int? TokenNumber, string? TableCode, string StatusLabel, bool Paid, string RestaurantName, string TrackingId)
{
    public static string ToStatusLabel(OrderStatus status) => status switch
    {
        OrderStatus.New => "Pending",
        OrderStatus.Preparing => "Preparing",
        OrderStatus.Ready => "Prepared",
        OrderStatus.Served => "Served",
        _ => status.ToString(),
    };
}

public record WhatsAppTrackingMappingRequest(int TenantId, string TrackingId, string WhatsAppNumberE164);

/// <summary>AlreadyLinkedToDifferentNumber lets the TRACK command reply "This token is already
/// linked to another number" instead of silently reassigning it — see
/// WhatsAppOrderTracking's doc comment for the idempotency guarantee this enforces.</summary>
public record WhatsAppTrackingMappingDto(string TrackingId, int OrderId, int TenantId, string? WhatsAppNumberE164, bool AlreadyLinkedToDifferentNumber);

public record WhatsAppSessionUpsertRequest(string Status, DateTime? ConnectedAt, string? PhoneNumberE164, DateTime? LastQrGeneratedAt, string? LastDisconnectReason);

public record WhatsAppSessionDto(int TenantId, string Status, string? PhoneNumberE164, DateTime? ConnectedAt, DateTime? LastQrGeneratedAt)
{
    public static WhatsAppSessionDto From(WhatsAppSession s) => new(s.TenantId, s.Status.ToString(), s.PhoneNumberE164, s.ConnectedAt, s.LastQrGeneratedAt);
}

public record WhatsAppMessageLogCreateRequest(
    int TenantId, int? OrderId, string? TrackingId, string Direction, string Type, string? ToOrFromPhoneE164, string? Body, string Status, string? FailureReason);

/// <summary>Queue job status update — called by whatsapp-service after processing a job from
/// /api/internal/whatsapp/queue/pending. Status can be "Sent" (succeeded), "Pending" (failed,
/// retry scheduled), or "Skipped" (updates disabled). The endpoint calculates retry backoff
/// and NextAttemptAt based on Status and RetryCount.</summary>
public record UpdateQueueJobRequest(string Status, string? FailureReason = null);

/// <summary>A single claimed queue job — Type is the string name of WhatsAppMessageType
/// (only "StatusUpdate"/"BillPdf" are ever enqueued by CafePosApi itself; see
/// WhatsAppEventPublisher). ToOrFromPhoneE164/Body are null at enqueue time — the Node worker
/// resolves the linked phone, live order status, and message body itself when it processes
/// the job (see whatsapp-service/src/queue/pollingWorker.ts), since the "who" and "current
/// status" can only be known at send time, not when the underlying order event happened.</summary>
public record WhatsAppQueueJobDto(int Id, int? OrderId, int? TrackingId, string Type, string? ToOrFromPhoneE164, string? Body, int RetryCount);

public record WhatsAppTenantSettingsDto(string BusinessName, bool UpdatesEnabled);

// ---------- Staff-facing API (RN app -> CafePosApi, JWT-authed) ----------

public record WhatsAppStaffStatusDto(string Status, string? PhoneNumberE164, DateTime? ConnectedAt, bool UpdatesEnabled);
public record WhatsAppPairResponseDto(string? QrDataUrl, string Status);
public record UpdateWhatsAppSettingsRequest(bool UpdatesEnabled);
