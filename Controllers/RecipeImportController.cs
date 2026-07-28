using CafePOS.Api.Contracts;
using CafePOS.Api.Data;
using CafePOS.Api.Domain;
using CafePOS.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CafePOS.Api.Controllers;

/// <summary>Bulk CSV/Excel import — one row per (menu item, ingredient) pair, parsed
/// client-side (see csvRecipeImport.ts) and posted here as JSON, same shape as
/// MenuController.BulkCreate. Wires up Inventory + Recipe together from a single file:
/// missing ingredients are created, existing ones optionally get a restock batch, and
/// each menu item's recipe is fully replaced with whatever rows the file has for it —
/// so a re-upload should list every ingredient for that item, not just the changed ones.</summary>
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

        // ---- Phase A: resolve every distinct ingredient name exactly once. Ingredients
        // are usually repeated across several menu items' rows ("Butter" in ten recipes) —
        // resolving/restocking per unique name (not per row) is what keeps a repeated
        // CurrentStock column from silently double- or triple-counting the same stock. ----
        var inventoryByName = (await db.InventoryItems.Where(i => i.IsActive).ToListAsync())
            .GroupBy(i => i.Name.Trim().ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.ToList());

        var resolvedIngredients = new Dictionary<string, InventoryItem>();
        var ingredientsCreated = 0;
        var ingredientsRestocked = 0;

        var ingredientGroups = numbered
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
                    UnitCost = first.Row.UnitCost ?? 0,
                    Max = first.Row.CurrentStock is double c && c > 0 ? c : 1,
                    LastRestockAt = DateTime.UtcNow,
                };
                newItem.ReorderLevel = Math.Round(newItem.Max * 0.25, 2);
                db.InventoryItems.Add(newItem);
                await db.SaveChangesAsync(); // assigns Id, needed as the new batch's FK below

                if (first.Row.CurrentStock is double initial && initial > 0)
                {
                    InventoryBatchService.CreateBatch(db, newItem, initial, newItem.UnitCost, expiryDate: null,
                        InventoryTransactionType.Purchase, referenceId: "bulk-import", CurrentUserId(), CurrentUserName());
                }
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

            var item = matches[0];
            if (first.Row.CurrentStock is double qty && qty > 0)
            {
                if (!UnitConverter.AreCompatible(first.Row.Unit, item.Unit))
                {
                    errors.Add(new RecipeImportRowError(first.RowNumber, first.Row.MenuItemName, first.Row.IngredientName,
                        $"'{first.Row.Unit}' can't convert to this ingredient's stored unit ('{item.Unit}') — stock was not changed."));
                }
                else
                {
                    var converted = UnitConverter.Convert(qty, first.Row.Unit, item.Unit);
                    var cost = first.Row.UnitCost ?? item.UnitCost;
                    InventoryBatchService.CreateBatch(db, item, converted, cost, expiryDate: null,
                        InventoryTransactionType.Purchase, referenceId: "bulk-import", CurrentUserId(), CurrentUserName());
                    if (first.Row.UnitCost is decimal) item.UnitCost = cost;
                    item.LastRestockAt = DateTime.UtcNow;
                    ingredientsRestocked++;
                }
            }
            resolvedIngredients[nameLower] = item;
        }

        await db.SaveChangesAsync();

        // ---- Phase B: wire each menu item's recipe from the ingredients Phase A resolved.
        // Full replace, same as RecipesController.Upsert — a re-uploaded file is assumed to
        // list a menu item's complete ingredient set, not a diff. ----
        var menuItemsByName = (await db.MenuItems.ToListAsync())
            .GroupBy(m => m.Name.Trim().ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.ToList());

        var menuItemsUpdated = 0;
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

            var menuItem = menuMatches[0];
            if (menuItem.ProductType != ProductType.Prepared)
            {
                foreach (var x in group)
                    errors.Add(new RecipeImportRowError(x.RowNumber, menuItemName, x.Row.IngredientName, "Only Prepared menu items can have a recipe."));
                continue;
            }

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

        return new RecipeImportResultDto(menuItemsUpdated, ingredientsCreated, ingredientsRestocked, errors.Count, errors);
    }

    private int? CurrentUserId()
    {
        var claim = User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
        return int.TryParse(claim, out var id) ? id : null;
    }

    private string CurrentUserName() => User.Identity?.Name ?? "Cafe Staff";
}
