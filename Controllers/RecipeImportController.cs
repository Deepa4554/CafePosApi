using CafePOS.Api.Contracts;
using CafePOS.Api.Data;
using CafePOS.Api.Domain;
using CafePOS.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CafePOS.Api.Controllers;

/// <summary>Bulk recipe (bill-of-materials) import — one row per (menu item, ingredient)
/// pair, parsed client-side (see csvRecipeImport.ts) and posted here as JSON, same shape
/// as MenuController.BulkCreate.
///
/// The file defines recipes only. Stock levels and rates stay owned by the Inventory
/// screen; an ingredient the file names but Inventory doesn't have yet is created empty
/// (zero stock/cost) so the recipe has something to point at, and the user fills in the
/// real numbers there. Per-dish food cost then computes itself from recipe quantity ×
/// the ingredient's UnitCost (RecipesController.GetCost), no cost column needed here.
///
/// Each menu item's recipe is fully replaced with whatever rows the file has for it — so
/// a re-upload should list every ingredient for that item, not just the changed ones.</summary>
[ApiController]
[Route("api/recipe-import")]
[Authorize(Policy = Policies.OwnerOrManager)]
[Authorize(Policy = Policies.RequirePlus)]
public class RecipeImportController(CafePosDbContext db, IAuditService audit) : ControllerBase
{
    private const int MaxRows = 2000;

    [HttpPost]
    public async Task<ActionResult<RecipeImportResultDto>> BulkImport(List<RecipeImportRowRequest> rows)
    {
        if (rows is null || rows.Count == 0)
            throw new ApiValidationException("No rows to import.");
        if (rows.Count > MaxRows)
            throw new ApiValidationException($"Cannot import more than {MaxRows} rows at once.");

        var errors = new List<RecipeImportRowError>();
        // +2: row 1 is the header, and file rows are 1-based for a person reading them in Excel.
        var numbered = rows.Select((r, i) => (Row: r, RowNumber: i + 2)).ToList();

        // ---- Phase A: resolve menu items first, and keep only the rows belonging to one
        // that actually resolved. Ingredient creation (Phase B) runs off those rows alone:
        // resolving menu items later would mean a file with a mistyped dish name still
        // left brand-new junk ingredients sitting in Inventory for rows that all failed. ----
        var menuItemsByName = (await db.MenuItems.ToListAsync())
            .GroupBy(m => m.Name.Trim().ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.ToList());

        var resolvedMenuItems = new Dictionary<string, MenuItem>();
        var usableRows = new List<(RecipeImportRowRequest Row, int RowNumber)>();

        var menuGroups = numbered
            .Where(x => !string.IsNullOrWhiteSpace(x.Row.MenuItemName))
            .GroupBy(x => x.Row.MenuItemName.Trim(), StringComparer.OrdinalIgnoreCase);

        foreach (var group in menuGroups)
        {
            var menuItemName = group.Key;
            var nameLower = menuItemName.ToLowerInvariant();

            if (!menuItemsByName.TryGetValue(nameLower, out var menuMatches))
            {
                foreach (var x in group)
                    errors.Add(new RecipeImportRowError(x.RowNumber, menuItemName, x.Row.IngredientName, "Menu item not found — create it on the Menu screen first, then re-import."));
                continue;
            }
            if (menuMatches.Count > 1)
            {
                foreach (var x in group)
                    errors.Add(new RecipeImportRowError(x.RowNumber, menuItemName, x.Row.IngredientName, "Multiple menu items share this name — rename one so it's unique, then re-import."));
                continue;
            }
            if (menuMatches[0].ProductType != ProductType.Prepared)
            {
                foreach (var x in group)
                    errors.Add(new RecipeImportRowError(x.RowNumber, menuItemName, x.Row.IngredientName, "Only Prepared menu items can have a recipe."));
                continue;
            }

            resolvedMenuItems[nameLower] = menuMatches[0];
            usableRows.AddRange(group);
        }

        // ---- Phase B: resolve every distinct ingredient name exactly once. Ingredients are
        // usually repeated across several menu items' rows ("Butter" in ten recipes), so
        // working per unique name rather than per row keeps one ingredient from being
        // created twice off the same file.
        //
        // Existing ingredients are read, never written: stock and cost belong to the
        // Inventory screen. A name that isn't there yet is created as an empty shell (zero
        // stock, zero cost, zero max) purely so the recipe has something to point at — the
        // user then fills in the real quantity and rate on the Inventory screen, and the
        // dish's food cost starts computing from there. ----
        var inventoryByName = (await db.InventoryItems.Where(i => i.IsActive).ToListAsync())
            .GroupBy(i => i.Name.Trim().ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.ToList());

        var resolvedIngredients = new Dictionary<string, InventoryItem>();
        var ingredientsCreated = 0;

        var ingredientGroups = usableRows
            .Where(x => !string.IsNullOrWhiteSpace(x.Row.IngredientName))
            .GroupBy(x => x.Row.IngredientName.Trim(), StringComparer.OrdinalIgnoreCase);

        foreach (var group in ingredientGroups)
        {
            var first = group.First();
            var nameLower = first.Row.IngredientName.Trim().ToLowerInvariant();

            if (!inventoryByName.TryGetValue(nameLower, out var matches))
            {
                if (string.IsNullOrWhiteSpace(first.Row.Unit))
                {
                    foreach (var x in group)
                        errors.Add(new RecipeImportRowError(x.RowNumber, x.Row.MenuItemName, x.Row.IngredientName, "New ingredient needs a Unit to be created."));
                    continue;
                }

                var newItem = new InventoryItem
                {
                    Name = first.Row.IngredientName.Trim(),
                    Category = "Imported",
                    Unit = first.Row.Unit.Trim(),
                    // Zeros, not guesses. Max 0 keeps the stock bar/reorder-% empty rather
                    // than inventing a capacity the user never set (both the list and the
                    // edit modal already guard against a zero Max).
                    Current = 0,
                    Max = 0,
                    UnitCost = 0,
                    ReorderLevel = 0,
                };
                db.InventoryItems.Add(newItem);
                ingredientsCreated++;
                resolvedIngredients[nameLower] = newItem;
                continue;
            }

            if (matches.Count > 1)
            {
                foreach (var x in group)
                    errors.Add(new RecipeImportRowError(x.RowNumber, x.Row.MenuItemName, x.Row.IngredientName, "Multiple inventory items share this name — rename one so it's unique, then re-import."));
                continue;
            }

            resolvedIngredients[nameLower] = matches[0];
        }

        // Assigns Ids to the newly-created ingredients — RecipeItem.InventoryItemId below
        // needs them before the recipe rows can be built.
        await db.SaveChangesAsync();

        // ---- Phase C: wire each menu item's recipe from the ingredients Phase B resolved.
        // Full replace, same as RecipesController.Upsert — a re-uploaded file is assumed to
        // list a menu item's complete ingredient set, not a diff. ----
        var menuItemsUpdated = 0;

        foreach (var group in usableRows.GroupBy(x => x.Row.MenuItemName.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            var menuItemName = group.Key;
            var menuItem = resolvedMenuItems[menuItemName.ToLowerInvariant()];

            var lines = new List<RecipeItem>();
            var seenIngredientIds = new HashSet<int>();

            foreach (var x in group)
            {
                var row = x.Row;
                if (string.IsNullOrWhiteSpace(row.IngredientName))
                {
                    errors.Add(new RecipeImportRowError(x.RowNumber, menuItemName, row.IngredientName, "Ingredient name is blank."));
                    continue;
                }
                if (row.Quantity <= 0)
                {
                    errors.Add(new RecipeImportRowError(x.RowNumber, menuItemName, row.IngredientName, "Quantity must be greater than zero."));
                    continue;
                }
                if (string.IsNullOrWhiteSpace(row.Unit))
                {
                    errors.Add(new RecipeImportRowError(x.RowNumber, menuItemName, row.IngredientName, "Unit is blank."));
                    continue;
                }
                if (!resolvedIngredients.TryGetValue(row.IngredientName.Trim().ToLowerInvariant(), out var ingredient))
                {
                    errors.Add(new RecipeImportRowError(x.RowNumber, menuItemName, row.IngredientName, "Ingredient could not be resolved — see its own error row above."));
                    continue;
                }
                if (!UnitConverter.AreCompatible(row.Unit, ingredient.Unit))
                {
                    errors.Add(new RecipeImportRowError(x.RowNumber, menuItemName, row.IngredientName, $"'{row.Unit}' can't convert to {ingredient.Name}'s stored unit ('{ingredient.Unit}')."));
                    continue;
                }

                if (!seenIngredientIds.Add(ingredient.Id))
                {
                    errors.Add(new RecipeImportRowError(x.RowNumber, menuItemName, row.IngredientName, "Duplicate ingredient row for this menu item — the later row replaced the earlier quantity."));
                    lines.RemoveAll(l => l.InventoryItemId == ingredient.Id);
                }
                lines.Add(new RecipeItem { InventoryItemId = ingredient.Id, Quantity = row.Quantity, Unit = row.Unit.Trim() });
            }

            if (lines.Count == 0) continue; // every row for this item failed validation

            var recipe = await db.Recipes.Include(r => r.Items).FirstOrDefaultAsync(r => r.MenuItemId == menuItem.Id);
            if (recipe is null)
            {
                recipe = new Recipe { MenuItemId = menuItem.Id };
                db.Recipes.Add(recipe);
            }
            else
            {
                db.RecipeItems.RemoveRange(recipe.Items);
                recipe.Items.Clear();
            }
            recipe.Items = lines;
            menuItemsUpdated++;

            await audit.LogAsync(AuditAction.Update, AuditResource.Menu, menuItem.Id.ToString(),
                $"Recipe for '{menuItem.Name}' replaced via bulk import: {lines.Count} ingredient(s).",
                AuditSeverity.Low);
        }

        await db.SaveChangesAsync();

        return new RecipeImportResultDto(menuItemsUpdated, ingredientsCreated, errors.Count, errors);
    }
}
