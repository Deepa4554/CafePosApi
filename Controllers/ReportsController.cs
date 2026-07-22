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
