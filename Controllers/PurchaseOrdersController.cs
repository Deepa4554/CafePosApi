using CafePOS.Api.Contracts;
using CafePOS.Api.Data;
using CafePOS.Api.Domain;
using CafePOS.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CafePOS.Api.Controllers;

/// <summary>Multi-line stock purchases — a genuine batch document (supplier, line items,
/// who created it), unlike single-item restock/waste/adjust which just log ledger rows.</summary>
[ApiController]
[Route("api/purchase-orders")]
[Authorize(Policy = Policies.OwnerOrManager)]
[Authorize(Policy = Policies.RequirePlus)]
public class PurchaseOrdersController(CafePosDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IEnumerable<PurchaseOrderDto>> List()
    {
        var orders = await db.PurchaseOrders.Include(p => p.Items).OrderByDescending(p => p.CreatedAt).ToListAsync();
        return await ToDtos(orders);
    }

    [HttpPost]
    public async Task<ActionResult<PurchaseOrderDto>> Create(CreatePurchaseOrderRequest req)
    {
        if (req.Items is null || req.Items.Count == 0)
            throw new ApiValidationException("A purchase order needs at least one line item.");

        var inventoryIds = req.Items.Select(i => i.InventoryItemId).ToList();
        var inventory = await db.InventoryItems.Where(i => inventoryIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id);
        foreach (var line in req.Items)
        {
            if (!inventory.ContainsKey(line.InventoryItemId))
                throw new ApiValidationException($"Ingredient {line.InventoryItemId} not found.");
            if (line.Quantity <= 0)
                throw new ApiValidationException("Purchase quantity must be greater than zero.");
        }

        var order = new PurchaseOrder
        {
            SupplierName = req.SupplierName?.Trim(),
            Note = req.Note?.Trim(),
            CreatedByUserId = CurrentUserId() ?? 0,
            CreatedByName = CurrentUserName(),
            Items = req.Items.Select(i => new PurchaseItem
            {
                InventoryItemId = i.InventoryItemId,
                Quantity = i.Quantity,
                Unit = i.Unit.Trim(),
                UnitCost = i.UnitCost,
            }).ToList(),
        };
        db.PurchaseOrders.Add(order);
        await db.SaveChangesAsync(); // assigns order.Id for the ledger ReferenceId below

        foreach (var line in order.Items)
        {
            var ingredient = inventory[line.InventoryItemId];
            var previous = ingredient.Current;
            var addedInIngredientUnit = UnitConverter.AreCompatible(line.Unit, ingredient.Unit)
                ? UnitConverter.Convert(line.Quantity, line.Unit, ingredient.Unit)
                : line.Quantity; // fallback: units already match in practice (same picker list)
            ingredient.Current += addedInIngredientUnit;
            ingredient.UnitCost = line.UnitCost;
            ingredient.LastRestockAt = DateTime.UtcNow;

            db.InventoryTransactions.Add(new InventoryTransaction
            {
                InventoryItemId = ingredient.Id,
                Type = InventoryTransactionType.Purchase,
                PreviousStock = previous,
                ChangedQuantity = addedInIngredientUnit,
                RemainingStock = ingredient.Current,
                ReferenceId = order.Id.ToString(),
                UserId = CurrentUserId(),
                UserName = CurrentUserName(),
            });
        }

        await db.SaveChangesAsync();
        return CreatedAtAction(nameof(List), await ToDto(order));
    }

    private async Task<List<PurchaseOrderDto>> ToDtos(List<PurchaseOrder> orders)
    {
        var ids = orders.SelectMany(o => o.Items).Select(i => i.InventoryItemId).Distinct().ToList();
        var names = await db.InventoryItems.Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.Name);
        return orders.Select(o => new PurchaseOrderDto(
            o.Id, o.SupplierName, o.Note, o.CreatedByName, o.CreatedAt,
            o.Items.Select(i => new PurchaseItemDto(i.InventoryItemId, names.TryGetValue(i.InventoryItemId, out var n) ? n : "Unknown", i.Quantity, i.Unit, i.UnitCost)).ToList()
        )).ToList();
    }

    private async Task<PurchaseOrderDto> ToDto(PurchaseOrder order) => (await ToDtos([order]))[0];

    private int? CurrentUserId()
    {
        var claim = User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
        return int.TryParse(claim, out var id) ? id : null;
    }

    private string CurrentUserName() => User.Identity?.Name ?? "Cafe Staff";
}
