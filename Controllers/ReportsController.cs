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
public class ReportsController : ControllerBase
{
    private readonly CafePosDbContext db;

    public ReportsController(CafePosDbContext db)
    {
        this.db = db;
        // Every endpoint here but one is a read-only aggregation over the biggest tables in
        // the database. Change-tracking all of that cost roughly double the memory, plus a
        // change-detection pass, for entities that were never going to be written back —
        // and several reports pull tens of thousands of rows. Set once for the controller
        // instead of remembered per query; DismissMissingRecipe, the one endpoint that does
        // write, opts back in explicitly with AsTracking().
        db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
    }

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
        var (periodStart, periodEndExclusive) = ResolveIstRange(from, to, days);

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
        // AsTracking because this controller runs NoTracking by default (see the constructor)
        // — without it the flag below would be set on a detached copy and never saved.
        var alert = await db.MissingRecipeAlerts.AsTracking().FirstOrDefaultAsync(a => a.Id == id);
        if (alert is null) return NotFound();
        alert.Dismissed = true;
        await db.SaveChangesAsync();
        return NoContent();
    }

    // ---------- Owner Daily-Audit Reports ----------

    /// <summary>Same "explicit from/to wins, else a rolling N-day window ending now" shape
    /// as DashboardController.Analytics, and IST-bounded: a UTC-midnight boundary would
    /// silently start "today" at 5:30am IST, so a report asked for today would drop the
    /// after-midnight trade onto the previous day. Every dated read in this controller goes
    /// through here for that reason — Variance() used to do its own raw-UTC math and was the
    /// one report where the night's stock movement landed on the wrong day.</summary>
    private static (DateTime StartUtc, DateTime EndExclusiveUtc) ResolveIstRange(DateOnly? from, DateOnly? to, int days)
    {
        DateTime startIst, endExclusiveIst;
        if (from is not null || to is not null)
        {
            startIst = (from ?? to!.Value).ToDateTime(TimeOnly.MinValue);
            endExclusiveIst = (to ?? from!.Value).ToDateTime(TimeOnly.MinValue).AddDays(1);
        }
        else
        {
            if (days <= 0) days = 30;
            var nowIst = IstClock.NowIst;
            startIst = nowIst.AddDays(-days);
            endExclusiveIst = nowIst;
        }
        return (startIst - IstClock.Offset, endExclusiveIst - IstClock.Offset);
    }

    /// <summary>Current valuation (Current × UnitCost per ingredient) is always "as of
    /// now" — it does not shift with a past `to` date, since those are live fields, not
    /// historical snapshots. Movement columns (Opening..Closing) only populate when a date
    /// range is given, reusing the same InventoryTransactions aggregation Variance() does,
    /// plus a proper opening/closing balance lookup off each transaction's own
    /// PreviousStock/RemainingStock instead of just summing signed deltas.</summary>
    [HttpGet("stock")]
    public async Task<IEnumerable<StockReportLineDto>> Stock([FromQuery] DateOnly? from = null, [FromQuery] DateOnly? to = null, [FromQuery] int? branchId = null)
    {
        var itemsQuery = db.InventoryItems.AsQueryable();
        if (branchId is int bid) itemsQuery = itemsQuery.Where(i => i.BranchId == bid);
        var items = await itemsQuery.OrderBy(i => i.Name).ToListAsync();

        if (from is null && to is null)
        {
            return items.Select(i => new StockReportLineDto(
                i.Id, i.Name, i.Category, i.Unit, i.Current, i.UnitCost, Math.Round((decimal)i.Current * i.UnitCost, 2),
                null, null, null, null, null, null));
        }

        var (periodStartUtc, periodEndExclusiveUtc) = ResolveIstRange(from, to, 30);
        var itemIds = items.Select(i => i.Id).ToList();
        var txns = await db.InventoryTransactions
            .Where(t => itemIds.Contains(t.InventoryItemId) && t.CreatedAt < periodEndExclusiveUtc)
            .OrderBy(t => t.CreatedAt)
            .ToListAsync();

        return items.Select(i =>
        {
            var mine = txns.Where(t => t.InventoryItemId == i.Id).ToList();
            var beforeStart = mine.Where(t => t.CreatedAt < periodStartUtc).ToList();
            var inWindow = mine.Where(t => t.CreatedAt >= periodStartUtc).ToList();

            var opening = beforeStart.Count > 0 ? beforeStart[^1].RemainingStock
                : inWindow.Count > 0 ? inWindow[0].PreviousStock
                : i.Current;
            var purchased = inWindow.Where(t => t.Type == InventoryTransactionType.Purchase).Sum(t => t.ChangedQuantity);
            var sold = inWindow.Where(t => t.Type == InventoryTransactionType.Sale).Sum(t => Math.Abs(t.ChangedQuantity));
            var wasted = inWindow.Where(t => t.Type is InventoryTransactionType.Waste or InventoryTransactionType.Expired).Sum(t => Math.Abs(t.ChangedQuantity));
            var other = inWindow.Where(t => t.Type is InventoryTransactionType.ManualAdjustment or InventoryTransactionType.Return or InventoryTransactionType.Transfer).Sum(t => t.ChangedQuantity);
            var closing = mine.Count > 0 ? mine[^1].RemainingStock : i.Current;

            return new StockReportLineDto(i.Id, i.Name, i.Category, i.Unit, i.Current, i.UnitCost, Math.Round((decimal)i.Current * i.UnitCost, 2),
                opening, purchased, sold, wasted, other, closing);
        });
    }

    /// <summary>Revenue − COGS − Expenses for a period. COGS reuses FoodCost()'s exact
    /// per-menu-item ingredient-cost derivation (walk Recipe.Items → InventoryItem.UnitCost,
    /// handle ProductType.Independent via LinkedInventoryItemId) multiplied by quantity
    /// actually sold. A menu item with no recipe on file contributes zero COGS — silently
    /// understating cost — so OrdersWithoutRecipeCost surfaces that blind spot rather than
    /// hiding it (see MissingRecipeAlerts for the fix). Expenses have no BranchId column,
    /// so `branchId` only narrows the Revenue/COGS side — Expenses is always whole-tenant.</summary>
    [HttpGet("profit")]
    public async Task<ProfitReportDto> Profit([FromQuery] DateOnly? from = null, [FromQuery] DateOnly? to = null, [FromQuery] int? branchId = null, [FromQuery] int days = 30)
    {
        var (periodStartUtc, periodEndExclusiveUtc) = ResolveIstRange(from, to, days);

        var ordersQuery = db.Orders.Include(o => o.Items)
            .Where(o => o.Paid && o.CreatedAt >= periodStartUtc && o.CreatedAt < periodEndExclusiveUtc);
        if (branchId is int bid) ordersQuery = ordersQuery.Where(o => o.BranchId == bid);
        var orders = await ordersQuery.ToListAsync();
        var revenue = orders.Sum(o => o.Total);

        var menuItems = await db.MenuItems.Where(m => m.ProductType == ProductType.Prepared || m.ProductType == ProductType.Independent).ToListAsync();
        var recipes = await db.Recipes.Include(r => r.Items).ToListAsync();
        var recipeByMenuItem = recipes.ToDictionary(r => r.MenuItemId);
        var inventoryIds = recipes.SelectMany(r => r.Items).Select(ri => ri.InventoryItemId).ToHashSet();
        foreach (var m in menuItems)
            if (m.ProductType == ProductType.Independent && m.LinkedInventoryItemId is int linkedId)
                inventoryIds.Add(linkedId);
        var inventory = await db.InventoryItems.Where(i => inventoryIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id);

        var unitCostByMenuItem = new Dictionary<int, decimal>();
        foreach (var menuItem in menuItems)
        {
            decimal cost;
            if (menuItem.ProductType == ProductType.Independent)
            {
                if (menuItem.LinkedInventoryItemId is not int linkedId || !inventory.TryGetValue(linkedId, out var linked)) continue;
                cost = linked.UnitCost;
            }
            else
            {
                if (!recipeByMenuItem.TryGetValue(menuItem.Id, out var recipe)) continue;
                cost = recipe.Items.Where(ri => inventory.ContainsKey(ri.InventoryItemId)).Sum(ri =>
                {
                    var ingredient = inventory[ri.InventoryItemId];
                    var qtyInIngredientUnit = UnitConverter.Convert(ri.Quantity, ri.Unit, ingredient.Unit);
                    return Math.Round((decimal)qtyInIngredientUnit * ingredient.UnitCost, 2);
                });
            }
            unitCostByMenuItem[menuItem.Id] = cost;
        }

        decimal cogs = 0;
        var ordersWithoutRecipeCost = 0;
        foreach (var order in orders)
        {
            var missing = false;
            foreach (var item in order.Items.Where(i => !i.Voided))
            {
                if (unitCostByMenuItem.TryGetValue(item.MenuItemId, out var unitCost)) cogs += item.Qty * unitCost;
                else missing = true;
            }
            if (missing) ordersWithoutRecipeCost++;
        }

        var expenseRows = await db.CafeExpenses
            .Where(e => e.SpentAt >= periodStartUtc && e.SpentAt < periodEndExclusiveUtc)
            .ToListAsync();
        var expenses = expenseRows.Sum(e => e.Amount);

        var grossProfit = revenue - cogs;
        var netProfit = grossProfit - expenses;

        var daySpan = Math.Max(1, (int)Math.Ceiling((periodEndExclusiveUtc - periodStartUtc).TotalDays));
        var daily = Enumerable.Range(0, daySpan).Select(offset =>
        {
            var dayStartUtc = periodStartUtc.AddDays(offset);
            var dayEndExclusiveUtc = dayStartUtc.AddDays(1);
            var dayOrders = orders.Where(o => o.CreatedAt >= dayStartUtc && o.CreatedAt < dayEndExclusiveUtc).ToList();
            var dayRevenue = dayOrders.Sum(o => o.Total);
            var dayCogs = dayOrders.SelectMany(o => o.Items).Where(i => !i.Voided)
                .Sum(i => unitCostByMenuItem.TryGetValue(i.MenuItemId, out var uc) ? i.Qty * uc : 0);
            var dayExpenses = expenseRows.Where(e => e.SpentAt >= dayStartUtc && e.SpentAt < dayEndExclusiveUtc).Sum(e => e.Amount);
            return new ProfitDayLineDto(IstClock.ToIst(dayStartUtc).ToString("d MMM"), dayRevenue, dayCogs, dayExpenses);
        }).ToList();

        return new ProfitReportDto(revenue, cogs, grossProfit, expenses, netProfit, ordersWithoutRecipeCost, daily);
    }

    /// <summary>Item/category/payment-mode breakdown for a period. Payment-mode-wise MUST
    /// read Order.Payments/OrderPayment.Method+Amount (one row per tender), not the summary
    /// Order.PaymentMethod string — that's the only correct source once a split payment
    /// exists. Discounts/refunds are folded into this report's summary line rather than a
    /// dedicated screen — a judgment call, not a technical limit.</summary>
    [HttpGet("sales")]
    public async Task<SalesReportDto> Sales([FromQuery] DateOnly? from = null, [FromQuery] DateOnly? to = null, [FromQuery] int? branchId = null, [FromQuery] int days = 30)
    {
        var (periodStartUtc, periodEndExclusiveUtc) = ResolveIstRange(from, to, days);

        var ordersQuery = db.Orders.Include(o => o.Items).Include(o => o.Payments)
            .Where(o => o.Paid && o.CreatedAt >= periodStartUtc && o.CreatedAt < periodEndExclusiveUtc);
        if (branchId is int bid) ordersQuery = ordersQuery.Where(o => o.BranchId == bid);
        var orders = await ordersQuery.ToListAsync();

        var grossSales = orders.Sum(o => o.Subtotal);
        var totalDiscounts = orders.Sum(o => o.DiscountAmount + o.BillDiscountAmount + o.CouponDiscountAmount + o.GiftCardAmountApplied + o.LoyaltyDiscountAmount);
        var netSales = orders.Sum(o => o.Total);
        var refundsTotal = orders.Where(o => o.Refunded).Sum(o => o.RefundedAmount ?? 0m);

        var allItems = orders.SelectMany(o => o.Items).Where(i => !i.Voided).ToList();

        var itemWise = allItems.GroupBy(i => new { i.MenuItemId, i.Name })
            .Select(g => new SalesItemLineDto(g.Key.MenuItemId, g.Key.Name, g.Sum(i => i.Qty), g.Sum(i => i.Price * i.Qty)))
            .OrderByDescending(x => x.NetSales)
            .ToList();

        var menuItemIds = allItems.Select(i => i.MenuItemId).Distinct().ToList();
        var categoryByMenuItem = await db.MenuItems.Where(m => menuItemIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, m => m.Category);
        var categoryWise = allItems.GroupBy(i => categoryByMenuItem.GetValueOrDefault(i.MenuItemId, "Uncategorized"))
            .Select(g => new SalesCategoryLineDto(g.Key, g.Sum(i => i.Qty), g.Sum(i => i.Price * i.Qty)))
            .OrderByDescending(x => x.NetSales)
            .ToList();

        var paymentModeWise = orders.SelectMany(o => o.Payments)
            .GroupBy(p => p.Method)
            .Select(g => new SalesPaymentLineDto(g.Key, g.Sum(p => p.Amount), g.Count()))
            .OrderByDescending(x => x.Amount)
            .ToList();

        return new SalesReportDto(grossSales, totalDiscounts, netSales, refundsTotal, orders.Count, itemWise, categoryWise, paymentModeWise);
    }

    /// <summary>Rate-wise collected tax for a period — groups by OrderItem.TaxRatePct
    /// (falling back to CafeSettings.TaxRatePct for lines with no snapshotted rate, same
    /// resolution RecomputeTotals itself uses). TotalTaxCollected should reconcile to the
    /// same-period sum of Order.Tax.</summary>
    [HttpGet("tax-gst")]
    public async Task<TaxGstReportDto> TaxGst([FromQuery] DateOnly? from = null, [FromQuery] DateOnly? to = null, [FromQuery] int? branchId = null, [FromQuery] int days = 30)
    {
        var (periodStartUtc, periodEndExclusiveUtc) = ResolveIstRange(from, to, days);

        var ordersQuery = db.Orders.Include(o => o.Items)
            .Where(o => o.Paid && o.CreatedAt >= periodStartUtc && o.CreatedAt < periodEndExclusiveUtc);
        if (branchId is int bid) ordersQuery = ordersQuery.Where(o => o.BranchId == bid);
        var orders = await ordersQuery.ToListAsync();

        var settings = await db.Settings.FirstOrDefaultAsync();
        var defaultRate = settings?.TaxRatePct ?? 8;

        var byRate = orders.SelectMany(o => o.Items).Where(i => !i.Voided)
            .GroupBy(i => i.TaxRatePct ?? defaultRate)
            .Select(g => new TaxRateLineDto(g.Key, g.Sum(i => i.TaxableAmount), g.Sum(i => i.TaxAmount), g.Count()))
            .OrderByDescending(x => x.RatePct)
            .ToList();

        var bills = orders
            .Select(o => new TaxBillLineDto(
                o.Id, o.Title, o.CreatedAt,
                o.Items.Where(i => !i.Voided).Sum(i => i.TaxableAmount),
                o.Items.Where(i => !i.Voided).Sum(i => i.TaxAmount)))
            .Where(b => b.TaxAmount > 0)
            .OrderByDescending(b => b.CreatedAt)
            .ToList();

        return new TaxGstReportDto(byRate.Sum(x => x.TaxableAmount), byRate.Sum(x => x.TaxAmount), byRate, bills);
    }

    /// <summary>Bill-wise register — one row per order with its line items, the transaction-level
    /// counterpart to Sales()'s aggregates. Unpaid orders are included (an owner auditing a day
    /// wants to see what's still open), which is why the caller gets Paid/Refunded flags rather
    /// than a pre-filtered list.</summary>
    [HttpGet("orders")]
    public async Task<OrdersReportDto> Orders(
        [FromQuery] DateOnly? from = null, [FromQuery] DateOnly? to = null, [FromQuery] int? branchId = null,
        [FromQuery] int days = 30, [FromQuery] string? orderType = null, [FromQuery] string? paymentMethod = null)
    {
        var (periodStartUtc, periodEndExclusiveUtc) = ResolveIstRange(from, to, days);

        var query = db.Orders.Include(o => o.Items).Include(o => o.Customer)
            .Where(o => o.CreatedAt >= periodStartUtc && o.CreatedAt < periodEndExclusiveUtc);
        if (branchId is int bid) query = query.Where(o => o.BranchId == bid);
        if (!string.IsNullOrWhiteSpace(orderType)) query = query.Where(o => o.OrderType == orderType);
        if (!string.IsNullOrWhiteSpace(paymentMethod)) query = query.Where(o => o.PaymentMethod == paymentMethod);

        var totalCount = await query.CountAsync();
        var orders = await query
            .OrderByDescending(o => o.CreatedAt)
            .Take(OrdersReportDto.MaxRows)
            .ToListAsync();

        var lines = orders.Select(o =>
        {
            var discountTotal = o.DiscountAmount + o.BillDiscountAmount + o.CouponDiscountAmount
                              + o.GiftCardAmountApplied + o.LoyaltyDiscountAmount;
            return new OrderDetailLineDto(
                o.Id, o.Title, o.CreatedAt, o.OrderType, o.TableCode, o.TokenNumber,
                o.Customer?.Name ?? o.GuestName, o.Customer?.Phone ?? o.GuestPhone,
                o.Subtotal, discountTotal, o.Tax, o.Total,
                o.PaymentMethod, o.Paid, o.Refunded, o.RefundedAmount,
                o.Items.Count(i => !i.Voided),
                o.Items.Select(i => new OrderDetailItemDto(
                    i.Name, i.VariantName, i.Qty, i.Price, i.Price * i.Qty, i.TaxAmount, i.Voided)).ToList());
        }).ToList();

        return new OrdersReportDto(
            totalCount,
            lines.Sum(l => l.Subtotal),
            lines.Sum(l => l.DiscountTotal),
            lines.Sum(l => l.Tax),
            lines.Sum(l => l.Total),
            lines.Where(l => l.Refunded).Sum(l => l.RefundedAmount ?? 0m),
            totalCount > OrdersReportDto.MaxRows,
            lines);
    }

    /// <summary>Who the cafe's customers are and what they're worth. The headline split is
    /// identified (Order.CustomerId set) vs walk-in — an owner can only market to the former,
    /// so the ratio matters more than either total alone. Lapsed = no visit in 60+ days, a
    /// win-back list rather than a period metric. Customer has no BranchId, so `branchId`
    /// narrows the period figures (which come from Orders) but never the lifetime ones.</summary>
    [HttpGet("crm")]
    public async Task<CrmReportDto> Crm([FromQuery] DateOnly? from = null, [FromQuery] DateOnly? to = null, [FromQuery] int? branchId = null, [FromQuery] int days = 30)
    {
        var (periodStartUtc, periodEndExclusiveUtc) = ResolveIstRange(from, to, days);

        var ordersQuery = db.Orders.Where(o => o.Paid && o.CreatedAt >= periodStartUtc && o.CreatedAt < periodEndExclusiveUtc);
        if (branchId is int bid) ordersQuery = ordersQuery.Where(o => o.BranchId == bid);
        var orders = await ordersQuery
            .Select(o => new { o.CustomerId, o.Total, o.LoyaltyPointsRedeemed })
            .ToListAsync();

        var identified = orders.Where(o => o.CustomerId != null).ToList();
        var revenueFromCustomers = identified.Sum(o => o.Total);
        var revenueFromWalkIns = orders.Where(o => o.CustomerId == null).Sum(o => o.Total);
        var totalRevenue = revenueFromCustomers + revenueFromWalkIns;

        var perCustomer = identified
            .GroupBy(o => o.CustomerId!.Value)
            .ToDictionary(g => g.Key, g => new { Visits = g.Count(), Spent = g.Sum(o => o.Total) });

        var nowIst = IstClock.NowIst;
        var lapsedCutoffUtc = (nowIst - TimeSpan.FromDays(60)) - IstClock.Offset;

        // These two are whole-table figures, but they're both plain aggregates — computing
        // them in SQL keeps this report's memory flat as the customer list grows, instead of
        // pulling every row (with its notes and addresses) into the API just to add up two
        // integer columns. AvailablePoints/the tier being computed properties is why the
        // ROWS still have to be materialised below, but only the active ones need to be.
        var pointsOutstanding = await db.Customers.SumAsync(c => c.TotalPoints - c.RedeemedPoints);
        var lapsedCustomers = await db.Customers.CountAsync(c => c.LastVisitAt < lapsedCutoffUtc);

        var activeIds = perCustomer.Keys.ToList();
        var activeCustomers = await db.Customers
            .Where(c => activeIds.Contains(c.Id))
            .ToListAsync();
        var newCustomers = activeCustomers.Count(c => c.JoinedAt >= periodStartUtc && c.JoinedAt < periodEndExclusiveUtc);
        var returningCustomers = activeCustomers.Count - newCustomers;

        var lines = activeCustomers.Select(c =>
        {
            var stats = perCustomer[c.Id];
            return new CrmReportCustomerLineDto(
                c.Id, c.Name, c.Phone,
                CustomerSummaryDto.TierFor(c.TotalPoints).ToString().ToUpperInvariant(),
                stats.Visits, stats.Spent,
                stats.Visits > 0 ? Math.Round(stats.Spent / stats.Visits, 2) : 0m,
                c.VisitCount, c.TotalSpent, c.AvailablePoints,
                c.LastVisitAt, c.JoinedAt,
                c.JoinedAt >= periodStartUtc && c.JoinedAt < periodEndExclusiveUtc);
        })
        .OrderByDescending(l => l.SpentInPeriod)
        .ToList();

        return new CrmReportDto(
            activeCustomers.Count, newCustomers, returningCustomers,
            activeCustomers.Count > 0 ? Math.Round(returningCustomers * 100.0 / activeCustomers.Count, 1) : 0,
            lapsedCustomers,
            revenueFromCustomers, revenueFromWalkIns,
            totalRevenue > 0 ? Math.Round((double)(revenueFromCustomers / totalRevenue) * 100, 1) : 0,
            activeCustomers.Count > 0 ? Math.Round(revenueFromCustomers / activeCustomers.Count, 2) : 0m,
            activeCustomers.Count > 0 ? Math.Round((double)identified.Count / activeCustomers.Count, 2) : 0,
            identified.Sum(o => o.LoyaltyPointsRedeemed), pointsOutstanding,
            lines);
    }

    // ---------- HR Reports ----------

    [HttpGet("daily-attendance")]
    public async Task<List<DailyAttendanceReportLineDto>> DailyAttendance([FromQuery] DateOnly date)
    {
        var staffList = await db.Staff.Where(s => s.Status != StaffStatus.Terminated).OrderBy(s => s.Name).ToListAsync();
        var records = await db.AttendanceRecords.Where(a => a.Date == date).ToListAsync();
        var recordByStaff = records.ToDictionary(r => r.StaffId);

        var dayStart = date.ToDateTime(TimeOnly.MinValue);
        var dayEnd = dayStart.AddDays(1);
        var shiftStaffIds = (await db.Shifts.Where(s => s.StartsAt < dayEnd && s.EndsAt > dayStart).Select(s => s.StaffId).ToListAsync()).ToHashSet();
        var leaveStaffIds = (await db.LeaveRequests.Where(l => l.Status == LeaveRequestStatus.Approved && l.StartDate <= date && l.EndDate >= date).Select(l => l.StaffId).ToListAsync()).ToHashSet();

        var lines = new List<DailyAttendanceReportLineDto>();
        foreach (var s in staffList)
        {
            if (recordByStaff.TryGetValue(s.Id, out var r))
                lines.Add(new DailyAttendanceReportLineDto(s.Id, s.Name, s.Role, date, r.Status.ToString().ToUpperInvariant(), r.PunchInAt, r.PunchOutAt, r.WorkedMinutes, r.LateMinutes));
            else if (leaveStaffIds.Contains(s.Id))
                lines.Add(new DailyAttendanceReportLineDto(s.Id, s.Name, s.Role, date, "ON_LEAVE", null, null, null, 0));
            else if (shiftStaffIds.Contains(s.Id))
                lines.Add(new DailyAttendanceReportLineDto(s.Id, s.Name, s.Role, date, "ABSENT", null, null, null, 0));
        }
        return lines;
    }

    [HttpGet("daily-attendance/export")]
    public async Task<IActionResult> DailyAttendanceExport([FromQuery] DateOnly date)
    {
        var lines = await DailyAttendance(date);
        var headers = new[] { "Staff", "Role", "Date", "Status", "Punch In", "Punch Out", "Worked Minutes", "Late Minutes" };
        var rows = lines.Select(l => (IEnumerable<object?>)[l.StaffName, l.Role, l.Date.ToString(), l.Status, l.PunchInAt, l.PunchOutAt, l.WorkedMinutes, l.LateMinutes]);
        return File(CsvBuilder.Build(headers, rows), "text/csv", $"daily-attendance-{date}.csv");
    }

    [HttpGet("monthly-attendance")]
    public async Task<List<MonthlyAttendanceReportLineDto>> MonthlyAttendance([FromQuery] int year, [FromQuery] int month)
    {
        var periodStart = new DateOnly(year, month, 1);
        var periodEnd = periodStart.AddMonths(1).AddDays(-1);
        var periodEndExclusive = periodEnd.ToDateTime(TimeOnly.MinValue).AddDays(1);
        var periodStartInclusive = periodStart.ToDateTime(TimeOnly.MinValue);

        var staffList = await db.Staff.Where(s => s.Status != StaffStatus.Terminated).OrderBy(s => s.Name).ToListAsync();
        var records = await db.AttendanceRecords.Where(a => a.Date >= periodStart && a.Date <= periodEnd).ToListAsync();
        var leaves = await db.LeaveRequests.Where(l => l.Status == LeaveRequestStatus.Approved && l.StartDate <= periodEnd && l.EndDate >= periodStart).ToListAsync();
        var shifts = await db.Shifts.Where(s => s.StartsAt < periodEndExclusive && s.EndsAt >= periodStartInclusive).Select(s => new { s.StaffId, s.StartsAt }).ToListAsync();

        return staffList.Select(s =>
        {
            var mine = records.Where(r => r.StaffId == s.Id).ToList();
            var presentDays = mine.Count(r => r.WorkedMinutes.HasValue);
            var lateDays = mine.Count(r => r.Status == AttendanceStatus.Late);
            var halfDays = mine.Count(r => r.Status == AttendanceStatus.HalfDay);
            var totalHours = mine.Sum(r => r.WorkedMinutes ?? 0) / 60.0;

            var attendedDates = mine.Where(r => r.WorkedMinutes.HasValue).Select(r => r.Date).ToHashSet();
            var leaveDates = new HashSet<DateOnly>();
            var leaveDays = 0;
            foreach (var l in leaves.Where(l => l.StaffId == s.Id))
            {
                var os = l.StartDate > periodStart ? l.StartDate : periodStart;
                var oe = l.EndDate < periodEnd ? l.EndDate : periodEnd;
                if (oe < os) continue;
                leaveDays += oe.DayNumber - os.DayNumber + 1;
                for (var d = os; d <= oe; d = d.AddDays(1)) leaveDates.Add(d);
            }
            var scheduledDates = shifts.Where(sh => sh.StaffId == s.Id).Select(sh => DateOnly.FromDateTime(sh.StartsAt)).Distinct().ToList();
            var absentDays = scheduledDates.Count(d => !attendedDates.Contains(d) && !leaveDates.Contains(d));

            return new MonthlyAttendanceReportLineDto(s.Id, s.Name, s.Role, presentDays, lateDays, halfDays, absentDays, leaveDays, totalHours);
        }).ToList();
    }

    [HttpGet("monthly-attendance/export")]
    public async Task<IActionResult> MonthlyAttendanceExport([FromQuery] int year, [FromQuery] int month)
    {
        var lines = await MonthlyAttendance(year, month);
        var headers = new[] { "Staff", "Role", "Present Days", "Late Days", "Half Days", "Absent Days", "Leave Days", "Total Hours" };
        var rows = lines.Select(l => (IEnumerable<object?>)[l.StaffName, l.Role, l.PresentDays, l.LateDays, l.HalfDays, l.AbsentDays, l.LeaveDays, l.TotalWorkedHours.ToString("0.0")]);
        return File(CsvBuilder.Build(headers, rows), "text/csv", $"monthly-attendance-{year}-{month:00}.csv");
    }

    [HttpGet("salary-register")]
    public async Task<ActionResult<IEnumerable<PayrollLineDto>>> SalaryRegister([FromQuery] int payrollRunId)
    {
        var run = await db.PayrollRuns.Include(r => r.Lines).FirstOrDefaultAsync(r => r.Id == payrollRunId);
        if (run is null) return NotFound();
        return Ok(run.Lines.OrderBy(l => l.StaffName).Select(PayrollLineDto.From));
    }

    [HttpGet("salary-register/export")]
    public async Task<IActionResult> SalaryRegisterExport([FromQuery] int payrollRunId)
    {
        var run = await db.PayrollRuns.Include(r => r.Lines).FirstOrDefaultAsync(r => r.Id == payrollRunId);
        if (run is null) return NotFound();
        var lines = run.Lines.OrderBy(l => l.StaffName).ToList();
        var headers = new[] { "Staff", "Salary Type", "Basic", "Overtime Pay", "Allowances", "Gross", "Deductions", "Net Salary" };
        var rows = lines.Select(l => (IEnumerable<object?>)[
            l.StaffName, l.SalaryType.ToString(), l.BasicSalary.ToString("0.00"), l.OvertimePay.ToString("0.00"),
            l.AllowancesTotal.ToString("0.00"), l.GrossEarnings.ToString("0.00"), l.TotalDeductions.ToString("0.00"), l.NetSalary.ToString("0.00")]);
        return File(CsvBuilder.Build(headers, rows), "text/csv", $"salary-register-{run.PeriodStart:yyyyMM}.csv");
    }

    [HttpGet("leave")]
    public async Task<List<LeaveRequestDto>> LeaveReport([FromQuery] DateOnly? periodStart, [FromQuery] DateOnly? periodEnd, [FromQuery] LeaveRequestStatus? status)
    {
        var query = db.LeaveRequests.AsQueryable();
        if (periodStart is DateOnly ps) query = query.Where(l => l.EndDate >= ps);
        if (periodEnd is DateOnly pe) query = query.Where(l => l.StartDate <= pe);
        if (status is LeaveRequestStatus st) query = query.Where(l => l.Status == st);
        var results = await query.OrderByDescending(l => l.CreatedAt).ToListAsync();
        return results.Select(LeaveRequestDto.From).ToList();
    }

    [HttpGet("leave/export")]
    public async Task<IActionResult> LeaveReportExport([FromQuery] DateOnly? periodStart, [FromQuery] DateOnly? periodEnd, [FromQuery] LeaveRequestStatus? status)
    {
        var lines = await LeaveReport(periodStart, periodEnd, status);
        var headers = new[] { "Staff", "Type", "Start", "End", "Status", "Reason", "Reviewed By" };
        var rows = lines.Select(l => (IEnumerable<object?>)[l.StaffName, l.Type, l.StartDate.ToString(), l.EndDate.ToString(), l.Status, l.Reason, l.ReviewedByName]);
        return File(CsvBuilder.Build(headers, rows), "text/csv", "leave-report.csv");
    }

    [HttpGet("overtime")]
    public async Task<List<OvertimeReportLineDto>> OvertimeReport([FromQuery] DateOnly periodStart, [FromQuery] DateOnly periodEnd)
    {
        var staffList = await db.Staff.Where(s => s.Status != StaffStatus.Terminated).ToListAsync();
        var records = await db.AttendanceRecords.Where(a => a.Date >= periodStart && a.Date <= periodEnd && a.OvertimeMinutes > 0).ToListAsync();
        return staffList
            .Select(s =>
            {
                var mine = records.Where(r => r.StaffId == s.Id).ToList();
                return new OvertimeReportLineDto(s.Id, s.Name, s.Role, mine.Sum(r => r.OvertimeMinutes) / 60.0, mine.Count);
            })
            .Where(x => x.OvertimeDays > 0)
            .OrderByDescending(x => x.TotalOvertimeHours)
            .ToList();
    }

    [HttpGet("overtime/export")]
    public async Task<IActionResult> OvertimeReportExport([FromQuery] DateOnly periodStart, [FromQuery] DateOnly periodEnd)
    {
        var lines = await OvertimeReport(periodStart, periodEnd);
        var headers = new[] { "Staff", "Role", "Total Overtime Hours", "Overtime Days" };
        var rows = lines.Select(l => (IEnumerable<object?>)[l.StaffName, l.Role, l.TotalOvertimeHours.ToString("0.0"), l.OvertimeDays]);
        return File(CsvBuilder.Build(headers, rows), "text/csv", $"overtime-{periodStart}-to-{periodEnd}.csv");
    }

    [HttpGet("employee-list")]
    public async Task<List<EmployeeListLineDto>> EmployeeList()
    {
        var staffList = await db.Staff.OrderBy(s => s.Name).ToListAsync();
        var branchIds = staffList.Where(s => s.BranchId != null).Select(s => s.BranchId!.Value).Distinct().ToList();
        var branches = await db.Branches.Where(b => branchIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, b => b.Name);
        return staffList.Select(s => new EmployeeListLineDto(
            s.Id, s.Name, s.Role, s.Department, s.Designation, s.BranchId is int bid ? branches.GetValueOrDefault(bid) : null,
            s.JoinedAt, s.Status.ToString().ToUpperInvariant(), s.SalaryType.ToString().ToUpperInvariant(), s.BasicSalary, s.HourlyRate, s.UserId != null)).ToList();
    }

    [HttpGet("employee-list/export")]
    public async Task<IActionResult> EmployeeListExport()
    {
        var lines = await EmployeeList();
        var headers = new[] { "Name", "Role", "Department", "Designation", "Branch", "Joined", "Status", "Salary Type", "Basic Salary", "Hourly Rate", "Has Login" };
        var rows = lines.Select(l => (IEnumerable<object?>)[
            l.Name, l.Role, l.Department, l.Designation, l.BranchName, l.JoinedAt.ToString("yyyy-MM-dd"), l.Status, l.SalaryType, l.BasicSalary, l.HourlyRate, l.HasLogin]);
        return File(CsvBuilder.Build(headers, rows), "text/csv", "employee-list.csv");
    }
}
