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
    /// <summary>Additive optional filters for the Purchase Report — all default to "no
    /// filter" so the operational Purchase Orders screen (which never passes them) keeps
    /// working unchanged. No `branchId` param: PurchaseOrder/PurchaseItem have no BranchId
    /// column, so a purchase order can't be scoped to a branch in this schema.</summary>
    [HttpGet]
    public async Task<IEnumerable<PurchaseOrderDto>> List(
        [FromQuery] DateOnly? from = null, [FromQuery] DateOnly? to = null,
        [FromQuery] PurchaseOrderStatus? status = null, [FromQuery] int? vendorId = null)
    {
        var query = db.PurchaseOrders.AsQueryable();
        if (from is DateOnly f) query = query.Where(p => p.CreatedAt >= f.ToDateTime(TimeOnly.MinValue) - IstClock.Offset);
        if (to is DateOnly t) query = query.Where(p => p.CreatedAt < t.ToDateTime(TimeOnly.MinValue).AddDays(1) - IstClock.Offset);
        if (status is PurchaseOrderStatus st) query = query.Where(p => p.Status == st);
        if (vendorId is int vid) query = query.Where(p => p.VendorId == vid);

        var orders = await query.Include(p => p.Items).OrderByDescending(p => p.CreatedAt).ToListAsync();
        return await ToDtos(orders);
    }

    /// <summary>Places the order — records what's being asked for. Stock is untouched here;
    /// it only moves at Receive, once the goods (and their real price) are actually in hand.</summary>
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
            if (line.UnitCost is decimal c && c < 0)
                throw new ApiValidationException("Expected unit cost cannot be negative.");
        }

        Vendor? vendor = null;
        if (req.VendorId is int vendorId)
        {
            vendor = await db.Vendors.FindAsync(vendorId);
            if (vendor is null)
                throw new ApiValidationException("Selected vendor was not found.");
        }

        var order = new PurchaseOrder
        {
            VendorId = vendor?.Id,
            // Vendor name wins for display once linked; free-text stays as the fallback path.
            SupplierName = vendor?.Name ?? req.SupplierName?.Trim(),
            Note = req.Note?.Trim(),
            Status = PurchaseOrderStatus.Ordered,
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
        await db.SaveChangesAsync();
        return CreatedAtAction(nameof(List), await ToDto(order));
    }

    /// <summary>Confirms goods actually arrived — the point stock/batches/cost update. Every
    /// line on the order must be covered in one call (no partial receipt yet).</summary>
    [HttpPost("{id:int}/receive")]
    public async Task<ActionResult<PurchaseOrderDto>> Receive(int id, ReceivePurchaseOrderRequest req)
    {
        var order = await db.PurchaseOrders.Include(p => p.Items).FirstOrDefaultAsync(p => p.Id == id);
        if (order is null) return NotFound();
        if (order.Status != PurchaseOrderStatus.Ordered) throw new ApiConflictException("This purchase order has already been received or cancelled.");

        if (req.Items is null || req.Items.Count != order.Items.Count || order.Items.Any(line => req.Items.All(r => r.PurchaseItemId != line.Id)))
            throw new ApiValidationException("Every line item on the order must be received.");
        foreach (var line in req.Items)
        {
            if (line.ReceivedQuantity <= 0)
                throw new ApiValidationException("Received quantity must be greater than zero.");
            if (line.UnitCost < 0)
                throw new ApiValidationException("Unit cost cannot be negative.");
        }

        var inventoryIds = order.Items.Select(i => i.InventoryItemId).ToList();
        // Locked before the balances are read, so the weighted-average cost below and the
        // additions themselves both work off the latest committed figures and can't be
        // overwritten by a sale firing the same ingredient mid-receive — see
        // InventoryBatchService.LockIngredientsAsync.
        await InventoryBatchService.LockIngredientsAsync(db, inventoryIds);
        var inventory = await db.InventoryItems.Where(i => inventoryIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id);

        foreach (var received in req.Items)
        {
            var line = order.Items.First(l => l.Id == received.PurchaseItemId);
            var ingredient = inventory[line.InventoryItemId];

            line.ReceivedQuantity = received.ReceivedQuantity;
            line.UnitCost = received.UnitCost;
            line.ExpiryDate = received.ExpiryDate;

            var previous = ingredient.Current;
            var addedInIngredientUnit = UnitConverter.AreCompatible(line.Unit, ingredient.Unit)
                ? UnitConverter.Convert(received.ReceivedQuantity, line.Unit, ingredient.Unit)
                : received.ReceivedQuantity; // fallback: units already match in practice (same picker list)

            // Weighted-average cost: (balanceBefore*avgCostBefore + addedQty*lineCost) /
            // (balanceBefore+addedQty) — computed from the PRE-addition balance, before
            // CreateBatch bumps Current below. Falls back to the incoming cost when there's
            // no positive existing balance to average against (first-ever purchase, or a
            // balance that was at/below zero). Stays the quick "current avg cost" display
            // figure — distinct from the new batch's own real UnitCost.
            var balanceBefore = previous;
            ingredient.UnitCost = balanceBefore + addedInIngredientUnit > 0
                ? Math.Round(((decimal)balanceBefore * ingredient.UnitCost + (decimal)addedInIngredientUnit * received.UnitCost) / (decimal)(balanceBefore + addedInIngredientUnit), 4)
                : received.UnitCost;
            ingredient.LastRestockAt = DateTime.UtcNow;

            await InventoryBatchService.CreateBatchAsync(db, ingredient, addedInIngredientUnit, received.UnitCost, received.ExpiryDate,
                InventoryTransactionType.Purchase, order.Id.ToString(), CurrentUserId(), CurrentUserName());
        }

        order.Status = PurchaseOrderStatus.Received;
        order.ReceivedAt = DateTime.UtcNow;
        order.ReceivedByUserId = CurrentUserId();
        order.ReceivedByName = CurrentUserName();

        await db.SaveChangesAsync();
        return await ToDto(order);
    }

    /// <summary>Drops an order that never arrived. Ordered never touched stock, so there's
    /// nothing to reverse.</summary>
    [HttpPost("{id:int}/cancel")]
    public async Task<ActionResult<PurchaseOrderDto>> Cancel(int id)
    {
        var order = await db.PurchaseOrders.Include(p => p.Items).FirstOrDefaultAsync(p => p.Id == id);
        if (order is null) return NotFound();
        if (order.Status != PurchaseOrderStatus.Ordered) throw new ApiConflictException("Only a pending order can be cancelled.");

        order.Status = PurchaseOrderStatus.Cancelled;
        await db.SaveChangesAsync();
        return await ToDto(order);
    }

    private async Task<List<PurchaseOrderDto>> ToDtos(List<PurchaseOrder> orders)
    {
        var ids = orders.SelectMany(o => o.Items).Select(i => i.InventoryItemId).Distinct().ToList();
        var names = await db.InventoryItems.Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.Name);
        var vendorIds = orders.Where(o => o.VendorId is not null).Select(o => o.VendorId!.Value).Distinct().ToList();
        var vendorPhones = await db.Vendors.Where(v => vendorIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, v => v.Phone);
        return orders.Select(o => new PurchaseOrderDto(
            o.Id, o.VendorId, o.SupplierName,
            o.VendorId is int vid && vendorPhones.TryGetValue(vid, out var phone) ? phone : null,
            o.Note, o.Status.ToString().ToUpperInvariant(), o.CreatedByName, o.CreatedAt, o.ReceivedAt, o.ReceivedByName,
            o.Items.Select(i => new PurchaseItemDto(i.Id, i.InventoryItemId, names.TryGetValue(i.InventoryItemId, out var n) ? n : "Unknown", i.Quantity, i.Unit, i.UnitCost, i.ExpiryDate, i.ReceivedQuantity)).ToList()
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
