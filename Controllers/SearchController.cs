using CafePOS.Api.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CafePOS.Api.Controllers;

public record SearchResultDto(string Category, string Id, string Title, string Subtitle);

[ApiController]
[Route("api/search")]
public class SearchController : ControllerBase
{
    private const int PerCategoryLimit = 5;

    private readonly CafePosDbContext db;

    public SearchController(CafePosDbContext db)
    {
        this.db = db;
        // Fires on (almost) every keystroke in the header search box and never writes —
        // tracking five rows per category per keystroke is pure overhead.
        db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
    }

    /// <summary>Aggregates across menu, tables, orders, customers, and inventory — powers the header search dropdown.</summary>
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
        //
        // Contains() becomes LIKE '%term%', which no ordinary B-tree index can serve — so
        // each of these was a full table scan, per keystroke, growing with the catalog. The
        // AddTrigramSearchIndexes migration adds pg_trgm GIN indexes over lower(Name) etc,
        // which Postgres CAN use for a leading wildcard, so the shape of this query is
        // deliberately left alone: it's the index that changed, not the predicate.
        var term = q.Trim().ToLower();
        // A fully-numeric term is a short code typed in full (e.g. "24") — match it exactly,
        // not as a substring, so "24" doesn't also surface "124"/"244". Alphabetic codes stay
        // substring-matched so partials still work.
        var numericTerm = term.All(char.IsDigit);
        var results = new List<SearchResultDto>();

        var menu = await db.MenuItems
            .Where(m => m.Name.ToLower().Contains(term)
                || (m.ShortCode != null && (numericTerm
                    ? m.ShortCode.ToLower() == term
                    : m.ShortCode.ToLower().Contains(term))))
            .Take(PerCategoryLimit).ToListAsync();
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
