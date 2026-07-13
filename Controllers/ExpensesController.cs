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
