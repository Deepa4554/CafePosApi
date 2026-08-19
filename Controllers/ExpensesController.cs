using System.Text.Json;
using CafePOS.Api.Contracts;
using CafePOS.Api.Data;
using CafePOS.Api.Domain;
using CafePOS.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CafePOS.Api.Controllers;

/// <summary>A cafe's own day-to-day running costs (rent, salaries, utilities, ...) —
/// tenant-scoped bookkeeping, not to be confused with SuperAdminController's
/// PlatformExpense endpoints (CafePOS-the-startup's own books).</summary>
[ApiController]
[Route("api/expenses")]
[Authorize(Policy = Policies.OwnerOrManager)]
// ExpenseReport too — the Expense Report screen reads the same rows.
[RequireScreen("Expenses", "ExpenseReport")]
public class ExpensesController(CafePosDbContext db) : ControllerBase
{
    /// <summary>The tenders an expense can be paid as — shared by both entry paths now (the
    /// daily sheet and the one-off Add Expense form). Kept separate from KhatabookController/
    /// TiffinController's own copies rather than shared, so adding a tender to one is a
    /// deliberate decision for the others too — same reasoning as theirs.
    ///
    /// "Due" means the cafe took the goods on udhaar and hasn't paid the vendor yet. It's a
    /// label on the expense and nothing more: the row still lands on the books at full amount on
    /// the day it was incurred, and no Khatabook entry is opened — that book tracks what
    /// CUSTOMERS owe the cafe, not what the cafe owes its vendors, which would need its own
    /// ledger before "Due" here could mean anything settle-able.</summary>
    private static readonly HashSet<string> ValidPaymentModes =
        new(StringComparer.OrdinalIgnoreCase) { "Cash", "UPI", "Card", "Due" };

    /// <summary>What a row carrying no mode at all is bucketed under. Rows written before the
    /// column existed (and any an older client still saves without one) get their own bucket
    /// rather than being folded into Cash, which would overstate the till by exactly the
    /// amount nobody has actually classified yet.</summary>
    private const string UnsetPaymentMode = "Not set";

    /// <summary>Validates a submitted mode and returns it in ValidPaymentModes' canonical
    /// casing, so "upi" and "UPI" can't become two separate buckets in the mode-wise totals.
    /// Null/blank passes straight through as null — each caller decides for itself whether that
    /// means "default to Cash" (the daily sheet, where every filled row was paid somehow) or
    /// "leave unset" (Add Expense, which is free to not ask).</summary>
    private static string? NormalizePaymentMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode)) return null;
        var match = ValidPaymentModes.FirstOrDefault(m => m.Equals(mode.Trim(), StringComparison.OrdinalIgnoreCase));
        if (match is null)
            throw new ApiValidationException($"\"{mode}\" isn't a payment mode this screen knows — use {string.Join(", ", ValidPaymentModes)}.");
        return match;
    }

    private static List<PaymentModeTotalDto> ByPaymentMode(IEnumerable<CafeExpense> rows) => rows
        .GroupBy(e => string.IsNullOrWhiteSpace(e.PaymentMode) ? UnsetPaymentMode : e.PaymentMode)
        .Select(g => new PaymentModeTotalDto(g.Key, g.Sum(e => e.Amount)))
        .OrderByDescending(m => m.Total)
        .ToList();

    [HttpGet]
    public async Task<CafeExpenseSummaryDto> List()
    {
        var all = await db.CafeExpenses.OrderByDescending(e => e.SpentAt).ToListAsync();
        var monthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var thisMonth = all.Where(e => e.SpentAt >= monthStart).ToList();

        var byCategory = thisMonth
            .GroupBy(e => e.Category)
            .Select(g => new CategoryTotalDto(g.Key.ToString(), g.Sum(e => e.Amount)))
            .OrderByDescending(c => c.Total)
            .ToList();

        return new CafeExpenseSummaryDto(
            all.Sum(e => e.Amount),
            thisMonth.Sum(e => e.Amount),
            byCategory,
            ByPaymentMode(thisMonth),
            all.Select(CafeExpenseDto.From).ToList());
    }

    /// <summary>Date-ranged view for the Expense Report — a separate action (not new params
    /// on List() above) so it can carry its own [Authorize(RequirePlus)] on top of this
    /// controller's Normal-plan List()/Create()/Delete(), the same method-level layering
    /// trick DashboardController.Forecast() uses (ASP.NET Core [Authorize] policies are
    /// additive-only — List() can't be "downgraded" back off Plus per-action, so gating just
    /// the reporting view means a new action, not new params on the existing one). No
    /// BranchId column on CafeExpense — always a whole-tenant total, no branch filter.</summary>
    [HttpGet("report")]
    [Authorize(Policy = Policies.RequirePlus)]
    public async Task<CafeExpenseReportDto> Report([FromQuery] DateOnly? from = null, [FromQuery] DateOnly? to = null)
    {
        var query = db.CafeExpenses.AsQueryable();
        if (from is DateOnly f) query = query.Where(e => e.SpentAt >= f.ToDateTime(TimeOnly.MinValue) - IstClock.Offset);
        if (to is DateOnly t) query = query.Where(e => e.SpentAt < t.ToDateTime(TimeOnly.MinValue).AddDays(1) - IstClock.Offset);
        var rows = await query.OrderByDescending(e => e.SpentAt).ToListAsync();

        var byCategory = rows
            .GroupBy(e => e.Category)
            .Select(g => new CategoryTotalDto(g.Key.ToString(), g.Sum(e => e.Amount)))
            .OrderByDescending(c => c.Total)
            .ToList();

        return new CafeExpenseReportDto(rows.Sum(e => e.Amount), byCategory, ByPaymentMode(rows), rows.Select(CafeExpenseDto.From).ToList());
    }

    [HttpPost]
    public async Task<ActionResult<CafeExpenseDto>> Create(CreateCafeExpenseRequest req)
    {
        if (req.Amount <= 0)
            throw new ApiValidationException("Amount must be greater than 0.");
        if (string.IsNullOrWhiteSpace(req.Purpose))
            throw new ApiValidationException("Enter what this expense was for.");
        if (string.IsNullOrWhiteSpace(req.SpentBy))
            throw new ApiValidationException("Enter who this was spent by.");
        // Checked before the approval branch below on purpose: a bad mode frozen into an
        // ApprovalRequest's PayloadJson would only blow up days later at approve time, in
        // front of an Owner who can't fix it and didn't type it.
        var paymentMode = NormalizePaymentMode(req.PaymentMode);

        var idClaim = User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value
            ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        var recordedBy = await db.Users.FindAsync(int.Parse(idClaim!));

        // Owner bypasses always — they ARE the approver. A Manager over threshold needs
        // Owner sign-off instead of the expense landing on the books immediately. Nothing
        // to link to yet (no CafeExpense row exists until approved), so the fields needed
        // to create it are carried in PayloadJson instead — see ApprovalsController.Approve.
        if (!User.IsInRole(nameof(AppRole.Owner)) && req.Amount > ApprovalThresholds.ExpenseAmount)
        {
            db.Approvals.Add(new ApprovalRequest
            {
                Type = ApprovalType.Expense,
                RequestedById = recordedBy?.Id ?? 0,
                Title = $"Expense — {req.Purpose}",
                Description = $"{req.Category} · spent by {req.SpentBy}",
                Amount = req.Amount,
                PayloadJson = JsonSerializer.Serialize(req),
            });
            await db.SaveChangesAsync();
            return Accepted(new { pendingApproval = true, message = $"Expense of {req.Amount:C} needs Owner approval (above the {ApprovalThresholds.ExpenseAmount:C} auto-approve limit) — sent to Approvals." });
        }

        var expense = new CafeExpense
        {
            Amount = req.Amount,
            Category = req.Category,
            Purpose = req.Purpose.Trim(),
            SpentBy = req.SpentBy.Trim(),
            SpentAt = req.SpentAt ?? DateTime.UtcNow,
            PaymentMode = paymentMode,
            RecordedByUserId = recordedBy?.Id ?? 0,
            RecordedByName = recordedBy?.Name ?? "",
        };
        db.CafeExpenses.Add(expense);
        await db.SaveChangesAsync();
        return CafeExpenseDto.From(expense);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var expense = await db.CafeExpenses.FindAsync(id);
        if (expense is null) return NotFound();
        db.CafeExpenses.Remove(expense);
        await db.SaveChangesAsync();
        return NoContent();
    }

    // ---------- Daily purchase list ----------
    // The cafe's paper "daily purchase list" on screen: a fixed, tenant-owned set of rows
    // (Mutton, Gas, Das Kaka, Cook Salary, ...) that staff fill amounts into each day. Only
    // the filled rows become CafeExpense — a list of 31 with 10 used writes 10 rows, not 31.

    /// <summary>The day's sheet: every active list row, in sheet order, carrying whatever was
    /// already saved for that date (0 when nothing was). Blank rows are returned on purpose —
    /// the sheet is the entry form, so staff have to see a row to fill it. A row split across
    /// more than one payment mode in the same day (rare, but SaveDailySheet doesn't forbid it)
    /// reports back whichever mode covers the largest share of its total, rather than one
    /// arbitrarily "winning" — good enough for the entry popup to pre-select something sane.</summary>
    [HttpGet("daily")]
    public async Task<DailyPurchaseSheetDto> DailySheet([FromQuery] DateOnly? date = null)
    {
        var day = date ?? DateOnly.FromDateTime(IstClock.NowIst);

        var items = await db.PurchaseListItems
            .Where(i => i.IsActive)
            .OrderBy(i => i.SortOrder).ThenBy(i => i.Id)
            .ToListAsync();

        var start = IstClock.IstDateStartUtc(day);
        var end = IstClock.IstDateStartUtc(day.AddDays(1));
        var expensesByItem = await db.CafeExpenses
            .Where(e => e.PurchaseListItemId != null && e.SpentAt >= start && e.SpentAt < end)
            .ToListAsync();
        var byItem = expensesByItem.ToLookup(e => e.PurchaseListItemId!.Value);

        var lines = items
            .Select(i =>
            {
                var rows = byItem[i.Id].ToList();
                var amount = rows.Sum(e => e.Amount);
                var mode = rows
                    .GroupBy(e => e.PaymentMode ?? "Cash")
                    .OrderByDescending(g => g.Sum(e => e.Amount))
                    .FirstOrDefault()?.Key;
                return new DailyPurchaseLineDto(i.Id, i.Name, i.DefaultCategory, amount, rows.Count > 0 ? mode : null);
            })
            .ToList();

        return new DailyPurchaseSheetDto(day, lines, lines.Sum(l => l.Amount));
    }

    /// <summary>Saves a day's sheet. Rows at 0 are skipped, and the day's existing
    /// list-sourced rows are replaced rather than added to — re-saving is how staff correct
    /// an amount they mistyped, so an append would silently double the day's spend.</summary>
    [HttpPost("daily")]
    public async Task<ActionResult<DailyPurchaseSheetDto>> SaveDailySheet(SaveDailyPurchaseRequest req)
    {
        var day = req.Date ?? DateOnly.FromDateTime(IstClock.NowIst);
        var filled = (req.Lines ?? []).Where(l => l.Amount > 0).ToList();

        var items = await db.PurchaseListItems.Where(i => i.IsActive).ToListAsync();
        var byId = items.ToDictionary(i => i.Id);
        if (filled.Any(l => !byId.ContainsKey(l.ItemId)))
            throw new ApiValidationException("Some rows are no longer on the purchase list — reload the sheet and try again.");

        // Validated up front rather than per row inside the write loop below, so a typo on the
        // last row doesn't leave the earlier ones already added to the change tracker.
        foreach (var line in filled) NormalizePaymentMode(line.PaymentMode);

        // This endpoint writes CafeExpense directly, so it would otherwise walk straight past
        // the Owner sign-off that Create() enforces. Rather than silently booking a large line
        // (or minting approvals that a re-save would duplicate), the save is refused and the
        // offending rows are named — the Manager puts those through Add Expense, where they
        // become ApprovalRequests properly, and saves the rest here.
        if (!User.IsInRole(nameof(AppRole.Owner)))
        {
            var overLimit = filled
                .Where(l => l.Amount > ApprovalThresholds.ExpenseAmount)
                .Select(l => byId[l.ItemId].Name)
                .ToList();
            if (overLimit.Count > 0)
                throw new ApiValidationException(
                    $"{string.Join(", ", overLimit)} — above the {ApprovalThresholds.ExpenseAmount:C} auto-approve limit. Add those through Add Expense so an Owner can approve them; the other rows will save here.");
        }

        var idClaim = User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value
            ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        var recordedBy = await db.Users.FindAsync(int.Parse(idClaim!));
        var spentBy = string.IsNullOrWhiteSpace(req.SpentBy) ? (recordedBy?.Name ?? "Counter") : req.SpentBy.Trim();

        var start = IstClock.IstDateStartUtc(day);
        var end = IstClock.IstDateStartUtc(day.AddDays(1));
        var existing = await db.CafeExpenses
            .Where(e => e.PurchaseListItemId != null && e.SpentAt >= start && e.SpentAt < end)
            .ToListAsync();
        db.CafeExpenses.RemoveRange(existing);

        // Midday IST, not "now": the row then sits unambiguously inside the day it belongs to
        // even for a backdated sheet, and re-saving doesn't shuffle timestamps around.
        var spentAt = start.AddHours(12);
        foreach (var line in filled)
        {
            var item = byId[line.ItemId];
            db.CafeExpenses.Add(new CafeExpense
            {
                Amount = line.Amount,
                Category = item.DefaultCategory,
                Purpose = item.Name,
                SpentBy = spentBy,
                SpentAt = spentAt,
                PurchaseListItemId = item.Id,
                // Cash, not null, when the client sent nothing: a filled sheet row was paid
                // somehow, so leaving it unset would strand real spend in the "Not set" bucket.
                PaymentMode = NormalizePaymentMode(line.PaymentMode) ?? "Cash",
                RecordedByUserId = recordedBy?.Id ?? 0,
                RecordedByName = recordedBy?.Name ?? "",
            });
        }

        await db.SaveChangesAsync();
        return await DailySheet(day);
    }

    /// <summary>Adds a row to the tenant's list. Re-adding a name that was retired revives that
    /// row instead of creating a second one with the same name, which is what would otherwise
    /// happen every time someone re-added a row they'd removed.</summary>
    [HttpPost("daily/items")]
    public async Task<ActionResult<PurchaseListItemDto>> AddListItem(CreatePurchaseListItemRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            throw new ApiValidationException("Enter a name for the purchase list row.");
        var name = req.Name.Trim();

        var existing = await db.PurchaseListItems.FirstOrDefaultAsync(i => i.Name.ToLower() == name.ToLower());
        if (existing is not null)
        {
            if (existing.IsActive)
                throw new ApiValidationException($"\"{existing.Name}\" is already on the list.");
            existing.IsActive = true;
            if (req.DefaultCategory is ExpenseCategory revived) existing.DefaultCategory = revived;
            await db.SaveChangesAsync();
            return PurchaseListItemDto.From(existing);
        }

        var maxOrder = await db.PurchaseListItems.MaxAsync(i => (int?)i.SortOrder) ?? 0;
        var item = new PurchaseListItem
        {
            Name = name,
            SortOrder = maxOrder + 1,
            DefaultCategory = req.DefaultCategory ?? ExpenseCategory.Supplies,
        };
        db.PurchaseListItems.Add(item);
        await db.SaveChangesAsync();
        return PurchaseListItemDto.From(item);
    }

    /// <summary>Retires a row. Deactivates rather than deletes — past CafeExpense rows point
    /// back at this id, and already-saved days must keep resolving to a name.</summary>
    [HttpDelete("daily/items/{id:int}")]
    public async Task<IActionResult> RemoveListItem(int id)
    {
        var item = await db.PurchaseListItems.FindAsync(id);
        if (item is null) return NotFound();
        item.IsActive = false;
        await db.SaveChangesAsync();
        return NoContent();
    }
}
