using CafePOS.Api.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CafePOS.Api.Controllers;

public record SearchResultDto(string Category, string Id, string Title, string Subtitle);

[ApiController]
[Route("api/search")]
public class SearchController(CafePosDbContext db) : ControllerBase
{
    private const int PerCategoryLimit = 5;

    /// <summary>Aggregates across menu, tables, orders, customers, and inventory — powers GlobalSearchScreen.</summary>
    [HttpGet]
    public async Task<IEnumerable<SearchResultDto>> Search([FromQuery] string q)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 2)
            return [];

        // Case-insensitive on purpose (was a plain ordinal Contains before, which meant
        // "flat" never matched "Flat White" — real users type lowercase, seed/real data
        // is Title Case, so almost every query silently returned zero results).
        // .ToLower() on both sides translates to SQL on Npgsql and evaluates fine on the
        // InMemory dev provider, unlike EF.Functions.ILike which only Npgsql supports.
        var term = q.Trim().ToLower();
        var results = new List<SearchResultDto>();

        var menu = await db.MenuItems.Where(m => m.Name.ToLower().Contains(term)).Take(PerCategoryLimit).ToListAsync();
        results.AddRange(menu.Select(m => new SearchResultDto("Menu", m.Id.ToString(), m.Name, $"{m.Category} · ₹{m.Price:F2}")));

        var tables = await db.Tables.Where(t => t.Code.ToLower().Contains(term) || t.Zone.ToLower().Contains(term)).Take(PerCategoryLimit).ToListAsync();
        results.AddRange(tables.Select(t => new SearchResultDto("Tables", t.Id.ToString(), t.Code, $"{t.Zone} · {t.Seats} seats")));

        var customers = await db.Customers.Where(c => c.Name.ToLower().Contains(term)).Take(PerCategoryLimit).ToListAsync();
        results.AddRange(customers.Select(c => new SearchResultDto("Customers", c.Id.ToString(), c.Name, $"{c.VisitCount} visits · {c.TotalPoints} pts")));

        var inventory = await db.InventoryItems.Where(i => i.Name.ToLower().Contains(term)).Take(PerCategoryLimit).ToListAsync();
        results.AddRange(inventory.Select(i => new SearchResultDto("Inventory", i.Id.ToString(), i.Name, $"{i.Current}{i.Unit} / {i.Max}{i.Unit}")));

        if (int.TryParse(term.TrimStart('#'), out var orderId))
        {
            var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == orderId);
            if (order is not null)
                results.Add(new SearchResultDto("Orders", order.Id.ToString(), $"#{1000 + order.Id}", $"{order.Title} · ₹{order.Total:F2}"));
        }

        return results;
    }
}
