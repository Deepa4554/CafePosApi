using CafePOS.Api.Contracts;
using CafePOS.Api.Data;
using CafePOS.Api.Domain;
using CafePOS.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CafePOS.Api.Controllers;

/// <summary>Manages the Bill-of-Materials (Recipe) for Prepared menu items — what
/// ingredients, and how much of each, a sale of that item consumes.</summary>
[ApiController]
[Route("api/menu-items/{menuItemId:int}/recipe")]
[Authorize(Policy = Policies.OwnerOrManager)]
[Authorize(Policy = Policies.RequirePlus)]
public class RecipesController(CafePosDbContext db, IAuditService audit) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<RecipeDto>> Get(int menuItemId)
    {
        var recipe = await db.Recipes.Include(r => r.Items).FirstOrDefaultAsync(r => r.MenuItemId == menuItemId);
        if (recipe is null) return NotFound();

        return await ToDto(recipe);
    }

    /// <summary>Ingredient cost of this recipe vs the menu item's price — ingredient cost is
    /// more sensitive than the recipe shape itself (which any Owner/Manager already sees via
    /// Get above), so this stacks its own extra OwnerOrManager policy per the doc's security
    /// section (cost data must stay Owner/Manager-only).</summary>
    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpGet("cost")]
    public async Task<ActionResult<RecipeCostDto>> GetCost(int menuItemId)
    {
        var menuItem = await db.MenuItems.FindAsync(menuItemId);
        if (menuItem is null) return NotFound();
        var recipe = await db.Recipes.Include(r => r.Items).FirstOrDefaultAsync(r => r.MenuItemId == menuItemId);
        if (recipe is null) return NotFound();

        var inventoryIds = recipe.Items.Select(i => i.InventoryItemId).ToList();
        var inventory = await db.InventoryItems.Where(i => inventoryIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id);

        var lines = recipe.Items
            .Where(ri => inventory.ContainsKey(ri.InventoryItemId))
            .Select(ri =>
            {
                var ingredient = inventory[ri.InventoryItemId];
                var qtyInIngredientUnit = UnitConverter.Convert(ri.Quantity, ri.Unit, ingredient.Unit);
                return new RecipeItemCostDto(ingredient.Id, ingredient.Name, ri.Quantity, ri.Unit, Math.Round((decimal)qtyInIngredientUnit * ingredient.UnitCost, 2));
            })
            .ToList();

        var ingredientCost = lines.Sum(l => l.LineCost);
        var foodCostPct = menuItem.Price > 0 ? Math.Round(ingredientCost / menuItem.Price * 100, 1) : 0;
        return new RecipeCostDto(menuItem.Id, menuItem.Name, ingredientCost, menuItem.Price, foodCostPct, lines);
    }

    /// <summary>Full replace — the client always sends the complete ingredient list, not a
    /// diff, matching how the Recipe Builder screen's add/remove-row UI works.</summary>
    [HttpPut]
    public async Task<ActionResult<RecipeDto>> Upsert(int menuItemId, UpsertRecipeRequest req)
    {
        var menuItem = await db.MenuItems.FindAsync(menuItemId);
        if (menuItem is null) return NotFound();
        if (menuItem.ProductType != ProductType.Prepared)
            throw new ApiValidationException("Only Prepared menu items can have a recipe.");
        if (req.Items is null || req.Items.Count == 0)
            throw new ApiValidationException("A recipe needs at least one ingredient.");

        var inventoryIds = req.Items.Select(i => i.InventoryItemId).ToList();
        var inventory = await db.InventoryItems.Where(i => inventoryIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id);
        foreach (var line in req.Items)
        {
            if (!inventory.TryGetValue(line.InventoryItemId, out var ingredient))
                throw new ApiValidationException($"Ingredient {line.InventoryItemId} not found.");
            if (line.Quantity <= 0)
                throw new ApiValidationException($"Quantity for {ingredient.Name} must be greater than zero.");
            if (!UnitConverter.AreCompatible(line.Unit, ingredient.Unit))
                throw new ApiValidationException($"'{line.Unit}' can't be converted to {ingredient.Name}'s unit ('{ingredient.Unit}').");
        }

        var recipe = await db.Recipes.Include(r => r.Items).FirstOrDefaultAsync(r => r.MenuItemId == menuItemId);
        var oldIngredientIds = recipe?.Items.Select(i => i.InventoryItemId).ToHashSet() ?? [];
        if (recipe is null)
        {
            recipe = new Recipe { MenuItemId = menuItemId };
            db.Recipes.Add(recipe);
        }
        else
        {
            db.RecipeItems.RemoveRange(recipe.Items);
            recipe.Items.Clear();
        }

        recipe.Items = req.Items.Select(i => new RecipeItem
        {
            InventoryItemId = i.InventoryItemId,
            Quantity = i.Quantity,
            Unit = i.Unit.Trim(),
        }).ToList();

        await db.SaveChangesAsync();

        // Recipe changes silently rewrite what auto-deduction consumes — a manager
        // shrinking a portion's recorded quantity can hide theft/over-portioning from the
        // variance report (doc Section 11 rule #3), so every edit gets an audit trail.
        var newIngredientIds = recipe.Items.Select(i => i.InventoryItemId).ToHashSet();
        var added = newIngredientIds.Except(oldIngredientIds).Count();
        var removed = oldIngredientIds.Except(newIngredientIds).Count();
        var kept = newIngredientIds.Intersect(oldIngredientIds).Count();
        await audit.LogAsync(AuditAction.Update, AuditResource.Menu, menuItemId.ToString(),
            $"Recipe for '{menuItem.Name}' updated: {added} ingredient(s) added, {removed} removed, {kept} kept/changed.",
            AuditSeverity.Low);

        return await ToDto(recipe);
    }

    [HttpDelete]
    public async Task<IActionResult> Delete(int menuItemId)
    {
        var recipe = await db.Recipes.Include(r => r.Items).FirstOrDefaultAsync(r => r.MenuItemId == menuItemId);
        if (recipe is null) return NotFound();

        db.Recipes.Remove(recipe);
        await db.SaveChangesAsync();
        return NoContent();
    }

    private async Task<RecipeDto> ToDto(Recipe recipe)
    {
        var inventoryIds = recipe.Items.Select(i => i.InventoryItemId).ToList();
        var inventory = await db.InventoryItems.Where(i => inventoryIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id);

        var items = recipe.Items.Select(i => new RecipeItemDto(
            i.InventoryItemId,
            inventory.TryGetValue(i.InventoryItemId, out var ing) ? ing.Name : "Unknown",
            inventory.TryGetValue(i.InventoryItemId, out var ing2) ? ing2.Unit : "",
            i.Quantity,
            i.Unit)).ToList();

        return new RecipeDto(recipe.MenuItemId, items);
    }
}
