using CafePOS.Api.Contracts;
using CafePOS.Api.Data;
using CafePOS.Api.Domain;
using CafePOS.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CafePOS.Api.Controllers;

/// <summary>Cost/variance reporting — Owner/Manager-only (not the broader
/// Policies.CanReadInventory used by InventoryController), since ingredient cost, food-cost
/// %, and variance $ are exactly the numbers the doc's security section says must stay off
/// a Waiter/Cashier's screen.</summary>
[ApiController]
[Route("api/reports")]
[Authorize(Policy = Policies.OwnerOrManager)]
[Authorize(Policy = Policies.RequirePlus)]
public class ReportsController(CafePosDbContext db) : ControllerBase
{
    /// <summary>Ingredient cost vs menu price for every Prepared/Independent item, worst
    /// food-cost% first — "which items are eating your margin" (doc Section 7).</summary>
    [HttpGet("food-cost")]
    public async Task<IEnumerable<RecipeCostDto>> FoodCost()
    {
        var menuItems = await db.MenuItems.Where(m => m.ProductType == ProductType.Prepared || m.ProductType == ProductType.Independent).ToListAsync();
        var recipes = await db.Recipes.Include(r => r.Items).ToListAsync();
        var recipeByMenuItem = recipes.ToDictionary(r => r.MenuItemId);

        var inventoryIds = recipes.SelectMany(r => r.Items).Select(ri => ri.InventoryItemId).ToHashSet();
        foreach (var m in menuItems)
            if (m.ProductType == ProductType.Independent && m.LinkedInventoryItemId is int linkedId)
                inventoryIds.Add(linkedId);
        var inventory = await db.InventoryItems.Where(i => inventoryIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id);

        var results = new List<RecipeCostDto>();
        foreach (var menuItem in menuItems)
        {
            List<RecipeItemCostDto> lines;
            if (menuItem.ProductType == ProductType.Independent)
            {
                if (menuItem.LinkedInventoryItemId is not int linkedId || !inventory.TryGetValue(linkedId, out var linked)) continue;
                lines = [new RecipeItemCostDto(linked.Id, linked.Name, 1, linked.Unit, linked.UnitCost)];
            }
            else
            {
                if (!recipeByMenuItem.TryGetValue(menuItem.Id, out var recipe)) continue;
                lines = recipe.Items
                    .Where(ri => inventory.ContainsKey(ri.InventoryItemId))
                    .Select(ri =>
                    {
                        var ingredient = inventory[ri.InventoryItemId];
                        var qtyInIngredientUnit = UnitConverter.Convert(ri.Quantity, ri.Unit, ingredient.Unit);
                        return new RecipeItemCostDto(ingredient.Id, ingredient.Name, ri.Quantity, ri.Unit, Math.Round((decimal)qtyInIngredientUnit * ingredient.UnitCost, 2));
                    })
                    .ToList();
            }

            var ingredientCost = lines.Sum(l => l.LineCost);
            var foodCostPct = menuItem.Price > 0 ? Math.Round(ingredientCost / menuItem.Price * 100, 1) : 0;
            results.Add(new RecipeCostDto(menuItem.Id, menuItem.Name, ingredientCost, menuItem.Price, foodCostPct, lines));
        }

        return results.OrderByDescending(r => r.FoodCostPct);
    }

    /// <summary>Theoretical consumption (what the recipes say should have been used) vs
    /// purchases/wastage/the latest physical count correction, per ingredient. This system's
    /// only "actual" measurement is a Stock Take — not a continuous meter feed — so
    /// LatestStockTakeVariance is the closest thing to ground truth, not a live delta.</summary>
    [HttpGet("variance")]
    public async Task<IEnumerable<VarianceReportLineDto>> Variance(
        [FromQuery] int days = 30, [FromQuery] DateOnly? from = null, [FromQuery] DateOnly? to = null, [FromQuery] int? branchId = null)
    {
        DateTime periodStart;
        DateTime periodEndExclusive;
        if (from is not null || to is not null)
        {
            periodStart = (from ?? to!.Value).ToDateTime(TimeOnly.MinValue);
            periodEndExclusive = (to ?? from!.Value).ToDateTime(TimeOnly.MinValue).AddDays(1);
        }
        else
        {
            if (days <= 0) days = 30;
            periodStart = DateTime.UtcNow.AddDays(-days);
            periodEndExclusive = DateTime.UtcNow;
        }

        var itemsQuery = db.InventoryItems.AsQueryable();
        if (branchId is int bid) itemsQuery = itemsQuery.Where(i => i.BranchId == bid);
        var items = await itemsQuery.OrderBy(i => i.Name).ToListAsync();
        var itemIds = items.Select(i => i.Id).ToList();

        var txns = await db.InventoryTransactions
            .Where(t => itemIds.Contains(t.InventoryItemId) && t.CreatedAt >= periodStart && t.CreatedAt < periodEndExclusive)
            .ToListAsync();

        var latestStockTakeLines = await db.StockTakeLines
            .Where(l => itemIds.Contains(l.InventoryItemId) && l.Variance != null)
            .Join(db.StockTakes.Where(s => s.Status == StockTakeStatus.Finalized), l => l.StockTakeId, s => s.Id, (l, s) => new { l.InventoryItemId, l.Variance, s.FinalizedAt })
            .ToListAsync();
        var latestByItem = latestStockTakeLines
            .GroupBy(x => x.InventoryItemId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.FinalizedAt).First());

        return items.Select(i =>
        {
            var itemTxns = txns.Where(t => t.InventoryItemId == i.Id).ToList();
            var theoretical = itemTxns.Where(t => t.Type == InventoryTransactionType.Sale).Sum(t => Math.Abs(t.ChangedQuantity));
            var purchased = itemTxns.Where(t => t.Type == InventoryTransactionType.Purchase).Sum(t => t.ChangedQuantity);
            var wastage = itemTxns.Where(t => t.Type == InventoryTransactionType.Waste).Sum(t => Math.Abs(t.ChangedQuantity));
            latestByItem.TryGetValue(i.Id, out var latest);
            return new VarianceReportLineDto(i.Id, i.Name, i.Unit, theoretical, purchased, wastage, latest?.Variance, latest?.FinalizedAt);
        });
    }

    /// <summary>Prepared items that were sold with no Recipe on file — the doc's "this report
    /// should be zero" list.</summary>
    [HttpGet("missing-recipes")]
    public async Task<IEnumerable<MissingRecipeAlertDto>> MissingRecipes()
    {
        var alerts = await db.MissingRecipeAlerts.Where(a => !a.Dismissed).OrderByDescending(a => a.LastOccurredAt).ToListAsync();
        var menuIds = alerts.Select(a => a.MenuItemId).ToList();
        var names = await db.MenuItems.Where(m => menuIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, m => m.Name);
        return alerts.Select(a => new MissingRecipeAlertDto(a.Id, a.MenuItemId, names.TryGetValue(a.MenuItemId, out var n) ? n : "Unknown", a.OccurrenceCount, a.FirstOccurredAt, a.LastOccurredAt));
    }

    [HttpPost("missing-recipes/{id:int}/dismiss")]
    public async Task<IActionResult> DismissMissingRecipe(int id)
    {
        var alert = await db.MissingRecipeAlerts.FindAsync(id);
        if (alert is null) return NotFound();
        alert.Dismissed = true;
        await db.SaveChangesAsync();
        return NoContent();
    }
}
