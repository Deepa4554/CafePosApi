using CafePOS.Api.Data;
using CafePOS.Api.Domain;
using CafePOS.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CafePOS.Api.Controllers;

/// <summary>
/// Anonymous, TENANT-AWARE endpoints for the customer-facing QR ordering page
/// (PublicOrderPageController) — every route takes an encrypted QrToken (see
/// QrTokenService) that resolves to (tenantId, tableCode) server-side. Neither the
/// cafe's identity nor the table code ever appears in plain text in the URL/querystring
/// a customer's phone holds — only the opaque token does, and it's authenticated
/// (tamper-evident) so it can't be edited to point at a different table.
/// Everything here bypasses the normal global query filter deliberately
/// (IgnoreQueryFilters + explicit TenantId match): there's no JWT to derive a tenant
/// from, so the decoded token IS the tenant signal.
/// </summary>
[ApiController]
[Route("api/public")]
[AllowAnonymous]
public class PublicController(CafePosDbContext db, QrTokenService qrTokens, ReceiptTokenService receiptTokens) : ControllerBase
{
    /// <summary>
    /// The bill-PDF link sent over WhatsApp after an order is paid — see
    /// ReceiptTokenService for why the order id is never exposed in plain text here.
    /// Generated fresh on every request straight from the order's current DB state
    /// (see ReceiptPdfBuilder) rather than a stored file, so it can never go stale.
    /// </summary>
    [HttpGet("receipt/{token}")]
    public async Task<IActionResult> GetReceipt(string token)
    {
        var orderId = receiptTokens.TryDecode(token);
        if (orderId is null) return NotFound();

        // Payments are needed as well as Items: the scan-to-pay QR ReceiptPdfBuilder adds is
        // charged on what's still outstanding, and an unloaded collection would read as zero
        // paid and ask a part-paid guest for the whole bill again.
        var order = await db.Orders.IgnoreQueryFilters().Include(o => o.Items).ThenInclude(i => i.SelectedModifiers)
            .Include(o => o.Payments)
            .FirstOrDefaultAsync(o => o.Id == orderId.Value);
        if (order is null) return NotFound();

        var settings = await db.Settings.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.TenantId == order.TenantId);
        if (settings is null) return NotFound();

        var pdfBytes = ReceiptPdfBuilder.Build(settings, order);
        return File(pdfBytes, "application/pdf");
    }


    [HttpGet("{token}/table")]
    public async Task<ActionResult<object>> GetTable(string token)
    {
        var decoded = qrTokens.TryDecode(token);
        if (decoded is null) return NotFound();
        var (tenantId, tableCode) = decoded.Value;

        // Empty table code == the generic "menu only, no table" token (see
        // TablesController.GetMenuOnlyQrToken) — CustomerOrderPage renders this as a
        // takeaway/counter order instead of showing a table number.
        if (string.IsNullOrEmpty(tableCode))
            return new { Code = (string?)null, Zone = (string?)null, Seats = (int?)null, Occupied = false };

        var table = await db.Tables.IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.TenantId == tenantId && t.Code == tableCode);
        if (table is null) return NotFound();

        var busy = await db.Orders.IgnoreQueryFilters()
            .AnyAsync(o => o.TenantId == tenantId && o.TableCode == tableCode && !o.Cancelled && (!o.Paid || o.Status != OrderStatus.Served));
        return new { table.Code, table.Zone, table.Seats, Occupied = busy };
    }

    [HttpGet("{token}/menu-items")]
    public async Task<ActionResult<IEnumerable<MenuItem>>> GetMenu(string token)
    {
        var decoded = qrTokens.TryDecode(token);
        if (decoded is null) return NotFound();

        return Ok(await db.MenuItems.IgnoreQueryFilters()
            .Where(m => m.TenantId == decoded.Value.TenantId)
            .OrderBy(m => m.Category).ThenBy(m => m.Name)
            .ToListAsync());
    }

    [HttpGet("{token}/settings")]
    public async Task<ActionResult<CafeSettings>> GetSettings(string token)
    {
        var decoded = qrTokens.TryDecode(token);
        if (decoded is null) return NotFound();

        var settings = await db.Settings.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.TenantId == decoded.Value.TenantId);
        return settings is null ? NotFound() : settings;
    }

    private const int PublicBestSellerCount = 5;

    /// <summary>
    /// Always aims for a full row of PublicBestSellerCount items so a customer never
    /// scans into an empty/sparse "Best Sellers" strip: real units-sold in the last 30
    /// days first, then Popular-flagged items, then any other available item — so even a
    /// brand-new cafe with zero order history still shows a full row (as long as the menu
    /// itself has that many items).
    /// </summary>
    [HttpGet("{token}/best-sellers")]
    public async Task<ActionResult<IEnumerable<MenuItem>>> GetBestSellers(string token)
    {
        var decoded = qrTokens.TryDecode(token);
        if (decoded is null) return NotFound();
        var tenantId = decoded.Value.TenantId;

        var cutoff = DateTime.UtcNow.AddDays(-30);
        var sales = await (
            from oi in db.OrderItems.IgnoreQueryFilters()
            join o in db.Orders.IgnoreQueryFilters() on oi.OrderId equals o.Id
            where o.TenantId == tenantId && o.CreatedAt >= cutoff
            group oi.Qty by oi.MenuItemId into g
            select new { MenuItemId = g.Key, UnitsSold = g.Sum() })
            .OrderByDescending(x => x.UnitsSold)
            .Take(PublicBestSellerCount)
            .ToListAsync();

        var salesIds = sales.Select(s => s.MenuItemId).ToList();
        var menuById = await db.MenuItems.IgnoreQueryFilters()
            .Where(m => m.TenantId == tenantId && salesIds.Contains(m.Id))
            .ToDictionaryAsync(m => m.Id);

        var results = sales
            .Where(s => menuById.ContainsKey(s.MenuItemId))
            .Select(s => menuById[s.MenuItemId])
            .ToList();

        if (results.Count < PublicBestSellerCount)
        {
            var usedIds = results.Select(m => m.Id).ToHashSet();
            var fallback = await db.MenuItems.IgnoreQueryFilters()
                .Where(m => m.TenantId == tenantId && m.Available && !usedIds.Contains(m.Id))
                .OrderByDescending(m => m.Popular).ThenBy(m => m.Category).ThenBy(m => m.Name)
                .Take(PublicBestSellerCount - results.Count)
                .ToListAsync();
            results.AddRange(fallback);
        }

        return Ok(results);
    }
}
