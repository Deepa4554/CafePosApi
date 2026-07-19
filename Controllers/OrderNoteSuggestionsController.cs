using CafePOS.Api.Contracts;
using CafePOS.Api.Data;
using CafePOS.Api.Domain;
using CafePOS.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CafePOS.Api.Controllers;

/// <summary>Tap-to-apply note suggestions for order items (e.g. "No onion", "Extra spicy") —
/// a small generic starter list every cafe gets, plus whatever this specific cafe's staff
/// has typed before (so a recurring instruction only needs to be typed once).</summary>
[ApiController]
[Route("api/order-note-suggestions")]
[Authorize]
public class OrderNoteSuggestionsController(CafePosDbContext db) : ControllerBase
{
    private static readonly string[] DefaultSuggestions =
    [
        "Extra Hot", "Less Sugar", "No Sugar", "Extra Sugar", "Less Ice", "No Ice", "Extra Shot",
        "Regular Milk", "Oat Milk", "Take Away", "No Onion", "No Garlic", "Less Spicy", "Extra Spicy",
        "Extra Cheese", "Pack Separately",
    ];

    [HttpGet]
    public async Task<IEnumerable<OrderNoteSuggestionDto>> List()
    {
        var saved = await db.OrderNoteSuggestions.ToListAsync();
        var byText = saved.ToDictionary(s => s.Text, s => s, StringComparer.OrdinalIgnoreCase);

        var merged = new List<OrderNoteSuggestionDto>();
        foreach (var text in DefaultSuggestions)
            merged.Add(byText.TryGetValue(text, out var s) ? OrderNoteSuggestionDto.From(s) : new OrderNoteSuggestionDto(0, text, 0));
        foreach (var s in saved)
            if (!DefaultSuggestions.Contains(s.Text, StringComparer.OrdinalIgnoreCase))
                merged.Add(OrderNoteSuggestionDto.From(s));

        return merged.OrderByDescending(m => m.UsageCount).ThenBy(m => m.Text, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Called whenever staff applies/types a note on an order item — bumps an
    /// existing suggestion's usage, or remembers a brand-new one for next time.</summary>
    [HttpPost]
    public async Task<ActionResult<OrderNoteSuggestionDto>> Upsert(UpsertOrderNoteSuggestionRequest req)
    {
        var text = req.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text))
            throw new ApiValidationException("Note text is required.");
        if (text.Length > 100)
            throw new ApiValidationException("Note text cannot exceed 100 characters.");

        var existing = await db.OrderNoteSuggestions.FirstOrDefaultAsync(s => s.Text.ToLower() == text.ToLower());
        if (existing is not null)
        {
            existing.UsageCount += 1;
            existing.LastUsedAt = DateTime.UtcNow;
        }
        else
        {
            existing = new OrderNoteSuggestion { Text = text };
            db.OrderNoteSuggestions.Add(existing);
        }
        await db.SaveChangesAsync();
        return OrderNoteSuggestionDto.From(existing);
    }
}
