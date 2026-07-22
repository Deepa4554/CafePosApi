using CafePOS.Api.Contracts;
using CafePOS.Api.Data;
using CafePOS.Api.Domain;
using CafePOS.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CafePOS.Api.Controllers;

/// <summary>
/// Advance/Loan requests and balance tracking. OutstandingBalance is deliberately
/// never editable here — it only moves via PayrollController.MarkPaid (each period's
/// EMI deduction) or Close (manual settlement outside payroll) — see StaffLoan's doc
/// comment for why.
/// </summary>
[ApiController]
[Route("api/staff-loans")]
public class StaffLoansController(CafePosDbContext db, IAuditService audit) : ControllerBase
{
    [Authorize(Policy = Policies.RequirePlus)]
    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpGet]
    public async Task<IEnumerable<StaffLoanDto>> List([FromQuery] int? staffId, [FromQuery] LoanStatus? status)
    {
        var query = db.StaffLoans.AsQueryable();
        if (staffId is int sid) query = query.Where(l => l.StaffId == sid);
        if (status is LoanStatus s) query = query.Where(l => l.Status == s);
        var loans = await query.OrderByDescending(l => l.CreatedAt).ToListAsync();
        return loans.Select(StaffLoanDto.From);
    }

    [Authorize(Policy = Policies.RequirePlus)]
    [HttpGet("me")]
    public async Task<IEnumerable<StaffLoanDto>> GetMine()
    {
        var staff = await CurrentStaffAsync();
        var loans = await db.StaffLoans.Where(l => l.StaffId == staff.Id).OrderByDescending(l => l.CreatedAt).ToListAsync();
        return loans.Select(StaffLoanDto.From);
    }

    [Authorize(Policy = Policies.RequirePlus)]
    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpPost]
    public async Task<ActionResult<StaffLoanDto>> Create(CreateStaffLoanRequest req)
    {
        var staff = await db.Staff.FindAsync(req.StaffId);
        if (staff is null) throw new ApiValidationException("Staff member not found.");
        if (req.PrincipalAmount <= 0) throw new ApiValidationException("Principal amount must be greater than zero.");
        if (req.MonthlyDeduction <= 0) throw new ApiValidationException("Monthly deduction must be greater than zero.");

        var actor = await CurrentUserAsync();
        var loan = new StaffLoan
        {
            StaffId = staff.Id,
            StaffName = staff.Name,
            Type = req.Type,
            PrincipalAmount = req.PrincipalAmount,
            MonthlyDeduction = req.MonthlyDeduction,
            StartDate = req.StartDate ?? DateOnly.FromDateTime(DateTime.UtcNow),
            OutstandingBalance = req.PrincipalAmount,
            Reason = req.Reason,
            ApprovedByUserId = actor.Id,
            ApprovedByName = actor.Name,
        };
        db.StaffLoans.Add(loan);
        await db.SaveChangesAsync();

        await audit.LogAsync(AuditAction.Create, AuditResource.Loan, loan.Id.ToString(),
            $"{actor.Name} recorded a {req.Type.ToString().ToLowerInvariant()} of {req.PrincipalAmount:0.00} for {staff.Name}.", AuditSeverity.Medium, actor.Id, actor.Name);
        return CreatedAtAction(nameof(List), StaffLoanDto.From(loan));
    }

    [Authorize(Policy = Policies.RequirePlus)]
    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpPatch("{id:int}")]
    public async Task<ActionResult<StaffLoanDto>> Update(int id, UpdateStaffLoanRequest req)
    {
        var loan = await db.StaffLoans.FindAsync(id);
        if (loan is null) return NotFound();
        if (loan.Status != LoanStatus.Active) throw new ApiConflictException("This loan is already closed.");

        if (req.MonthlyDeduction is decimal md)
        {
            if (md <= 0) throw new ApiValidationException("Monthly deduction must be greater than zero.");
            loan.MonthlyDeduction = md;
        }
        if (req.Reason is not null) loan.Reason = req.Reason;

        await db.SaveChangesAsync();
        return StaffLoanDto.From(loan);
    }

    /// <summary>Manual settlement outside payroll (e.g. repaid in cash) — the other way
    /// a loan's balance can reach zero, besides PayrollController.MarkPaid.</summary>
    [Authorize(Policy = Policies.RequirePlus)]
    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpPost("{id:int}/close")]
    public async Task<ActionResult<StaffLoanDto>> Close(int id)
    {
        var loan = await db.StaffLoans.FindAsync(id);
        if (loan is null) return NotFound();
        if (loan.Status != LoanStatus.Active) throw new ApiConflictException("This loan is already closed.");

        loan.Status = LoanStatus.Closed;
        loan.OutstandingBalance = 0;
        loan.ClosedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        var actor = await CurrentUserAsync();
        await audit.LogAsync(AuditAction.Update, AuditResource.Loan, loan.Id.ToString(),
            $"{actor.Name} closed {loan.StaffName}'s {loan.Type.ToString().ToLowerInvariant()}.", AuditSeverity.Medium, actor.Id, actor.Name);
        return StaffLoanDto.From(loan);
    }

    private async Task<StaffMember> CurrentStaffAsync()
    {
        var idClaim = User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value
            ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        var userId = idClaim is not null && int.TryParse(idClaim, out var id) ? id : (int?)null;
        var staff = userId is not null ? await db.Staff.FirstOrDefaultAsync(s => s.UserId == userId) : null;
        return staff ?? throw new ApiValidationException("This login has no linked staff roster entry.");
    }

    private async Task<AppUser> CurrentUserAsync()
    {
        var idClaim = User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value
            ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        var id = int.Parse(idClaim!);
        return await db.Users.FindAsync(id) ?? throw new KeyNotFoundException("User not found.");
    }
}
