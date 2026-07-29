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
public class ExpensesController(CafePosDbContext db) : ControllerBase
{
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

        return new CafeExpenseReportDto(rows.Sum(e => e.Amount), byCategory, rows.Select(CafeExpenseDto.From).ToList());
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
}
