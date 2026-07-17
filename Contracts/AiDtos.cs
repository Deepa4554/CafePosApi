namespace CafePOS.Api.Contracts;

// ---------- AI Chat ----------

/// <summary>Role is "user" or "assistant" — matches the chat bubble side, translated
/// to Gemini's "user"/"model" roles inside GeminiService.</summary>
public record AiChatMessageDto(string Role, string Text);

public record AiChatRequest(List<AiChatMessageDto> History, string Message);

public record AiChatResponseDto(string Reply);

// ---------- Sales Forecast ----------

public record ForecastPointDto(string Date, decimal PredictedRevenue);

public record SalesForecastDto(List<ForecastPointDto> Forecast, string Method);

// ---------- Shift Optimization ----------

public record HourWindowStaffingDto(string Label, int OrderCount, int StaffScheduled, double OrdersPerStaff, string Status);

public record ShiftOptimizationDto(List<HourWindowStaffingDto> Windows, List<string> Suggestions);
