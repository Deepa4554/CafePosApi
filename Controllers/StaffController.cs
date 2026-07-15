using CafePOS.Api.Contracts;
using CafePOS.Api.Data;
using CafePOS.Api.Domain;
using CafePOS.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CafePOS.Api.Controllers;

/// <summary>
/// Roster, app-login, and password-reset endpoints in here are deliberately NOT
/// Plus-gated — giving a Waiter/Cashier a login and being able to reset it is basic
/// day-1 operating necessity, not an upsell. Only the HR/analytics tooling further
/// down (shifts, shift-optimization, leave requests, performance, payroll) carries
/// its own explicit [Authorize(Policy = Policies.RequirePlus)] per endpoint.
/// </summary>
[ApiController]
[Route("api/staff")]
public class StaffController(CafePosDbContext db, ITenantContext tenant, IPasswordHasher<AppUser> hasher, IImageStorageService imageStorage, IAuditService audit) : ControllerBase
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

    /// <summary>Resolves the logged-in user's own StaffMember record, if any — powers
    /// the POS Checkout "Waiter" picker's self-service default, so a waiter taking
    /// their own order doesn't have to pick themselves from a list every time. 404
    /// when this login isn't linked to a roster entry (e.g. an Owner with no HR row).</summary>
    [HttpGet("me")]
    public async Task<ActionResult<StaffDto>> Me()
    {
        var idClaim = User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value
            ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (idClaim is null || !int.TryParse(idClaim, out var userId)) return NotFound();

        var staff = await db.Staff.FirstOrDefaultAsync(s => s.UserId == userId);
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
        if (req.PhotoUrl is not null) staff.PhotoUrl = await imageStorage.ResolveAsync("staff", req.PhotoUrl);

        await db.SaveChangesAsync();
        return StaffDto.From(staff);
    }

    /// <summary>
    /// Lets an Owner/Manager set a staff member's app-login password directly, no email/OTP
    /// involved — the self-service forgot-password flow needs a working outbound email
    /// route, but a manager standing next to the person doesn't. Also revokes their refresh
    /// token, matching AuthController.ResetPassword's "reset logs you out everywhere" rule.
    /// </summary>
    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpPost("{id:int}/reset-password")]
    public async Task<IActionResult> ResetPassword(int id, ResetStaffPasswordRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.NewPassword) || req.NewPassword.Length < 6)
            throw new ApiValidationException("New password must be at least 6 characters.");

        var staff = await db.Staff.FindAsync(id);
        if (staff is null) return NotFound();
        if (staff.UserId is null) throw new ApiValidationException("This staff member doesn't have app access.");

        var user = await db.Users.FindAsync(staff.UserId.Value);
        if (user is null) throw new ApiValidationException("This staff member doesn't have app access.");

        user.PasswordHash = hasher.HashPassword(user, req.NewPassword);
        // "Log out everywhere" — mirrors AuthController.ResetPassword's own revoke-all,
        // since RefreshTokenEntry is one row per device now, not a single field to null out.
        var activeSessions = await db.RefreshTokens.Where(t => t.UserId == user.Id && t.RevokedAt == null).ToListAsync();
        foreach (var entry in activeSessions) entry.RevokedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        var actor = await CurrentUserAsync();
        await audit.LogAsync(AuditAction.PasswordChange, AuditResource.Staff, staff.Id.ToString(),
            $"{actor.Name} reset the login password for {staff.Name}.", AuditSeverity.High, actor.Id, actor.Name);

        return NoContent();
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
    [Authorize(Policy = Policies.RequirePlus)]
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
    [Authorize(Policy = Policies.RequirePlus)]
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

    [Authorize(Policy = Policies.RequirePlus)]
    [HttpGet("{id:int}/shifts")]
    public async Task<IEnumerable<ShiftDto>> ListShifts(int id)
    {
        var shifts = await db.Shifts.Where(s => s.StaffId == id).OrderBy(s => s.StartsAt).ToListAsync();
        return shifts.Select(ShiftDto.From);
    }

    [Authorize(Policy = Policies.RequirePlus)]
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

    [Authorize(Policy = Policies.RequirePlus)]
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

    /// <summary>
    /// Real performance, computed from actual Orders and Shifts — not a manually
    /// submitted "review". OrdersHandled/TotalRevenue come from Order.ServedByStaffId
    /// (who actually took/served the order — not necessarily whoever was logged into
    /// a shared counter POS, see Order.ServedByStaffId's doc comment), so this works
    /// for every staff member on the roster whether or not they have an app login.
    /// AttendanceRatePct compares each staff member's completed shifts against real
    /// evidence they were actually working — a Login audit entry (if they have a
    /// login) or an order they served — inside that shift's window. Staff with no
    /// completed shifts yet get a null attendance rate rather than a fabricated number.
    /// </summary>
    /// <param name="periodStart">Omit both period params for all-time (unbounded). The
    /// Team Portal's date-range pills (Today/This Week/This Month) resolve to local IST
    /// boundaries client-side and pass them here as UTC instants.</param>
    [Authorize(Policy = Policies.RequirePlus)]
    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpGet("performance")]
    public async Task<IEnumerable<StaffPerformanceSummaryDto>> ListAllPerformance([FromQuery] DateTime? periodStart, [FromQuery] DateTime? periodEnd)
    {
        var staffList = await db.Staff.OrderBy(s => s.Name).ToListAsync();
        return (await ComputePerformanceAsync(staffList, periodStart, periodEnd)).OrderByDescending(x => x.TotalRevenue).ToList();
    }

    [Authorize(Policy = Policies.RequirePlus)]
    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpGet("{id:int}/performance")]
    public async Task<ActionResult<StaffPerformanceSummaryDto>> Performance(int id, [FromQuery] DateTime? periodStart, [FromQuery] DateTime? periodEnd)
    {
        var staff = await db.Staff.FindAsync(id);
        if (staff is null) return NotFound();

        var results = await ComputePerformanceAsync([staff], periodStart, periodEnd);
        return results[0];
    }

    private async Task<List<StaffPerformanceSummaryDto>> ComputePerformanceAsync(List<StaffMember> staffList, DateTime? periodStart, DateTime? periodEnd)
    {
        if (staffList.Count == 0) return [];
        var staffIds = staffList.Select(s => s.Id).ToList();

        var ordersQuery = db.Orders.Where(o => o.ServedByStaffId != null && staffIds.Contains(o.ServedByStaffId.Value));
        if (periodStart is DateTime ps) ordersQuery = ordersQuery.Where(o => o.CreatedAt >= ps);
        if (periodEnd is DateTime pe) ordersQuery = ordersQuery.Where(o => o.CreatedAt <= pe);
        var orders = await ordersQuery
            .Select(o => new { o.ServedByStaffId, o.CreatedAt, o.Total, o.Paid, o.Refunded })
            .ToListAsync();

        var now = DateTime.UtcNow;
        var shiftsQuery = db.Shifts.Where(s => staffIds.Contains(s.StaffId) && s.EndsAt <= now);
        if (periodStart is DateTime ss) shiftsQuery = shiftsQuery.Where(s => s.StartsAt >= ss);
        if (periodEnd is DateTime se) shiftsQuery = shiftsQuery.Where(s => s.EndsAt <= se);
        var pastShifts = await shiftsQuery.ToListAsync();

        var userIds = staffList.Where(s => s.UserId != null).Select(s => s.UserId!.Value).ToList();
        var loginsQuery = db.AuditLog.Where(a => a.Action == AuditAction.Login && a.UserId != null && userIds.Contains(a.UserId.Value));
        if (periodStart is DateTime ls) loginsQuery = loginsQuery.Where(a => a.Timestamp >= ls);
        if (periodEnd is DateTime le) loginsQuery = loginsQuery.Where(a => a.Timestamp <= le);
        var logins = await loginsQuery
            .Select(a => new { a.UserId, a.Timestamp })
            .ToListAsync();

        return staffList.Select(s =>
        {
            var myOrders = orders.Where(o => o.ServedByStaffId == s.Id).ToList();
            var billedOrders = myOrders.Where(o => o.Paid && !o.Refunded).ToList();

            var myShifts = pastShifts.Where(sh => sh.StaffId == s.Id).ToList();
            double? attendancePct = null;
            if (myShifts.Count > 0)
            {
                var myLoginTimes = logins.Where(l => l.UserId == s.UserId).Select(l => l.Timestamp).ToList();
                var myOrderTimes = myOrders.Select(o => o.CreatedAt).ToList();
                var present = myShifts.Count(sh =>
                    myLoginTimes.Any(t => t >= sh.StartsAt.AddMinutes(-30) && t <= sh.EndsAt) ||
                    myOrderTimes.Any(t => t >= sh.StartsAt && t <= sh.EndsAt));
                attendancePct = Math.Round(present / (double)myShifts.Count * 100, 1);
            }

            return new StaffPerformanceSummaryDto(s.Id, s.Name, s.Role, billedOrders.Count, billedOrders.Sum(o => o.Total), attendancePct);
        }).ToList();
    }

    [Authorize(Policy = Policies.RequirePlus)]
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
    [Authorize(Policy = Policies.RequirePlus)]
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
    [Authorize(Policy = Policies.RequirePlus)]
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
    [Authorize(Policy = Policies.RequirePlus)]
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

    [Authorize(Policy = Policies.RequirePlus)]
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
    [Authorize(Policy = Policies.RequirePlus)]
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
