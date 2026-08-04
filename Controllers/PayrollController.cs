using CafePOS.Api.Contracts;
using CafePOS.Api.Data;
using CafePOS.Api.Domain;
using CafePOS.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CafePOS.Api.Controllers;

/// <summary>
/// One PayrollRun per tenant per period, containing one PayrollLine per staff member —
/// a single Owner action locks/pays the whole period as one unit. Every number on a
/// PayrollLine is computed once at generation time from real Attendance/Leave/Loan
/// data and snapshotted — see PayrollLine's doc comment for why nothing here or
/// elsewhere ever needs to check "is this period locked" before writing.
/// </summary>
[ApiController]
[Route("api/payroll-runs")]
// HRReports too: the HR Reports screen charts payroll runs alongside leave, so a login
// granted HR Reports but not Payroll still needs to read this.
[RequireScreen("Payroll", "HRReports")]
public class PayrollController(CafePosDbContext db, IAuditService audit) : ControllerBase
{
    [Authorize(Policy = Policies.RequirePlus)]
    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpGet]
    public async Task<IEnumerable<PayrollRunDto>> List([FromQuery] int? year, [FromQuery] int? month)
    {
        var query = db.PayrollRuns.Include(r => r.Lines).AsQueryable();
        if (year is int y) query = query.Where(r => r.PeriodStart.Year == y);
        if (month is int m) query = query.Where(r => r.PeriodStart.Month == m);
        var runs = await query.OrderByDescending(r => r.PeriodStart).ToListAsync();
        return runs.Select(r => PayrollRunDto.From(r, includeLines: false));
    }

    [Authorize(Policy = Policies.RequirePlus)]
    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpGet("{id:int}")]
    public async Task<ActionResult<PayrollRunDto>> Get(int id)
    {
        var run = await db.PayrollRuns.Include(r => r.Lines).FirstOrDefaultAsync(r => r.Id == id);
        if (run is null) return NotFound();
        return PayrollRunDto.From(run, includeLines: true);
    }

    [Authorize(Policy = Policies.RequirePlus)]
    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpPost("generate")]
    public async Task<ActionResult<PayrollRunDto>> Generate(GeneratePayrollRunRequest req)
    {
        if (req.PeriodEnd < req.PeriodStart) throw new ApiValidationException("Period end must be on or after period start.");
        if (await db.PayrollRuns.AnyAsync(r => r.PeriodStart == req.PeriodStart && r.PeriodEnd == req.PeriodEnd))
            throw new ApiConflictException("A payroll run already exists for this exact period.");

        var periodEndExclusive = req.PeriodEnd.ToDateTime(TimeOnly.MinValue).AddDays(1);
        var staffList = await db.Staff.Where(s => s.Status != StaffStatus.Terminated && s.JoinedAt < periodEndExclusive).ToListAsync();
        var settings = await db.Settings.FirstOrDefaultAsync() ?? new CafeSettings();

        var actor = await CurrentUserAsync();
        var run = new PayrollRun
        {
            PeriodStart = req.PeriodStart,
            PeriodEnd = req.PeriodEnd,
            GeneratedByUserId = actor.Id,
            GeneratedByName = actor.Name,
        };

        foreach (var staff in staffList)
            run.Lines.Add(await BuildLineAsync(staff, req.PeriodStart, req.PeriodEnd, settings));

        db.PayrollRuns.Add(run);
        await db.SaveChangesAsync();

        await audit.LogAsync(AuditAction.Create, AuditResource.Payroll, run.Id.ToString(),
            $"{actor.Name} generated payroll for {req.PeriodStart:dd MMM} - {req.PeriodEnd:dd MMM yyyy} ({run.Lines.Count} staff).", AuditSeverity.Medium, actor.Id, actor.Name);
        return CreatedAtAction(nameof(Get), new { id = run.Id }, PayrollRunDto.From(run, true));
    }

    [Authorize(Policy = Policies.RequirePlus)]
    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpPatch("{id:int}/lines/{lineId:int}")]
    public async Task<ActionResult<PayrollLineDto>> UpdateLine(int id, int lineId, UpdatePayrollLineRequest req)
    {
        var run = await db.PayrollRuns.FindAsync(id);
        if (run is null) return NotFound();
        if (run.Status != PayrollRunStatus.Draft) throw new ApiConflictException("This payroll run is locked — reopen it before editing.");
        var line = await db.PayrollLines.FirstOrDefaultAsync(l => l.Id == lineId && l.PayrollRunId == id);
        if (line is null) return NotFound();

        if (req.Allowances is not null) line.Allowances = req.Allowances.Select(a => new AllowanceLine(a.Name, a.Amount)).ToList();
        if (req.LateDeduction is decimal ld) line.LateDeduction = Math.Max(0, ld);
        if (req.PfDeduction is decimal pf) line.PfDeduction = Math.Max(0, pf);
        if (req.EsicDeduction is decimal es) line.EsicDeduction = Math.Max(0, es);
        if (req.ProfessionalTaxDeduction is decimal pt) line.ProfessionalTaxDeduction = Math.Max(0, pt);
        line.IsEdited = true;
        var actor = await CurrentUserAsync();
        line.EditedByUserId = actor.Id;

        FinalizeLine(line);
        await db.SaveChangesAsync();
        return PayrollLineDto.From(line);
    }

    [Authorize(Policy = Policies.RequirePlus)]
    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var run = await db.PayrollRuns.FindAsync(id);
        if (run is null) return NotFound();
        if (run.Status != PayrollRunStatus.Draft) throw new ApiConflictException("Only a Draft payroll run can be deleted.");
        db.PayrollRuns.Remove(run);
        await db.SaveChangesAsync();
        return NoContent();
    }

    [Authorize(Policy = Policies.RequirePlus)]
    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpPost("{id:int}/lock")]
    public async Task<ActionResult<PayrollRunDto>> Lock(int id)
    {
        var run = await db.PayrollRuns.Include(r => r.Lines).FirstOrDefaultAsync(r => r.Id == id);
        if (run is null) return NotFound();
        if (run.Status != PayrollRunStatus.Draft) throw new ApiConflictException("This payroll run is already locked.");

        var actor = await CurrentUserAsync();
        run.Status = PayrollRunStatus.Locked;
        run.LockedAt = DateTime.UtcNow;
        run.LockedByUserId = actor.Id;
        await db.SaveChangesAsync();

        await audit.LogAsync(AuditAction.Update, AuditResource.Payroll, run.Id.ToString(),
            $"{actor.Name} locked payroll for {run.PeriodStart:MMM yyyy}.", AuditSeverity.High, actor.Id, actor.Name);
        return PayrollRunDto.From(run, true);
    }

    /// <summary>Locked -> Draft only, never from Paid (money already moved by then) —
    /// Owner-only since reopening finalized payroll is a bigger decision than the
    /// Manager-level actions elsewhere in this controller.</summary>
    [Authorize(Policy = Policies.RequirePlus)]
    [Authorize(Policy = Policies.OwnerOnly)]
    [HttpPost("{id:int}/reopen")]
    public async Task<ActionResult<PayrollRunDto>> Reopen(int id)
    {
        var run = await db.PayrollRuns.Include(r => r.Lines).FirstOrDefaultAsync(r => r.Id == id);
        if (run is null) return NotFound();
        if (run.Status != PayrollRunStatus.Locked) throw new ApiConflictException("Only a Locked payroll run can be reopened.");

        var actor = await CurrentUserAsync();
        run.Status = PayrollRunStatus.Draft;
        run.LockedAt = null;
        run.LockedByUserId = null;
        await db.SaveChangesAsync();

        await audit.LogAsync(AuditAction.Update, AuditResource.Payroll, run.Id.ToString(),
            $"{actor.Name} reopened payroll for {run.PeriodStart:MMM yyyy}.", AuditSeverity.High, actor.Id, actor.Name);
        return PayrollRunDto.From(run, true);
    }

    /// <summary>The only place a StaffLoan's OutstandingBalance ever moves for a
    /// payroll-driven deduction — see StaffLoan's doc comment for why this isn't done
    /// at Draft generation time.</summary>
    [Authorize(Policy = Policies.RequirePlus)]
    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpPost("{id:int}/mark-paid")]
    public async Task<ActionResult<PayrollRunDto>> MarkPaid(int id)
    {
        var run = await db.PayrollRuns.Include(r => r.Lines).FirstOrDefaultAsync(r => r.Id == id);
        if (run is null) return NotFound();
        if (run.Status != PayrollRunStatus.Locked) throw new ApiConflictException("Only a Locked payroll run can be marked paid.");

        foreach (var line in run.Lines.Where(l => l.LoanDeduction > 0))
            await ApplyLoanDeductionsAsync(line);

        var actor = await CurrentUserAsync();
        run.Status = PayrollRunStatus.Paid;
        run.PaidAt = DateTime.UtcNow;
        run.PaidByUserId = actor.Id;
        await db.SaveChangesAsync();

        await audit.LogAsync(AuditAction.Update, AuditResource.Payroll, run.Id.ToString(),
            $"{actor.Name} marked payroll for {run.PeriodStart:MMM yyyy} as paid ({run.Lines.Count} staff, total {run.Lines.Sum(l => l.NetSalary):0.00}).",
            AuditSeverity.High, actor.Id, actor.Name);
        return PayrollRunDto.From(run, true);
    }

    [Authorize(Policy = Policies.RequirePlus)]
    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpGet("{id:int}/lines/{lineId:int}/payslip.pdf")]
    public async Task<IActionResult> PayslipPdf(int id, int lineId)
    {
        var run = await db.PayrollRuns.FindAsync(id);
        if (run is null) return NotFound();
        var line = await db.PayrollLines.FirstOrDefaultAsync(l => l.Id == lineId && l.PayrollRunId == id);
        if (line is null) return NotFound();

        var settings = await db.Settings.FirstOrDefaultAsync() ?? new CafeSettings();
        var bytes = PayslipPdfBuilder.Build(settings, run, line);
        return File(bytes, "application/pdf", $"payslip-{line.StaffName}-{run.PeriodStart:yyyyMM}.pdf");
    }

    [Authorize(Policy = Policies.RequirePlus)]
    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpGet("{id:int}/bank-export.csv")]
    public async Task<IActionResult> BankExport(int id)
    {
        var run = await db.PayrollRuns.Include(r => r.Lines).FirstOrDefaultAsync(r => r.Id == id);
        if (run is null) return NotFound();

        var payable = run.Lines.Where(l => l.NetSalary > 0 && !string.IsNullOrWhiteSpace(l.BankAccountNumberSnapshot)).OrderBy(l => l.StaffName).ToList();
        var headers = new[] { "Staff Name", "Account Number", "IFSC", "Amount" };
        var rows = payable.Select(l => (IEnumerable<object?>)[l.StaffName, l.BankAccountNumberSnapshot, l.BankIfscSnapshot, l.NetSalary.ToString("0.00")]);
        var bytes = CsvBuilder.Build(headers, rows);
        return File(bytes, "text/csv", $"bank-export-{run.PeriodStart:yyyyMM}.csv");
    }

    // ---------- Self-service ----------

    [Authorize(Policy = Policies.RequirePlus)]
    [HttpGet("/api/payroll/me/payslips")]
    public async Task<IEnumerable<PayrollLineDto>> MyPayslips([FromQuery] int? year)
    {
        var staff = await CurrentStaffAsync();
        var query = db.PayrollLines.Where(l => l.StaffId == staff.Id)
            .Join(db.PayrollRuns.Where(r => r.Status == PayrollRunStatus.Paid), l => l.PayrollRunId, r => r.Id, (l, r) => new { Line = l, Run = r });
        if (year is int y) query = query.Where(x => x.Run.PeriodStart.Year == y);
        var results = await query.OrderByDescending(x => x.Run.PeriodStart).ToListAsync();
        return results.Select(x => PayrollLineDto.From(x.Line));
    }

    [Authorize(Policy = Policies.RequirePlus)]
    [HttpGet("/api/payroll/me/payslips/{lineId:int}/pdf")]
    public async Task<IActionResult> MyPayslipPdf(int lineId)
    {
        var staff = await CurrentStaffAsync();
        var line = await db.PayrollLines.FirstOrDefaultAsync(l => l.Id == lineId && l.StaffId == staff.Id);
        if (line is null) return NotFound();
        var run = await db.PayrollRuns.FindAsync(line.PayrollRunId);
        if (run is null || run.Status != PayrollRunStatus.Paid) return NotFound();

        var settings = await db.Settings.FirstOrDefaultAsync() ?? new CafeSettings();
        var bytes = PayslipPdfBuilder.Build(settings, run, line);
        return File(bytes, "application/pdf", $"payslip-{run.PeriodStart:yyyyMM}.pdf");
    }

    // ---------- Generation math ----------

    /// <summary>
    /// Computes every number on a staff member's line for the period, from real
    /// Attendance/Leave/Loan data — see PayrollLine's doc comment for why this is the
    /// only place these numbers are ever computed (never recomputed later against a
    /// generated/locked line). Salary-type-specific base pay: Monthly prorates
    /// BasicSalary by period-length/30; Daily pays PerDayRate x (days worked + paid
    /// leave); Hourly pays HourlyRate x actual worked hours from Attendance.
    /// </summary>
    private async Task<PayrollLine> BuildLineAsync(StaffMember staff, DateOnly periodStart, DateOnly periodEnd, CafeSettings settings)
    {
        var periodDays = periodEnd.DayNumber - periodStart.DayNumber + 1;
        var monthRatio = periodDays / 30m;
        var periodEndExclusive = periodEnd.ToDateTime(TimeOnly.MinValue).AddDays(1);
        var periodStartInclusive = periodStart.ToDateTime(TimeOnly.MinValue);

        var attendance = await db.AttendanceRecords
            .Where(a => a.StaffId == staff.Id && a.Date >= periodStart && a.Date <= periodEnd)
            .ToListAsync();
        var presentDays = attendance.Count(a => a.WorkedMinutes.HasValue);
        var lateDays = attendance.Count(a => a.Status == AttendanceStatus.Late);
        var halfDays = attendance.Count(a => a.Status == AttendanceStatus.HalfDay);
        var overtimeHours = attendance.Sum(a => a.OvertimeMinutes) / 60.0;
        var totalWorkedHours = attendance.Sum(a => a.WorkedMinutes ?? 0) / 60.0;
        var attendedDates = attendance.Where(a => a.WorkedMinutes.HasValue).Select(a => a.Date).ToHashSet();

        var leaves = await db.LeaveRequests
            .Where(l => l.StaffId == staff.Id && l.Status == LeaveRequestStatus.Approved && l.StartDate <= periodEnd && l.EndDate >= periodStart)
            .ToListAsync();
        int paidLeaveDays = 0, unpaidLeaveDays = 0;
        var leaveDates = new HashSet<DateOnly>();
        foreach (var leave in leaves)
        {
            var overlapStart = leave.StartDate > periodStart ? leave.StartDate : periodStart;
            var overlapEnd = leave.EndDate < periodEnd ? leave.EndDate : periodEnd;
            if (overlapEnd < overlapStart) continue;
            var days = overlapEnd.DayNumber - overlapStart.DayNumber + 1;
            if (leave.Type == LeaveType.Unpaid) unpaidLeaveDays += days; else paidLeaveDays += days;
            for (var d = overlapStart; d <= overlapEnd; d = d.AddDays(1)) leaveDates.Add(d);
        }

        var scheduledDates = (await db.Shifts
                .Where(s => s.StaffId == staff.Id && s.StartsAt < periodEndExclusive && s.EndsAt >= periodStartInclusive)
                .Select(s => s.StartsAt)
                .ToListAsync())
            .Select(DateOnly.FromDateTime).Distinct().ToList();
        var absentDays = scheduledDates.Count(d => !attendedDates.Contains(d) && !leaveDates.Contains(d));

        decimal perDayRate, basePay, hourlyEquivalent;
        switch (staff.SalaryType)
        {
            case SalaryType.Daily:
                perDayRate = staff.BasicSalary ?? 0;
                basePay = perDayRate * (presentDays + paidLeaveDays);
                hourlyEquivalent = settings.StandardShiftHours > 0 ? perDayRate / settings.StandardShiftHours : 0;
                break;
            case SalaryType.Hourly:
                hourlyEquivalent = staff.HourlyRate ?? 0;
                perDayRate = hourlyEquivalent * settings.StandardShiftHours;
                basePay = (decimal)totalWorkedHours * hourlyEquivalent;
                break;
            default: // Monthly
                var monthlyBasic = staff.BasicSalary ?? 0;
                perDayRate = monthlyBasic / 30m;
                basePay = Math.Round(monthlyBasic * monthRatio, 2);
                hourlyEquivalent = settings.StandardShiftHours > 0 ? perDayRate / settings.StandardShiftHours : 0;
                break;
        }

        var overtimePay = Math.Round((decimal)overtimeHours * hourlyEquivalent * 1.5m, 2);
        var leaveDeduction = Math.Round(unpaidLeaveDays * perDayRate, 2);
        // Approximate: 10% of a day's pay per late mark — a simple default, not a policy engine.
        var lateDeduction = Math.Round(lateDays * perDayRate * 0.1m, 2);
        var grossBeforeAllowances = basePay + overtimePay;

        var pfCeiling = StatutoryDeductionCalculator.MonthlyPfWageCeiling * monthRatio;
        var esicCeiling = StatutoryDeductionCalculator.MonthlyEsicGrossCeiling * monthRatio;
        var ptThreshold = StatutoryDeductionCalculator.MonthlyProfessionalTaxThreshold * monthRatio;
        var ptFlat = StatutoryDeductionCalculator.MonthlyProfessionalTaxFlat * monthRatio;
        var pf = StatutoryDeductionCalculator.Pf(basePay, pfCeiling);
        var esic = StatutoryDeductionCalculator.Esic(grossBeforeAllowances, esicCeiling);
        var pt = StatutoryDeductionCalculator.ProfessionalTax(grossBeforeAllowances, ptThreshold, ptFlat);

        var activeLoans = await db.StaffLoans.Where(l => l.StaffId == staff.Id && l.Status == LoanStatus.Active).ToListAsync();
        var rawLoanDeduction = activeLoans.Sum(l => Math.Min(l.MonthlyDeduction, l.OutstandingBalance));

        var line = new PayrollLine
        {
            StaffId = staff.Id,
            StaffName = staff.Name,
            SalaryType = staff.SalaryType,
            BasicSalary = basePay,
            HourlyRate = staff.HourlyRate,
            PresentDays = presentDays,
            LateDays = lateDays,
            HalfDays = halfDays,
            AbsentDays = absentDays,
            PaidLeaveDays = paidLeaveDays,
            UnpaidLeaveDays = unpaidLeaveDays,
            OvertimeHours = overtimeHours,
            OvertimePay = overtimePay,
            Allowances = [],
            LeaveDeduction = leaveDeduction,
            LateDeduction = lateDeduction,
            LoanDeduction = rawLoanDeduction,
            PfDeduction = pf,
            EsicDeduction = esic,
            ProfessionalTaxDeduction = pt,
            BankAccountNumberSnapshot = staff.BankAccountNumber,
            BankIfscSnapshot = staff.BankIfsc,
        };
        FinalizeLine(line);
        return line;
    }

    /// <summary>Recomputes AllowancesTotal/GrossEarnings/TotalDeductions/NetSalary from
    /// whatever's currently on the line, and re-caps LoanDeduction so NetSalary can
    /// never go negative — shared by generation and the pre-lock line-edit endpoint.</summary>
    private static void FinalizeLine(PayrollLine line)
    {
        line.AllowancesTotal = line.Allowances.Sum(a => a.Amount);
        line.GrossEarnings = line.BasicSalary + line.OvertimePay + line.AllowancesTotal;
        var nonLoanDeductions = line.LeaveDeduction + line.LateDeduction + line.PfDeduction + line.EsicDeduction + line.ProfessionalTaxDeduction;
        var availableForLoan = Math.Max(0, line.GrossEarnings - nonLoanDeductions);
        line.LoanDeduction = Math.Min(line.LoanDeduction, availableForLoan);
        line.TotalDeductions = nonLoanDeductions + line.LoanDeduction;
        line.NetSalary = Math.Max(0, line.GrossEarnings - line.TotalDeductions);
    }

    private async Task ApplyLoanDeductionsAsync(PayrollLine line)
    {
        var remaining = line.LoanDeduction;
        if (remaining <= 0) return;

        var loans = await db.StaffLoans.Where(l => l.StaffId == line.StaffId && l.Status == LoanStatus.Active)
            .OrderBy(l => l.CreatedAt).ToListAsync();
        foreach (var loan in loans)
        {
            if (remaining <= 0) break;
            var take = Math.Min(remaining, loan.OutstandingBalance);
            if (take <= 0) continue;
            loan.OutstandingBalance -= take;
            remaining -= take;
            if (loan.OutstandingBalance <= 0)
            {
                loan.OutstandingBalance = 0;
                loan.Status = LoanStatus.Closed;
                loan.ClosedAt = DateTime.UtcNow;
            }
        }
    }

    private Task<StaffMember> CurrentStaffAsync() => this.GetOrCreateCurrentStaffAsync(db);

    private async Task<AppUser> CurrentUserAsync()
    {
        var idClaim = User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value
            ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        var id = int.Parse(idClaim!);
        return await db.Users.FindAsync(id) ?? throw new KeyNotFoundException("User not found.");
    }
}
