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
    /// <summary>Every category this tenant has, from BOTH places one can exist: implied by
    /// the items sitting in it, and as an explicit MenuCategory row (created by setting a
    /// default station, or by Create below for a category that has no items yet). The union
    /// is what lets an empty category be a real, listable thing — before it, a category only
    /// existed for as long as something was in it.
    ///
    /// Names are de-duplicated case-insensitively with the item spelling winning, so a
    /// legacy "rolls" row left over next to items in "Rolls" shows up once, not twice.</summary>
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

        return itemCounts.Select(c => c.Category)
            .Concat(defaults.Select(d => d.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name =>
            {
                var count = itemCounts.FirstOrDefault(c => string.Equals(c.Category, name, StringComparison.OrdinalIgnoreCase))?.Count ?? 0;
                var match = defaults.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));
                // No row means no configured position: int.MaxValue parks it behind every
                // category that has one, and the ThenBy keeps those unpositioned ones in the
                // alphabetical order they have always been listed in.
                return new CategoryDto(name, match?.DefaultStationId, match?.DefaultStation?.Name, count, match?.SortOrder ?? int.MaxValue);
            })
            .OrderBy(c => c.SortOrder)
            .ThenBy(c => c.Name)
            .ToList();
    }

    /// <summary>Creates a category with nothing in it yet, so an Owner can set the menu's
    /// shape up front instead of having to invent the category while adding the first item.
    /// It survives as a MenuCategory row; List above is what makes it visible despite having
    /// no items.</summary>
    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpPost]
    public async Task<ActionResult<CategoryDto>> Create(CreateCategoryRequest req)
    {
        var name = (req.Name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
            throw new ApiValidationException("Category name is required.");

        // Case-insensitive, unlike the (TenantId, Name) unique index, which would happily
        // take both "Rolls" and "rolls" and leave the menu with two identical-looking pills.
        if (await FindCategoryName(name) is string clash)
            throw new ApiValidationException($"\"{clash}\" already exists.");

        // On a menu nobody has arranged yet, every category is unpositioned and listed
        // alphabetically — so the new one stays unpositioned too and simply slots in
        // alphabetically with the rest. Giving it a position here would rank it ABOVE every
        // unpositioned category (they sort as int.MaxValue), i.e. adding "Zzz Extras" would
        // shove it to the front of the POS strip. Only once an order actually exists does a
        // new category get a position, and then it goes on the end of it.
        var lastPosition = await db.MenuCategories.MaxAsync(c => c.SortOrder);
        var sortOrder = lastPosition is int last ? last + 1 : (int?)null;

        db.MenuCategories.Add(new MenuCategory { Name = name, SortOrder = sortOrder });
        await db.SaveChangesAsync();

        return new CategoryDto(name, null, null, 0, sortOrder ?? int.MaxValue);
    }

    /// <summary>Rewrites the category order, front first. Every named category gets a row if
    /// it didn't have one — position can't be stored anywhere else — so this is also what
    /// first pins down the order of a menu that had only ever been listed alphabetically.</summary>
    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpPut("reorder")]
    public async Task<ActionResult<IEnumerable<CategoryDto>>> Reorder(ReorderCategoriesRequest req)
    {
        var rows = await db.MenuCategories.ToListAsync();

        for (var i = 0; i < req.Names.Count; i++)
        {
            var resolved = await FindCategoryName(req.Names[i].Trim());
            if (resolved is null) continue; // deleted from under the client mid-drag; skip it

            var row = rows.FirstOrDefault(c => c.Name == resolved);
            if (row is null)
            {
                row = new MenuCategory { Name = resolved };
                db.MenuCategories.Add(row);
                rows.Add(row);
            }
            row.SortOrder = i;
        }

        await db.SaveChangesAsync();
        return Ok(await List());
    }

    /// <summary>Renames a category everywhere it is referenced by name. When the new name is
    /// one that already exists this is a MERGE — the two categories' items end up together
    /// and there is no undo, so the client warns before calling it.</summary>
    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpPut("{name}/rename")]
    public async Task<ActionResult<CategoryMutationResultDto>> Rename(string name, RenameCategoryRequest req)
    {
        var newName = (req.NewName ?? "").Trim();
        if (string.IsNullOrWhiteSpace(newName))
            throw new ApiValidationException("New category name is required.");
        // Work from the tenant's own spelling, not the caller's — the equality checks below
        // and the MenuItem/Offer updates are all exact matches, so a differently-cased name
        // off the wire would find and move nothing while still reporting success.
        var oldName = await FindCategoryName(name.Trim())
            ?? throw new ApiValidationException($"\"{name.Trim()}\" is not a category on this menu.");

        // Merge when the target name is already in use by a DIFFERENT category; a pure
        // change of casing ("rolls" -> "Rolls") is a rename of itself, not a merge.
        var target = await FindCategoryName(newName);
        var merged = target is not null && !string.Equals(target, oldName, StringComparison.OrdinalIgnoreCase);
        // Adopt the existing spelling on a merge so the survivor keeps one consistent name.
        var finalName = merged ? target! : newName;

        var items = await db.MenuItems.Where(m => m.Category == oldName).ToListAsync();
        foreach (var item in items) item.Category = finalName;

        var offers = await db.Offers.Where(o => o.CategoryName == oldName).ToListAsync();
        foreach (var offer in offers) offer.CategoryName = finalName;

        var rows = await db.MenuCategories.Where(c => c.Name == oldName || c.Name == finalName).ToListAsync();
        var survivor = rows.FirstOrDefault(c => c.Name == finalName);
        var source = rows.FirstOrDefault(c => c.Name == oldName);
        if (survivor is null && source is not null)
        {
            source.Name = finalName;
        }
        else if (survivor is not null && source is not null && source.Id != survivor.Id)
        {
            // Target already had a row; keep its default station and drop the source's,
            // rather than leaving two rows fighting over the same (now identical) name.
            db.MenuCategories.Remove(source);
        }

        await db.SaveChangesAsync();
        return new CategoryMutationResultDto(finalName, items.Count, offers.Count, merged);
    }

    /// <summary>Deletes a category. A category with items in it cannot simply vanish — the
    /// items would be left holding a name nothing else knows about — so moveTo is required
    /// whenever it still has any, and its items (and any category-scoped offers pointing at
    /// it) follow them across.</summary>
    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpDelete("{name}")]
    public async Task<ActionResult<CategoryMutationResultDto>> Delete(string name, [FromQuery] string? moveTo)
    {
        // Resolve to the tenant's own spelling first — see Rename for why.
        var categoryName = await FindCategoryName(name.Trim())
            ?? throw new ApiValidationException($"\"{name.Trim()}\" is not a category on this menu.");

        var items = await db.MenuItems.Where(m => m.Category == categoryName).ToListAsync();
        var target = string.IsNullOrWhiteSpace(moveTo) ? null : await FindCategoryName(moveTo.Trim());

        if (items.Count > 0)
        {
            if (target is null)
                throw new ApiValidationException($"\"{categoryName}\" still has {items.Count} item{(items.Count == 1 ? "" : "s")} — choose a category to move them into.");
            if (string.Equals(target, categoryName, StringComparison.OrdinalIgnoreCase))
                throw new ApiValidationException("Choose a different category to move the items into.");
        }

        var offers = target is null
            ? []
            : await db.Offers.Where(o => o.CategoryName == categoryName).ToListAsync();

        foreach (var item in items) item.Category = target!;
        foreach (var offer in offers) offer.CategoryName = target;

        var rows = await db.MenuCategories.Where(c => c.Name == categoryName).ToListAsync();
        db.MenuCategories.RemoveRange(rows);

        await db.SaveChangesAsync();
        return new CategoryMutationResultDto(categoryName, items.Count, offers.Count, false);
    }

    /// <summary>The tenant's existing spelling of <paramref name="name"/>, or null if no
    /// category goes by it. Checks items and MenuCategory rows because either one alone is
    /// enough for a category to exist — see List.</summary>
    private async Task<string?> FindCategoryName(string name)
    {
        var fromItems = await db.MenuItems
            .Where(m => m.Category.ToLower() == name.ToLower())
            .Select(m => m.Category)
            .FirstOrDefaultAsync();
        if (fromItems is not null) return fromItems;

        return await db.MenuCategories
            .Where(c => c.Name.ToLower() == name.ToLower())
            .Select(c => c.Name)
            .FirstOrDefaultAsync();
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
        return new CategoryDto(categoryName, category.DefaultStationId, station?.Name, itemCount, category.SortOrder ?? int.MaxValue);
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
