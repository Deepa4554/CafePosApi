using System.Text.Json;
using System.Text.Json.Serialization;

namespace CafePOS.Api.Contracts;

// The wire shapes of the Dyno bridge's webhook contract (see DynoWebhookController).
//
// These are dictated by a third party, not by us: Dyno's Electron client builds every call in
// its own utils.js/routes.js, so the property names, the status integers and even the response
// bodies it insists on reading back are fixed points we have to match exactly. Where a name
// looks unidiomatic for C# (camelCase `orderId`, `resId`, `stockStatus`) that is why — the
// JsonSerializer here is configured camelCase anyway, but the names are pinned with
// [JsonPropertyName] regardless so a future serializer-wide setting change can't silently break
// the integration.

// ---------- Inbound: orders Dyno has picked up from an aggregator ----------

/// <summary>Body of `POST {base}/orders`. Dyno batches whatever it found on its 40s sweep.</summary>
public class DynoOrdersEnvelope
{
    [JsonPropertyName("orders")]
    public List<DynoOrder> Orders { get; set; } = [];
}

public class DynoOrder
{
    [JsonPropertyName("orderId")]
    public string? OrderId { get; set; }

    /// <summary>The aggregator outlet this order belongs to — matched against the tenant's
    /// configured Zomato/Swiggy restaurant id.</summary>
    [JsonPropertyName("resId")]
    public string? ResId { get; set; }

    /// <summary>"zomato" or "swiggy", lowercase, as Dyno spells it.</summary>
    [JsonPropertyName("vendor")]
    public string? Vendor { get; set; }

    /// <summary>Dyno's own status label for the order. Free text from the aggregator's
    /// vocabulary, kept as a string for the same reason Order.CourierStatus is.</summary>
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    /// <summary>The aggregator's raw order document, passed through untouched.
    ///
    /// Deliberately left as a <see cref="JsonElement"/> rather than a typed model: Dyno's OpenAPI
    /// spec declares every response as an untyped `{}` (FastAPI with no response_model), so the
    /// real shape is only knowable from a live order, and it differs between Zomato and Swiggy.
    /// <see cref="Infrastructure.DynoOrderParser"/> is the single place that interprets it, and
    /// the whole document is archived on the created order so a parse that guessed wrong can be
    /// re-run later against data we actually kept.</summary>
    [JsonPropertyName("data")]
    public JsonElement Data { get; set; }
}

// ---------- Outbound: what we tell Dyno to do next ----------

/// <summary>Response to `GET {base}/{resId}/orders/status` — the pending-command feed Dyno drains
/// every 30s. Dyno reads <c>orders</c>; if <c>orderHistory</c> is true it additionally fetches the
/// outlet's order history and posts it back.</summary>
public class DynoOrderCommandFeed
{
    [JsonPropertyName("orders")]
    public List<DynoOrderCommand> Orders { get; set; } = [];

    [JsonPropertyName("orderHistory")]
    public bool OrderHistory { get; set; }
}

/// <summary>One instruction for one aggregator order.
///
/// <see cref="Status"/> is Dyno's command vocabulary, and the numbers are not ours to choose (see
/// its utils.js updateOrderStatus): <c>1</c> = accept this order, <c>3</c> = mark it ready,
/// <c>-1</c> = reject it (Zomato only — Dyno has no Swiggy reject path). Anything else is
/// ignored by the bridge.</summary>
public class DynoOrderCommand
{
    [JsonPropertyName("orderId")]
    public required string OrderId { get; set; }

    [JsonPropertyName("status")]
    public int Status { get; set; }

    /// <summary>Minutes promised to the customer when accepting. Only read for an accept;
    /// Dyno substitutes 30 if we omit it.</summary>
    [JsonPropertyName("prepTime")]
    public int? PrepTime { get; set; }
}

/// <summary>Body of `POST {base}/orders/{orderId}/status` — Dyno reporting that it carried out a
/// command. <see cref="StatusCode"/> is the OUTCOME code, which is deliberately different from the
/// command code that caused it: <c>2</c> = accepted (from command 1), <c>4</c> = ready (from 3),
/// <c>-2</c> = rejected (from -1).</summary>
public class DynoOrderStatusAck
{
    [JsonPropertyName("statusCode")]
    public int StatusCode { get; set; }

    /// <summary>Whatever the aggregator's API answered, for the audit trail.</summary>
    [JsonPropertyName("statusResponse")]
    public JsonElement StatusResponse { get; set; }
}

// ---------- Stock / menu ----------

/// <summary>Response to `GET {base}/{resId}/items` — availability changes waiting to be pushed to
/// the aggregator, plus an optional request for the outlet's full menu.</summary>
public class DynoItemFeed
{
    [JsonPropertyName("items")]
    public List<DynoStockEntry> Items { get; set; } = [];

    [JsonPropertyName("categories")]
    public List<DynoStockEntry> Categories { get; set; } = [];

    /// <summary>Ask Dyno to dump the outlet's entire menu back to us (it answers on
    /// `POST {base}/{resId}/items`). Set only while we have no catalog for the outlet — it costs
    /// a full aggregator menu fetch, so it must not be left on.</summary>
    [JsonPropertyName("getAllItems")]
    public bool GetAllItems { get; set; }
}

/// <summary>One in/out-of-stock instruction. <see cref="Id"/> is the aggregator's own item or
/// category id; <see cref="StockStatus"/> true means purchasable.</summary>
public class DynoStockEntry
{
    [JsonPropertyName("id")]
    public required string Id { get; set; }

    [JsonPropertyName("stockStatus")]
    public bool StockStatus { get; set; }
}

/// <summary>Body of `POST {base}/{resId}/items/status` and `.../categories/status` — Dyno
/// confirming it applied one stock change.</summary>
public class DynoStockAck
{
    [JsonPropertyName("entityId")]
    public string? EntityId { get; set; }

    [JsonPropertyName("aggregator")]
    public string? Aggregator { get; set; }

    [JsonPropertyName("stockStatus")]
    public bool StockStatus { get; set; }

    [JsonPropertyName("isProcessed")]
    public bool IsProcessed { get; set; }

    [JsonPropertyName("statusResponse")]
    public JsonElement StatusResponse { get; set; }
}

/// <summary>Body of `POST {base}/{resId}/items` (the full-menu dump) and
/// `POST {base}/{resId}/orders/history`. Both arrive in the same envelope shape, with the
/// aggregator's untyped payload under <c>statusResponse</c>.</summary>
public class DynoBulkPayload
{
    [JsonPropertyName("status")]
    public bool Status { get; set; }

    [JsonPropertyName("statusResponse")]
    public JsonElement StatusResponse { get; set; }
}

// ---------- Owner-facing setup & mapping (DynoAdminController) ----------

/// <param name="WebhookUrl">The full URL to paste into the cafe's Dyno install, token included.
/// Null until the bridge has been enabled once.</param>
/// <param name="LastContactAt">When the bridge last called in — the only evidence a cafe has that
/// its Windows box is still running and still logged into the aggregators.</param>
/// <param name="Ready">Whether an incoming webhook call would actually be accepted: enabled, has a
/// token, and has at least one outlet id to match orders against.</param>
public record DynoSettingsDto(
    bool Enabled,
    string? ZomatoRestaurantId,
    string? SwiggyRestaurantId,
    int DefaultPrepTimeMins,
    bool AutoAccept,
    DateTime? LastContactAt,
    string? WebhookUrl,
    bool Ready);

/// <summary>Every field is optional — null means "not editing this one in this save", the same
/// convention UpdateBorzoSettingsRequest uses.</summary>
public record UpdateDynoSettingsRequest(
    bool? Enabled = null,
    string? ZomatoRestaurantId = null,
    string? SwiggyRestaurantId = null,
    int? DefaultPrepTimeMins = null,
    bool? AutoAccept = null);

/// <param name="SuggestedMenuItemId">A name-match hint for an unmapped row, or null when nothing
/// matched unambiguously. Only ever a suggestion — nothing is linked until someone confirms it.</param>
public record PlatformCatalogRowDto(
    string Provider,
    string ResId,
    string PlatformEntityId,
    string Name,
    bool IsCategory,
    decimal? PlatformPrice,
    int? MappedMenuItemId,
    string? MappedMenuItemName,
    int? SuggestedMenuItemId);

/// <summary>A null <paramref name="MenuItemId"/> removes the mapping.</summary>
public record SetPlatformMappingRequest(
    string Provider,
    string ResId,
    string PlatformEntityId,
    bool IsCategory,
    int? MenuItemId);

public record UnmatchedPlatformOrderDto(
    int OrderId,
    string Provider,
    string PlatformOrderId,
    DateTime ReceivedAt);

/// <summary>The `{ "status": n }` body Dyno checks after almost every call it makes to us.
///
/// This is load-bearing and easy to get wrong: for order-status acks Dyno compares the number it
/// reads back against the code it sent and logs a failure unless they match, and for the bulk
/// endpoints it wants exactly 200. Returning HTTP 200 with a body it can't parse counts as a
/// failure on its side, so every handler answers with this.</summary>
public record DynoAck([property: JsonPropertyName("status")] int Status);
