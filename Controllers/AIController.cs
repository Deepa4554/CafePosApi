using System.Text.Json;
using System.Text.RegularExpressions;
using CafePOS.Api.Contracts;
using CafePOS.Api.Data;
using CafePOS.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CafePOS.Api.Controllers;

/// <summary>Gemini-backed AI features — chat, and reading a photographed menu into
/// structured items. Forecast/inventory-risk/shift-optimization are all real math on
/// real data, not AI, and live in their own domain controllers (Dashboard/Staff).</summary>
[ApiController]
[Route("api/ai")]
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

    private static readonly Regex DataUriPattern = new(@"^data:(image/[a-zA-Z0-9+.-]+);base64,(.+)$", RegexOptions.Singleline);
    private const int MaxImageBytes = 6 * 1024 * 1024;
    private static readonly JsonSerializerOptions ExtractJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Reads a photographed menu (board, printed card, whatever) and returns the items it
    /// found, grouped by whatever section headings are visible in the photo — the client
    /// feeds the result straight into POST /menu-items/bulk with no transform, same as the
    /// CSV import path. Extraction accuracy depends on the photo; nothing here writes to
    /// the menu itself, so a bad read just means a bad list, never bad data.
    /// </summary>
    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpPost("import-menu-image")]
    public async Task<ActionResult<List<ExtractedMenuItemDto>>> ImportMenuFromImage(ImportMenuFromImageRequest req)
    {
        var match = DataUriPattern.Match(req.ImageDataUri ?? "");
        if (!match.Success)
            throw new ApiValidationException("That doesn't look like a valid image.");

        var mimeType = match.Groups[1].Value;
        var base64 = match.Groups[2].Value;
        if (base64.Length * 3 / 4 > MaxImageBytes)
            throw new ApiValidationException("Image is too large — please choose one under 6MB.");

        const string prompt = """
            You are reading a photograph of a physical cafe or restaurant menu. Extract every menu item you can clearly identify.

            Group items under the section/category heading they appear under in the photo (e.g. "Beverages", "Snacks", "Desserts"). If an item has no visible section heading, use "Food" as its category.

            Respond with ONLY a JSON array — no markdown code fences, no other text. Each element must look like:
            {"name": string, "category": string, "price": number, "subtitle": string or null}

            Rules:
            - "price" must be a plain number (e.g. 120.00), no currency symbol, not a string.
            - Skip anything that is not clearly a menu item with a price (addresses, phone numbers, opening hours, decorative text).
            - If the image does not look like a menu, or nothing is legible, return [].
            """;

        var raw = await gemini.GenerateWithImageAsync(prompt, base64, mimeType);
        var items = ParseExtractedItems(raw);
        if (items.Count == 0)
            throw new ApiConflictException("Couldn't find any menu items in that photo. Try a clearer, well-lit shot.");

        return items;
    }

    private static List<ExtractedMenuItemDto> ParseExtractedItems(string raw)
    {
        var cleaned = raw.Trim();
        if (cleaned.StartsWith("```"))
        {
            var firstNewline = cleaned.IndexOf('\n');
            cleaned = firstNewline >= 0 ? cleaned[(firstNewline + 1)..] : cleaned;
            var fenceEnd = cleaned.LastIndexOf("```", StringComparison.Ordinal);
            if (fenceEnd >= 0) cleaned = cleaned[..fenceEnd];
        }

        List<ExtractedMenuItemDto>? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<List<ExtractedMenuItemDto>>(cleaned, ExtractJsonOptions);
        }
        catch (JsonException)
        {
            throw new ApiConflictException("Couldn't read a menu from that image. Try a clearer photo.");
        }

        return (parsed ?? [])
            .Where(i => !string.IsNullOrWhiteSpace(i.Name) && i.Price > 0)
            .Select(i => i with { Name = i.Name.Trim(), Category = string.IsNullOrWhiteSpace(i.Category) ? "Food" : i.Category.Trim() })
            .ToList();
    }

    /// <summary>Grounds the assistant in real, current numbers for this specific cafe so
    /// it answers from truth instead of guessing — it's told explicitly not to invent
    /// figures beyond what's given here.</summary>
    private async Task<string> BuildSystemPromptAsync()
    {
        var settings = await db.Settings.FirstAsync();
        var todayStart = DateTime.UtcNow.Date;
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
