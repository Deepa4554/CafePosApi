using CafePOS.Api.Contracts;
using CafePOS.Api.Data;
using CafePOS.Api.Domain;
using CafePOS.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CafePOS.Api.Controllers;

/// <summary>Per-tenant default-station lookup for MenuItem.Category values — Category
/// itself stays a free-text string on MenuItem (see MenuController); this only lets an
/// Owner set "everything in Beverages defaults to the Bar station" once, instead of
/// tagging every item's StationId individually. See Domain.MenuCategory.</summary>
[ApiController]
[Route("api/categories")]
[Authorize]
public class CategoriesController(CafePosDbContext db) : ControllerBase
{
    /// <summary>Every distinct MenuItem.Category value for this tenant, left-joined with
    /// any MenuCategory row that sets a default station — a category with items but no
    /// default configured yet simply comes back with DefaultStationId null.</summary>
    [HttpGet]
    public async Task<IEnumerable<CategoryDto>> List()
    {
        var itemCounts = await db.MenuItems
            .GroupBy(m => m.Category)
            .Select(g => new { Category = g.Key, Count = g.Count() })
            .ToListAsync();

        var defaults = await db.MenuCategories
            .Include(c => c.DefaultStation)
            .ToListAsync();
        var defaultsByName = defaults.ToDictionary(c => c.Name, c => c);

        return itemCounts
            .OrderBy(c => c.Category)
            .Select(c =>
            {
                defaultsByName.TryGetValue(c.Category, out var match);
                return new CategoryDto(c.Category, match?.DefaultStationId, match?.DefaultStation?.Name, c.Count);
            });
    }

    /// <summary>Upserts this category's default station. Does NOT touch any existing
    /// MenuItem — only affects what a new item in this category prefills to on the
    /// client. Use ApplyToItems below to explicitly bulk-update existing items too.</summary>
    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpPut("{name}/default-station")]
    public async Task<ActionResult<CategoryDto>> SetDefaultStation(string name, SetCategoryDefaultStationRequest req)
    {
        var categoryName = name.Trim();
        if (string.IsNullOrWhiteSpace(categoryName))
            throw new ApiValidationException("Category name is required.");

        if (req.StationId is int sid && !await db.Stations.AnyAsync(s => s.Id == sid))
            throw new ApiValidationException("That station does not exist.");

        var category = await db.MenuCategories.FirstOrDefaultAsync(c => c.Name == categoryName);
        if (category is null)
        {
            category = new MenuCategory { Name = categoryName, DefaultStationId = req.StationId };
            db.MenuCategories.Add(category);
        }
        else
        {
            category.DefaultStationId = req.StationId;
        }
        await db.SaveChangesAsync();

        var station = req.StationId is int id ? await db.Stations.FindAsync(id) : null;
        var itemCount = await db.MenuItems.CountAsync(m => m.Category == categoryName);
        return new CategoryDto(categoryName, category.DefaultStationId, station?.Name, itemCount);
    }

    /// <summary>Explicit, opt-in bulk update — sets StationId on every MenuItem currently
    /// in this category. Kept separate from SetDefaultStation so configuring a default
    /// never silently overwrites items an Owner already hand-assigned to a different
    /// station.</summary>
    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpPost("{name}/apply-station-to-items")]
    public async Task<ActionResult<BulkImportResultDto>> ApplyStationToItems(string name, ApplyCategoryStationRequest req)
    {
        var categoryName = name.Trim();
        if (!await db.Stations.AnyAsync(s => s.Id == req.StationId))
            throw new ApiValidationException("That station does not exist.");

        var items = await db.MenuItems.Where(m => m.Category == categoryName).ToListAsync();
        foreach (var item in items) item.StationId = req.StationId;
        await db.SaveChangesAsync();

        return new BulkImportResultDto(items.Count, 0);
    }
}
