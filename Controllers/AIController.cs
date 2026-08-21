using CafePOS.Api.Contracts;
using CafePOS.Api.Data;
using CafePOS.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CafePOS.Api.Controllers;

/// <summary>Gemini-backed AI chat. Forecast/inventory-risk/shift-optimization are all real
/// math on real data, not AI, and live in their own domain controllers (Dashboard/Staff).
/// Menu-from-photo import runs entirely on-device (OCR) — no server/Gemini involvement.</summary>
[ApiController]
[Route("api/ai")]
[Authorize(Policy = Policies.RequirePlus)]
// The system prompt below grounds the model in this cafe's live revenue, order counts and
// menu performance — the same numbers DashboardController and ReportsController keep to
// Owner/Manager. Without this a Waiter could just ask the chat for figures the Dashboard and
// Reports screens deliberately never show them. Mirrors 'AI' being in the RN app's
// FLOOR_STAFF_HIDDEN_ROUTES; this is the half that holds even for a direct API call.
[Authorize(Policy = Policies.OwnerOrManager)]
// Every call here holds a request slot open while a third-party model thinks, and costs
// real money per call. Matches the literal string Program.cs's AiLimiterPolicy const uses —
// attribute arguments must be compile-time constants, so it can't share the const directly.
[Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("AiLimiter")]
public class AIController(IGeminiService gemini, CafePosDbContext db) : ControllerBase
{
    [HttpPost("chat")]
    public async Task<ActionResult<AiChatResponseDto>> Chat(AiChatRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Message))
            throw new ApiValidationException("Message can't be empty.");
        if (req.Message.Length > 2000)
            throw new ApiValidationException("Message is too long.");
        if (req.History.Count > 40)
            throw new ApiValidationException("This conversation has gotten long — start a new chat.");

        var systemPrompt = await BuildSystemPromptAsync();
        var history = req.History.Select(h => new AiChatMessage(h.Role, h.Text)).ToList();
        var reply = await gemini.ChatAsync(systemPrompt, history, req.Message.Trim());
        return new AiChatResponseDto(reply);
    }

    /// <summary>Grounds the assistant in real, current numbers for this specific cafe so
    /// it answers from truth instead of guessing — it's told explicitly not to invent
    /// figures beyond what's given here.</summary>
    private async Task<string> BuildSystemPromptAsync()
    {
        var settings = await db.Settings.FirstAsync();
        // "Today" is the cafe's calendar day (see IstClock), not UTC's: on a UTC boundary the
        // assistant would quote a figure that silently excluded everything rung up between
        // midnight and 5:30am, and before 5:30am would fold in the whole of yesterday.
        var todayStart = IstClock.IstDateStartUtc(DateOnly.FromDateTime(IstClock.NowIst));
        var todaysOrders = await db.Orders.Where(o => o.Paid && o.CreatedAt >= todayStart).ToListAsync();
        var todayRevenue = todaysOrders.Sum(o => o.Total);
        var lowStockCount = await db.InventoryItems.CountAsync(i => i.Current <= i.ReorderLevel);
        var staffCount = await db.Staff.CountAsync(s => s.Status == Domain.StaffStatus.Active);

        return $"""
            You are the in-app AI assistant for CafePOS, a cafe point-of-sale system. You're helping the owner or manager of "{settings.BusinessName}", most likely on their phone between tasks.

            Real, current data for this cafe — use it when relevant, and never invent numbers beyond what's given here:
            - Today's revenue so far: Rs.{todayRevenue:0.00} across {todaysOrders.Count} paid orders
            - Ingredients currently at or below their reorder level: {lowStockCount}
            - Active staff on the roster: {staffCount}

            Answer practically and concisely — a couple of short paragraphs at most, not an essay. Use "Rs." for money, not any currency symbol. If asked something you genuinely don't have data for (e.g. numbers from before today, or something outside this app's data), say so honestly instead of guessing.
            """;
    }
}
