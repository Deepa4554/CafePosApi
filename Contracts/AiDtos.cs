namespace CafePOS.Api.Contracts;

// ---------- AI Chat ----------

/// <summary>Role is "user" or "assistant" — matches the chat bubble side, translated
/// to Gemini's "user"/"model" roles inside GeminiService.</summary>
public record AiChatMessageDto(string Role, string Text);

public record AiChatRequest(List<AiChatMessageDto> History, string Message);

public record AiChatResponseDto(string Reply);

// ---------- Menu Import from Photo ----------

/// <summary>ImageDataUri is a full data URI ("data:image/jpeg;base64,...") — same shape
/// the app already stores directly in PhotoUrl/LogoUrl columns, so the client can just
/// pass through whatever pickImageAsDataUri() returned with no extra parsing.</summary>
public record ImportMenuFromImageRequest(string ImageDataUri);

/// <summary>Mirrors CreateMenuItemRequest's shape (minus Icon/Image/ProductType, which a
/// photo can't tell us) so the client can feed the result straight into /menu-items/bulk
/// with no transform — the same pattern CSV import already uses.</summary>
public record ExtractedMenuItemDto(string Name, string Category, decimal Price, string? Subtitle);

// ---------- Sales Forecast ----------

public record ForecastPointDto(string Date, decimal PredictedRevenue);

public record SalesForecastDto(List<ForecastPointDto> Forecast, string Method);

// ---------- Shift Optimization ----------

public record HourWindowStaffingDto(string Label, int OrderCount, int StaffScheduled, double OrdersPerStaff, string Status);

public record ShiftOptimizationDto(List<HourWindowStaffingDto> Windows, List<string> Suggestions);
