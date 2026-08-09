using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CafePOS.Api.Infrastructure;

/// <summary>
/// The only thing in CafePosApi that talks to Borzo (borzodelivery.com) — the courier service
/// that puts a real rider on a DELIVERY order. Same shape as WhatsAppNodeClient: a typed
/// HttpClient that keeps one external API behind one seam, so the RN app never holds a Borzo
/// token and every call is made by an already-authenticated, already-tenant-scoped request.
///
/// Two things about Borzo drive the design here:
///
///  - Auth and environment are per CAFE, not per deployment. Each tenant brings its own
///    X-DV-Auth-Token and its own balance, and can sit on sandbox or production independently
///    (CafeSettings.BorzoAuthToken / BorzoUseTestEnvironment). So neither the base address nor
///    the auth header can be baked into the registration — both ride along on every call.
///  - Borzo answers 200 OK for business failures too. `is_successful: false` with an `errors`
///    array is the normal way to be told an order is invalid, so HTTP status alone says almost
///    nothing; every method here reads the body's own success flag.
///
/// Prices are quoted through Calculate before anything is booked. That call charges nothing and
/// creates nothing, which is what lets the app show a real fee before the cafe commits.
/// </summary>
public class BorzoClient(HttpClient http, ILogger<BorzoClient> logger)
{
    private const string ProductionBaseUrl = "https://robot-in.borzodelivery.com/api/business/1.6";
    private const string TestBaseUrl = "https://robotapitest-in.borzodelivery.com/api/business/1.6";
    private const string AuthHeader = "X-DV-Auth-Token";

    /// <summary>Motorbike. The only sensible vehicle for a cafe order, and the default Borzo's
    /// own examples use — a car costs several times more to deliver one bag of food.</summary>
    private const int MotorbikeVehicleTypeId = 8;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static string BaseUrl(bool useTest) => useTest ? TestBaseUrl : ProductionBaseUrl;

    /// <summary>
    /// Prices a delivery without creating anything. Borzo returns a price even when the request
    /// has problems — those come back in `warnings` rather than `errors` — so a quote is always
    /// an estimate to show, never a promise that the order will place.
    /// </summary>
    public Task<BorzoResult> CalculateAsync(BorzoBooking booking, CancellationToken ct = default) =>
        PostAsync("calculate-order", BuildOrderPayload(booking, includeVehicle: false), booking, ct);

    /// <summary>
    /// Places the order for real: from here a rider is assigned and the cafe is charged. The
    /// pickup point carries `required_start_datetime`, which is how the kitchen's prep time
    /// reaches Borzo — it's the difference between a rider idling at the counter for twenty
    /// minutes and one arriving as the food is bagged.
    /// </summary>
    public Task<BorzoResult> CreateAsync(BorzoBooking booking, CancellationToken ct = default) =>
        PostAsync("create-order", BuildOrderPayload(booking, includeVehicle: true), booking, ct);

    /// <summary>Cancels a booked order. Borzo refuses once a rider has visited any point, and
    /// says so through is_successful/errors rather than an HTTP error.</summary>
    public Task<BorzoResult> CancelAsync(string? authToken, bool useTest, string courierOrderId, CancellationToken ct = default) =>
        PostAsync(
            "cancel-order",
            new Dictionary<string, object?> { ["order_id"] = courierOrderId },
            new BorzoBooking { AuthToken = authToken, UseTestEnvironment = useTest },
            ct);

    private Dictionary<string, object?> BuildOrderPayload(BorzoBooking booking, bool includeVehicle)
    {
        var pickup = new Dictionary<string, object?>
        {
            ["address"] = booking.PickupAddress,
            ["latitude"] = booking.PickupLatitude,
            ["longitude"] = booking.PickupLongitude,
            ["contact_person"] = new Dictionary<string, object?>
            {
                ["name"] = booking.PickupContactName,
                ["phone"] = booking.PickupContactPhone,
            },
            ["client_order_id"] = booking.ClientOrderId,
        };

        // Only on create: Borzo rejects required_start_datetime on some order types, and a quote
        // has no use for it anyway — the price doesn't depend on when the rider turns up.
        if (includeVehicle && booking.PickupReadyAt is DateTimeOffset readyAt)
            pickup["required_start_datetime"] = readyAt.ToString("yyyy-MM-ddTHH:mm:sszzz");

        var dropoff = new Dictionary<string, object?>
        {
            ["address"] = booking.DropoffAddress,
            ["latitude"] = booking.DropoffLatitude,
            ["longitude"] = booking.DropoffLongitude,
            ["contact_person"] = new Dictionary<string, object?>
            {
                ["name"] = booking.DropoffContactName,
                ["phone"] = booking.DropoffContactPhone,
            },
            ["client_order_id"] = booking.ClientOrderId,
        };

        // Cash to collect from the customer at the door. Borzo charges its own percentage on top
        // for handling it, which is why this is only ever set for genuinely unpaid orders.
        if (booking.CashToCollect is decimal cod && cod > 0)
        {
            dropoff["taking_amount"] = cod.ToString("0.00");
            dropoff["is_cod_cash_voucher_required"] = true;
        }

        var payload = new Dictionary<string, object?>
        {
            ["type"] = "standard",
            ["matter"] = booking.Matter,
            ["total_weight_kg"] = booking.TotalWeightKg.ToString("0.##"),
            ["is_client_notification_enabled"] = true,
            ["is_contact_person_notification_enabled"] = true,
            ["points"] = new[] { pickup, dropoff },
        };
        if (includeVehicle) payload["vehicle_type_id"] = MotorbikeVehicleTypeId;

        return payload;
    }

    private async Task<BorzoResult> PostAsync(
        string method, Dictionary<string, object?> payload, BorzoBooking booking, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(booking.AuthToken))
            return BorzoResult.Failure("No Borzo token saved. Add one in Integrations → Delivery Partner.");

        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"{BaseUrl(booking.UseTestEnvironment)}/{method}")
        {
            Content = JsonContent.Create(payload, options: JsonOptions),
        };
        request.Headers.Add(AuthHeader, booking.AuthToken);

        BorzoResponse? body;
        try
        {
            using var response = await http.SendAsync(request, ct);
            // Read the body before looking at the status: Borzo puts the actual reason
            // (invalid_auth_token, requests_limit_exceeded, insufficient_balance…) in there even
            // on a 4xx, and "401 Unauthorized" alone would tell the cafe nothing useful.
            body = await response.Content.ReadFromJsonAsync<BorzoResponse>(JsonOptions, ct);
            if (body is null)
                return BorzoResult.Failure($"Borzo returned an empty response ({(int)response.StatusCode}).");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(ex, "Borzo {Method} call failed", method);
            return BorzoResult.Failure("Couldn’t reach the delivery partner. Check the internet connection and try again.");
        }

        // The exact fields Borzo objected to (pickup vs dropoff, address vs phone vs coordinates)
        // live in parameter_warnings/parameter_errors, not the generic code — logged in full so a
        // "couldn't use the details" report can always be traced to the actual missing field.
        var detail = FlattenParameters(body.ParameterErrors) ?? FlattenParameters(body.ParameterWarnings);
        if (detail is not null)
            logger.LogWarning("Borzo {Method} parameter issues: {Detail}", method, detail);

        if (!body.IsSuccessful)
            return BorzoResult.Failure(WithDetail(DescribeErrors(body.Errors) ?? "The delivery partner rejected this order.", detail));

        return BorzoResult.Success(body.Order, WithDetail(DescribeErrors(body.Warnings), detail));
    }

    private static string? WithDetail(string? message, string? detail)
    {
        if (detail is null) return message;
        return message is null ? $"Missing/invalid: {detail}" : $"{message} ({detail})";
    }

    /// <summary>Flattens Borzo's nested parameter_warnings/parameter_errors into a compact
    /// "points[0].contact_person.phone: required" list a person can read, walking whatever tree
    /// shape came back rather than assuming one — the structure differs by which field failed.</summary>
    private static string? FlattenParameters(JsonElement? node)
    {
        if (node is not JsonElement el || el.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        var parts = new List<string>();
        Walk(el, "", parts);
        return parts.Count == 0 ? null : string.Join("; ", parts);

        static void Walk(JsonElement e, string path, List<string> acc)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var p in e.EnumerateObject())
                        Walk(p.Value, path.Length == 0 ? p.Name : $"{path}.{p.Name}", acc);
                    break;
                case JsonValueKind.Array:
                    var i = 0;
                    foreach (var item in e.EnumerateArray())
                    {
                        // A leaf array of strings ("required") is the error list itself, not more nesting.
                        if (item.ValueKind is JsonValueKind.String) acc.Add($"{path}: {item.GetString()}");
                        else Walk(item, $"{path}[{i}]", acc);
                        i++;
                    }
                    break;
                case JsonValueKind.String:
                    acc.Add($"{path}: {e.GetString()}");
                    break;
            }
        }
    }

    /// <summary>Turns Borzo's error codes into something a cashier can act on. Anything not
    /// listed is passed through as-is rather than swallowed — an unfamiliar code is still more
    /// useful on screen than "something went wrong".</summary>
    private static string? DescribeErrors(List<string>? codes)
    {
        if (codes is null || codes.Count == 0) return null;
        return string.Join(" ", codes.Select(code => code switch
        {
            "invalid_auth_token" or "required_auth_token" => "The Borzo token is missing or wrong — re-paste it in Integrations.",
            "insufficient_balance" => "Not enough balance in the Borzo account.",
            "requests_limit_exceeded" => "Too many requests to Borzo just now — wait a minute and retry.",
            "invalid_parameters" => "Borzo couldn’t use the pickup or delivery details (address or location may be missing).",
            "route_not_found" => "Borzo couldn’t find a route — the delivery address may be outside their service area.",
            "order_cannot_be_canceled" => "Too late to cancel — the rider has already started.",
            "unapproved_contract" => "The Borzo account isn’t approved for orders yet.",
            "service_unavailable" => "Borzo is temporarily unavailable. Try again shortly.",
            _ => code,
        }));
    }
}

/// <summary>Everything one Borzo call needs, gathered by the caller so this client never has to
/// reach for a DbContext or know what a tenant is.</summary>
public class BorzoBooking
{
    public required string? AuthToken { get; init; }
    public bool UseTestEnvironment { get; init; } = true;

    public string? PickupAddress { get; init; }
    public decimal? PickupLatitude { get; init; }
    public decimal? PickupLongitude { get; init; }
    public string? PickupContactName { get; init; }
    public string? PickupContactPhone { get; init; }
    /// <summary>When the food will be bagged and ready — becomes the pickup point's
    /// required_start_datetime so the rider is timed to the kitchen, not to the button press.</summary>
    public DateTimeOffset? PickupReadyAt { get; init; }

    public string? DropoffAddress { get; init; }
    public decimal? DropoffLatitude { get; init; }
    public decimal? DropoffLongitude { get; init; }
    public string? DropoffContactName { get; init; }
    public string? DropoffContactPhone { get; init; }

    /// <summary>The cafe's own order id, echoed back on every Borzo record and callback — the
    /// one thread tying a Borzo delivery to a PrabandhOS order.</summary>
    public string? ClientOrderId { get; init; }
    public string Matter { get; init; } = "Food";
    public decimal TotalWeightKg { get; init; } = 2;
    /// <summary>Cash the rider must collect at the door, for orders not already paid.</summary>
    public decimal? CashToCollect { get; init; }
}

/// <summary>Outcome of one Borzo call, already reduced to what a caller can act on: whether it
/// worked, what to say if it didn't, and the order Borzo returned if it did.</summary>
public record BorzoResult(bool Ok, string? Message, BorzoOrder? Order)
{
    public static BorzoResult Failure(string message) => new(false, message, null);
    public static BorzoResult Success(BorzoOrder? order, string? warning) => new(true, warning, order);

    /// <summary>What the courier charges for this delivery, all in — Borzo's payment_amount
    /// already folds in the COD and insurance fees on top of the base delivery fee.</summary>
    public decimal? Fee => Decimal.TryParse(Order?.PaymentAmount, out var amount) ? amount : null;

    /// <summary>Customer-facing tracking link. Borzo mints one per point; the drop-off is the one
    /// worth sending, since the pickup link only tracks the rider to the cafe.</summary>
    public string? TrackingUrl => Order?.Points?.LastOrDefault()?.TrackingUrl;
}

// Only the response fields actually used are declared; Borzo returns a great deal more per order.
public record BorzoResponse
{
    [JsonPropertyName("is_successful")] public bool IsSuccessful { get; init; }
    [JsonPropertyName("order")] public BorzoOrder? Order { get; init; }
    [JsonPropertyName("errors")] public List<string>? Errors { get; init; }
    [JsonPropertyName("warnings")] public List<string>? Warnings { get; init; }
    /// <summary>Per-field detail behind an invalid_parameters warning/error — a nested,
    /// heterogeneous shape (points[].address, points[].contact_person.phone, …) that isn't worth
    /// a strongly-typed model, so it's kept raw and flattened to a readable string on demand.</summary>
    [JsonPropertyName("parameter_warnings")] public JsonElement? ParameterWarnings { get; init; }
    [JsonPropertyName("parameter_errors")] public JsonElement? ParameterErrors { get; init; }
}

public record BorzoOrder
{
    [JsonPropertyName("order_id")] public long? OrderId { get; init; }
    [JsonPropertyName("order_name")] public string? OrderName { get; init; }
    [JsonPropertyName("status")] public string? Status { get; init; }
    [JsonPropertyName("status_description")] public string? StatusDescription { get; init; }
    /// <summary>Decimal-as-string, exactly as Borzo sends it ("55.00") — parsed at the edge in
    /// BorzoResult.Fee rather than trusting a JSON number that isn't one.</summary>
    [JsonPropertyName("payment_amount")] public string? PaymentAmount { get; init; }
    [JsonPropertyName("delivery_fee_amount")] public string? DeliveryFeeAmount { get; init; }
    [JsonPropertyName("points")] public List<BorzoPoint>? Points { get; init; }
    [JsonPropertyName("courier")] public BorzoCourier? Courier { get; init; }
}

public record BorzoPoint
{
    [JsonPropertyName("tracking_url")] public string? TrackingUrl { get; init; }
    [JsonPropertyName("client_order_id")] public string? ClientOrderId { get; init; }
}

public record BorzoCourier
{
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("surname")] public string? Surname { get; init; }
    [JsonPropertyName("phone")] public string? Phone { get; init; }
    [JsonPropertyName("latitude")] public string? Latitude { get; init; }
    [JsonPropertyName("longitude")] public string? Longitude { get; init; }

    public string? FullName => string.Join(" ", new[] { Name, Surname }.Where(s => !string.IsNullOrWhiteSpace(s)));
}
