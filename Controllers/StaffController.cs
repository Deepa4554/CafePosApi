using CafePOS.Api.Contracts;
using CafePOS.Api.Data;
using CafePOS.Api.Domain;
using CafePOS.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CafePOS.Api.Controllers;

[ApiController]
[Route("api/staff")]
[Authorize(Policy = Policies.RequirePlus)]
public class StaffController(CafePosDbContext db, ITenantContext tenant, IPasswordHasher<AppUser> hasher) : ControllerBase
{
    [HttpGet]
    public async Task<IEnumerable<StaffDto>> List([FromQuery] int? branchId)
    {
        var query = db.Staff.AsQueryable();
        if (branchId is not null) query = query.Where(s => s.BranchId == branchId);
        var staff = await query.OrderBy(s => s.Name).ToListAsync();
        return staff.Select(StaffDto.From);
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<StaffDto>> Get(int id)
    {
        var staff = await db.Staff.FindAsync(id);
        return staff is null ? NotFound() : StaffDto.From(staff);
    }

    /// <summary>
    /// Creates the HR roster entry and, if Password/LoginRole are supplied, a real app
    /// login for this same staff member — tied to the creator's own tenant (AppUser is
    /// deliberately not auto-tenant-stamped; see AppUser.TenantId doc comment), so the
    /// new Manager/Waiter/etc. lands in the same cafe, never a new one.
    /// </summary>
    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpPost]
    public async Task<ActionResult<StaffDto>> Create(CreateStaffRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name)) throw new ApiValidationException("Name is required.");
        if (string.IsNullOrWhiteSpace(req.Role)) throw new ApiValidationException("Role is required.");

        int? userId = null;
        var wantsLogin = req.Password is not null || req.LoginRole is not null || !string.IsNullOrWhiteSpace(req.Email);
        if (wantsLogin)
        {
            if (string.IsNullOrWhiteSpace(req.Email) || !req.Email.Contains('@'))
                throw new ApiValidationException("A valid email is required to give this staff member app access.");
            if (string.IsNullOrWhiteSpace(req.Password) || req.Password.Length < 6)
                throw new ApiValidationException("Password must be at least 6 characters.");
            if (req.LoginRole is null)
                throw new ApiValidationException("A role is required to give this staff member app access.");
            if (req.LoginRole == AppRole.Owner)
                throw new ApiValidationException("Owner accounts are created only via cafe signup, not here.");

            var normalizedEmail = req.Email.Trim().ToLowerInvariant();
            if (await db.Users.AnyAsync(u => u.Email == normalizedEmail))
                throw new ApiConflictException("An account with this email already exists.");

            var user = new AppUser
            {
                TenantId = tenant.TenantIdOrDefault,
                Email = normalizedEmail,
                Name = req.Name.Trim(),
                Role = req.LoginRole.Value,
                PasswordHash = "",
            };
            user.PasswordHash = hasher.HashPassword(user, req.Password);
            db.Users.Add(user);
            await db.SaveChangesAsync(); // assigns user.Id before StaffMember.UserId references it
            userId = user.Id;
        }

        var staff = new StaffMember
        {
            UserId = userId,
            Name = req.Name.Trim(),
            Role = req.Role.Trim(),
            Email = req.Email,
            Phone = req.Phone,
            HourlyRate = req.HourlyRate,
            BranchId = req.BranchId,
        };
        db.Staff.Add(staff);
        await db.SaveChangesAsync();
        return CreatedAtAction(nameof(Get), new { id = staff.Id }, StaffDto.From(staff));
    }

    /// <summary>Powers the Staff Profile edit flow — name/role/contact/branch/photo, all
    /// optional (partial update, matching MenuController.Update's pattern).</summary>
    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpPatch("{id:int}")]
    public async Task<ActionResult<StaffDto>> Update(int id, UpdateStaffRequest req)
    {
        var staff = await db.Staff.FindAsync(id);
        if (staff is null) return NotFound();

        if (req.Name is not null)
        {
            if (string.IsNullOrWhiteSpace(req.Name)) throw new ApiValidationException("Name cannot be blank.");
            staff.Name = req.Name.Trim();
        }
        if (req.Role is not null)
        {
            if (string.IsNullOrWhiteSpace(req.Role)) throw new ApiValidationException("Role cannot be blank.");
            staff.Role = req.Role.Trim();
        }
        if (req.Email is not null) staff.Email = req.Email.Trim();
        if (req.Phone is not null) staff.Phone = req.Phone.Trim();
        if (req.HourlyRate is not null) staff.HourlyRate = req.HourlyRate;
        if (req.BranchId is not null)
        {
            if (!await db.Branches.AnyAsync(b => b.Id == req.BranchId)) throw new ApiValidationException("Branch not found.");
            staff.BranchId = req.BranchId;
        }
        if (req.PhotoUrl is not null) staff.PhotoUrl = req.PhotoUrl;

        await db.SaveChangesAsync();
        return StaffDto.From(staff);
    }

    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpPatch("{id:int}/status")]
    public async Task<ActionResult<StaffDto>> UpdateStatus(int id, UpdateStaffStatusRequest req)
    {
        var staff = await db.Staff.FindAsync(id);
        if (staff is null) return NotFound();
        staff.Status = req.Status;
        await db.SaveChangesAsync();
        return StaffDto.From(staff);
    }

    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var staff = await db.Staff.FindAsync(id);
        if (staff is null) return NotFound();
        db.Staff.Remove(staff);
        await db.SaveChangesAsync();
        return NoContent();
    }

    // ---------- Shifts (Team Schedule / Edit Shift screens) ----------

    /// <summary>Every shift across every staff member on a given day — powers the Team
    /// Schedule day view. `{id:int}/shifts` below stays for a single staff member's
    /// history (Staff Profile screen); this one is tenant-wide for a specific date.</summary>
    [HttpGet("shifts")]
    public async Task<IEnumerable<ShiftWithStaffDto>> ListAllShifts([FromQuery] DateTime date)
    {
        var dayStart = date.Date;
        var dayEnd = dayStart.AddDays(1);
        var shifts = await db.Shifts.Where(s => s.StartsAt < dayEnd && s.EndsAt > dayStart).OrderBy(s => s.StartsAt).ToListAsync();

        var staffIds = shifts.Select(s => s.StaffId).Distinct().ToList();
        var staffMap = await db.Staff.Where(s => staffIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id);

        return shifts.Select(s => new ShiftWithStaffDto(
            s.Id, s.StaffId,
            staffMap.GetValueOrDefault(s.StaffId)?.Name ?? "Unknown",
            staffMap.GetValueOrDefault(s.StaffId)?.Role ?? "",
            s.StartsAt, s.EndsAt, s.Notes));
    }

    // Mirrors DashboardController's hour buckets — kept as its own copy since the two
    // controllers have no shared base and this is a 4-line constant, not worth a shared
    // utility for.
    private static readonly (string Label, int StartHour, int EndHour)[] ShiftHourBuckets =
    [
        ("08:00", 6, 10),
        ("12:00", 10, 14),
        ("15:00", 14, 17),
        ("19:00", 17, 21),
    ];

    /// <summary>
    /// Real staffing-vs-demand comparison — no AI/LLM involved. For the requested date
    /// (defaults to today), compares who's actually scheduled per time window against
    /// this cafe's typical order volume for that window (averaged over the last 7 real
    /// days), so it's useful whether you're checking today's coverage or planning a
    /// future day's schedule against realistic demand.
    /// </summary>
    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpGet("shift-optimization")]
    public async Task<ShiftOptimizationDto> ShiftOptimization([FromQuery] DateTime? date)
    {
        var day = (date ?? DateTime.UtcNow).Date;
        var dayEnd = day.AddDays(1);

        var shifts = await db.Shifts.Where(s => s.StartsAt < dayEnd && s.EndsAt > day).ToListAsync();

        var demandStart = day.AddDays(-6);
        var recentOrderTimes = await db.Orders
            .Where(o => o.CreatedAt >= demandStart && o.CreatedAt < dayEnd)
            .Select(o => o.CreatedAt)
            .ToListAsync();
        var sampledDays = Math.Max(1, recentOrderTimes.Select(o => o.Date).Distinct().Count());

        var windows = new List<HourWindowStaffingDto>();
        var ratiosWhereStaffed = new List<double>();

        foreach (var b in ShiftHourBuckets)
        {
            var avgOrderCount = recentOrderTimes.Count(o => o.Hour >= b.StartHour && o.Hour < b.EndHour) / (double)sampledDays;
            var bucketStart = day.AddHours(b.StartHour);
            var bucketEnd = day.AddHours(b.EndHour);
            var staffScheduled = shifts.Count(s => s.StartsAt < bucketEnd && s.EndsAt > bucketStart);
            var ordersPerStaff = staffScheduled > 0 ? avgOrderCount / staffScheduled : avgOrderCount;
            if (staffScheduled > 0) ratiosWhereStaffed.Add(ordersPerStaff);

            windows.Add(new HourWindowStaffingDto(b.Label, (int)Math.Round(avgOrderCount), staffScheduled, Math.Round(ordersPerStaff, 1), "Balanced"));
        }

        var avgRatio = ratiosWhereStaffed.Count > 0 ? ratiosWhereStaffed.Average() : 0;
        var suggestions = new List<string>();
        var finalWindows = windows.Select(w =>
        {
            string status;
            if (w.StaffScheduled == 0 && w.OrderCount > 0)
            {
                status = "Understaffed";
                suggestions.Add($"No one is scheduled around {w.Label} — this cafe typically sees ~{w.OrderCount} order{(w.OrderCount == 1 ? "" : "s")} in that window. Consider adding a shift.");
            }
            else if (avgRatio > 0 && w.OrdersPerStaff > avgRatio * 1.5)
            {
                status = "Understaffed";
                suggestions.Add($"Around {w.Label}, staff typically handle ~{w.OrdersPerStaff:0.0} orders each — well above the day's average. Consider scheduling one more person.");
            }
            else if (avgRatio > 0 && w.StaffScheduled > 1 && w.OrdersPerStaff < avgRatio * 0.5)
            {
                status = "Overstaffed";
                suggestions.Add($"Around {w.Label}, {w.StaffScheduled} staff are scheduled for a comparatively light order load — you may be able to reduce coverage here.");
            }
            else
            {
                status = "Balanced";
            }
            return w with { Status = status };
        }).ToList();

        if (suggestions.Count == 0)
            suggestions.Add("Staffing looks well matched to typical order volume across the day — no changes suggested.");

        return new ShiftOptimizationDto(finalWindows, suggestions);
    }

    [HttpGet("{id:int}/shifts")]
    public async Task<IEnumerable<ShiftDto>> ListShifts(int id)
    {
        var shifts = await db.Shifts.Where(s => s.StaffId == id).OrderBy(s => s.StartsAt).ToListAsync();
        return shifts.Select(ShiftDto.From);
    }

    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpPost("shifts")]
    public async Task<ActionResult<ShiftDto>> CreateShift(CreateShiftRequest req)
    {
        if (req.EndsAt <= req.StartsAt) throw new ApiValidationException("Shift end must be after start.");
        if (!await db.Staff.AnyAsync(s => s.Id == req.StaffId)) throw new ApiValidationException("Staff member not found.");

        var shift = new Shift { StaffId = req.StaffId, StartsAt = req.StartsAt, EndsAt = req.EndsAt, Notes = req.Notes };
        db.Shifts.Add(shift);
        await db.SaveChangesAsync();
        return ShiftDto.From(shift);
    }

    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpDelete("shifts/{shiftId:int}")]
    public async Task<IActionResult> DeleteShift(int shiftId)
    {
        var shift = await db.Shifts.FindAsync(shiftId);
        if (shift is null) return NotFound();
        db.Shifts.Remove(shift);
        await db.SaveChangesAsync();
        return NoContent();
    }

    // ---------- Performance & Payroll ----------

    /// <summary>Every staff member's reviews rolled up and ranked by rating — powers
    /// the Performance Reports leaderboard. Staff with no reviews yet are simply
    /// absent rather than shown with fabricated zeros.</summary>
    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpGet("performance")]
    public async Task<IEnumerable<StaffPerformanceSummaryDto>> ListAllPerformance()
    {
        var reviews = await db.PerformanceReviews.ToListAsync();
        var staffIds = reviews.Select(r => r.StaffId).Distinct().ToList();
        var staffMap = await db.Staff.Where(s => staffIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id);

        return reviews
            .GroupBy(r => r.StaffId)
            .Select(g => new StaffPerformanceSummaryDto(
                g.Key,
                staffMap.GetValueOrDefault(g.Key)?.Name ?? "Unknown",
                staffMap.GetValueOrDefault(g.Key)?.Role ?? "",
                g.Sum(r => r.OrdersHandled),
                g.Sum(r => r.RevenueGenerated),
                g.Average(r => r.AttendanceRatePct),
                g.Average(r => r.RatingOutOf5)))
            .OrderByDescending(x => x.AvgRating)
            .ToList();
    }

    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpGet("{id:int}/performance")]
    public async Task<IEnumerable<PerformanceReviewDto>> Performance(int id)
    {
        var reviews = await db.PerformanceReviews.Where(p => p.StaffId == id).OrderByDescending(p => p.PeriodEnd).ToListAsync();
        return reviews.Select(PerformanceReviewDto.From);
    }

    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpGet("payroll")]
    public async Task<IEnumerable<PayrollLineDto>> Payroll([FromQuery] DateTime periodStart, [FromQuery] DateTime periodEnd)
    {
        var staff = await db.Staff.Where(s => s.HourlyRate != null).ToListAsync();
        var shifts = await db.Shifts.Where(s => s.StartsAt >= periodStart && s.EndsAt <= periodEnd).ToListAsync();

        return staff.Select(s =>
        {
            var hours = shifts.Where(sh => sh.StaffId == s.Id).Sum(sh => (sh.EndsAt - sh.StartsAt).TotalHours);
            return new PayrollLineDto(s.Id, s.Name, s.HourlyRate!.Value, hours, (decimal)hours * s.HourlyRate.Value);
        });
    }

    // ---------- Leave requests ----------

    /// <summary>All leave requests, newest first — optionally filtered to one status
    /// (Pending/Approved/Rejected) for the "tabs" the Leave screen shows.</summary>
    [HttpGet("leave-requests")]
    public async Task<IEnumerable<LeaveRequestDto>> ListLeaveRequests([FromQuery] LeaveRequestStatus? status)
    {
        var query = db.LeaveRequests.AsQueryable();
        if (status is not null) query = query.Where(l => l.Status == status);
        var requests = await query.OrderByDescending(l => l.CreatedAt).ToListAsync();
        return requests.Select(LeaveRequestDto.From);
    }

    /// <summary>Any staff-portal user can request leave for themselves (or, if they're
    /// an Owner/Manager, on behalf of someone else) — approval is the gated step, not
    /// the request itself.</summary>
    [HttpPost("leave-requests")]
    public async Task<ActionResult<LeaveRequestDto>> CreateLeaveRequest(CreateLeaveRequest req)
    {
        var staff = await db.Staff.FindAsync(req.StaffId);
        if (staff is null) throw new ApiValidationException("Staff member not found.");
        if (req.EndDate < req.StartDate) throw new ApiValidationException("End date must be on or after the start date.");

        var leave = new LeaveRequest
        {
            StaffId = staff.Id,
            StaffName = staff.Name,
            StartDate = req.StartDate,
            EndDate = req.EndDate,
            Type = req.Type,
            Reason = req.Reason?.Trim(),
        };
        db.LeaveRequests.Add(leave);
        await db.SaveChangesAsync();
        return CreatedAtAction(nameof(ListLeaveRequests), LeaveRequestDto.From(leave));
    }

    /// <summary>Approving immediately puts the staff member on leave (see LeaveRequest's
    /// doc comment for why this is manual rather than date-triggered).</summary>
    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpPost("leave-requests/{id:int}/approve")]
    public async Task<ActionResult<LeaveRequestDto>> ApproveLeaveRequest(int id)
    {
        var leave = await db.LeaveRequests.FindAsync(id);
        if (leave is null) return NotFound();
        if (leave.Status != LeaveRequestStatus.Pending) throw new ApiConflictException("This request has already been reviewed.");

        var reviewer = await CurrentUserAsync();
        leave.Status = LeaveRequestStatus.Approved;
        leave.ReviewedByUserId = reviewer.Id;
        leave.ReviewedByName = reviewer.Name;
        leave.ReviewedAt = DateTime.UtcNow;

        var staff = await db.Staff.FindAsync(leave.StaffId);
        if (staff is not null) staff.Status = StaffStatus.OnLeave;

        await db.SaveChangesAsync();
        return LeaveRequestDto.From(leave);
    }

    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpPost("leave-requests/{id:int}/reject")]
    public async Task<ActionResult<LeaveRequestDto>> RejectLeaveRequest(int id, ReviewLeaveRequest req)
    {
        var leave = await db.LeaveRequests.FindAsync(id);
        if (leave is null) return NotFound();
        if (leave.Status != LeaveRequestStatus.Pending) throw new ApiConflictException("This request has already been reviewed.");

        var reviewer = await CurrentUserAsync();
        leave.Status = LeaveRequestStatus.Rejected;
        leave.ReviewedByUserId = reviewer.Id;
        leave.ReviewedByName = reviewer.Name;
        leave.ReviewedAt = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(req.Note)) leave.Reason = $"{leave.Reason} (Rejected: {req.Note.Trim()})".Trim();

        await db.SaveChangesAsync();
        return LeaveRequestDto.From(leave);
    }

    /// <summary>Ends an approved leave early / marks the staff member back at work —
    /// the counterpart to Approve's OnLeave flip.</summary>
    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpPost("leave-requests/{id:int}/return-to-work")]
    public async Task<ActionResult<LeaveRequestDto>> ReturnToWork(int id)
    {
        var leave = await db.LeaveRequests.FindAsync(id);
        if (leave is null) return NotFound();

        var staff = await db.Staff.FindAsync(leave.StaffId);
        if (staff is not null && staff.Status == StaffStatus.OnLeave) staff.Status = StaffStatus.Active;

        await db.SaveChangesAsync();
        return LeaveRequestDto.From(leave);
    }

    private async Task<AppUser> CurrentUserAsync()
    {
        var idClaim = User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value
            ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        var id = int.Parse(idClaim!);
        return await db.Users.FindAsync(id) ?? throw new KeyNotFoundException("User not found.");
    }
}
