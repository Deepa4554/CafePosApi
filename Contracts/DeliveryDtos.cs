namespace CafePOS.Api.Contracts;

/// <summary>
/// Contracts for third-party courier booking (see DeliveryController / BorzoClient). Kept out of
/// Dtos.cs for the same reason ReportsDtos and ManagementDtos are — one area, one file.
/// </summary>

/// <summary>What the cafe has configured, for the settings screen. Deliberately reports whether a
/// token is saved rather than returning it: the token is a spendable credential, and a screen
/// that displays it is a screen that leaks it over anyone's shoulder.</summary>
public record BorzoSettingsDto(
    bool Enabled,
    bool HasAuthToken,
    bool UseTestEnvironment,
    bool PassFeeToCustomer,
    string? PickupAddress,
    decimal? PickupLatitude,
    decimal? PickupLongitude,
    /// <summary>False when something required is still missing (no token, or no pickup pin), so
    /// the UI can say what's blocking instead of failing at booking time.</summary>
    bool ReadyToBook);

/// <summary>Every field optional — the settings screen saves one section at a time, and null
/// means "leave as is". Sending an empty AuthToken clears the saved one.</summary>
public record UpdateBorzoSettingsRequest(
    bool? Enabled,
    string? AuthToken,
    bool? UseTestEnvironment,
    bool? PassFeeToCustomer,
    string? PickupAddress,
    decimal? PickupLatitude,
    decimal? PickupLongitude);

/// <summary>A price for a delivery that hasn't been booked. Fee is what the courier will charge
/// the cafe; PassedToCustomer says whether it's going on the customer's bill.</summary>
public record DeliveryQuoteDto(
    bool Ok,
    decimal? Fee,
    bool PassedToCustomer,
    /// <summary>Set when the quote came back with caveats, or couldn't be produced at all —
    /// shown as-is to staff.</summary>
    string? Message);

/// <summary>Book a rider for an order. PrepMinutes is the kitchen's own estimate and becomes the
/// courier's pickup time, so the rider arrives as the food is bagged rather than waiting.</summary>
public record BookRiderRequest(int PrepMinutes);

/// <summary>An order placed by a customer from the delivery QR (see
/// PublicController.CreateDeliveryOrder). Latitude/Longitude come from the browser's own
/// geolocation and are null when the customer declined to share it — the order still stands,
/// it just can't be handed to a courier automatically.</summary>
public record CreateDeliveryOrderRequest(
    string? GuestName,
    string? GuestPhone,
    string? Address,
    decimal? Latitude,
    decimal? Longitude,
    List<CreateOrderItemDto> Items);

/// <summary>Live courier state for one order, for the delivery screen.</summary>
public record DeliveryStatusDto(
    int OrderId,
    /// <summary>Where the order is going, and whether the customer shared a map location with it.
    /// Carried here rather than on OrderDto so the delivery screen has everything it needs from
    /// one call — and because an order's destination is only ever interesting for a delivery.</summary>
    string? DeliveryAddress,
    bool HasLocation,
    string? Provider,
    string? CourierOrderId,
    string? Status,
    string? TrackingUrl,
    decimal? Fee,
    string? RiderName,
    string? RiderPhone,
    DateTime? BookedAt);
