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
public class StaffController(CafePosDbContext db, ITenantContext tenant, IPasswordHasher<AppUser> hasher, IImageStorageService imageStorage, IAuditService audit, IRealtimeNotifier realtime) : ControllerBase
{
    [HttpGet]
    public async Task<IEnumerable<StaffDto>> List([FromQuery] int? branchId)
    {
        var query = db.Staff.AsQueryable();
        if (branchId is not null) query = query.Where(s => s.BranchId == branchId);
        var staff = await query.OrderBy(s => s.Name).ToListAsync();
        var includeCompensation = IsOwnerOrManager();
        return staff.Select(s => StaffDto.From(s, includeCompensation));
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<StaffDto>> Get(int id)
    {
        var staff = await db.Staff.FindAsync(id);
        return staff is null ? NotFound() : StaffDto.From(staff, IsOwnerOrManager());
    }

    /// <summary>Whether the caller can see a staff member's compensation (HourlyRate/
    /// BasicSalary/Department/Designation) in List/Get — everything else in StaffDto is
    /// visible to any authenticated role. See StaffDto's doc comment.</summary>
    private bool IsOwnerOrManager() => User.IsInRole(nameof(AppRole.Owner)) || User.IsInRole(nameof(AppRole.Manager));

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
            Department = req.Department,
            Designation = req.Designation,
            SalaryType = req.SalaryType,
            BasicSalary = req.BasicSalary,
            BankAccountNumber = req.BankAccountNumber,
            BankIfsc = req.BankIfsc,
            BankName = req.BankName,
            Aadhaar = req.Aadhaar,
            Pan = req.Pan,
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
        if (req.Department is not null) staff.Department = req.Department.Trim();
        if (req.Designation is not null) staff.Designation = req.Designation.Trim();
        if (req.SalaryType is not null) staff.SalaryType = req.SalaryType.Value;
        if (req.BasicSalary is not null) staff.BasicSalary = req.BasicSalary;
        if (req.BankAccountNumber is not null) staff.BankAccountNumber = req.BankAccountNumber.Trim();
        if (req.BankIfsc is not null) staff.BankIfsc = req.BankIfsc.Trim();
        if (req.BankName is not null) staff.BankName = req.BankName.Trim();
        if (req.Aadhaar is not null) staff.Aadhaar = req.Aadhaar.Trim();
        if (req.Pan is not null) staff.Pan = req.Pan.Trim();

        await db.SaveChangesAsync();
        return StaffDto.From(staff);
    }

    /// <summary>Masked by default (e.g. "XXXX XXXX 1234") — Aadhaar/PAN/bank details never
    /// appear in StaffDto since GET /staff is reachable by any authenticated role.
    /// ?reveal=true returns full values and writes an audit entry, since this is the one
    /// place raw Aadhaar/PAN ever leaves the DB.</summary>
    [Authorize(Policy = Policies.RequirePlus)]
    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpGet("{id:int}/financial-details")]
    public async Task<ActionResult<StaffFinancialDetailsDto>> GetFinancialDetails(int id, [FromQuery] bool reveal = false)
    {
        var staff = await db.Staff.FindAsync(id);
        if (staff is null) return NotFound();

        if (!reveal)
        {
            return new StaffFinancialDetailsDto(
                staff.Id, Mask(staff.BankAccountNumber), staff.BankIfsc, staff.BankName, Mask(staff.Aadhaar), Mask(staff.Pan), false);
        }

        var actor = await CurrentUserAsync();
        await audit.LogAsync(AuditAction.Update, AuditResource.Staff, staff.Id.ToString(),
            $"{actor.Name} viewed {staff.Name}'s financial details.", AuditSeverity.High, actor.Id, actor.Name);

        return new StaffFinancialDetailsDto(staff.Id, staff.BankAccountNumber, staff.BankIfsc, staff.BankName, staff.Aadhaar, staff.Pan, true);
    }

    private static string? Mask(string? value) =>
        string.IsNullOrEmpty(value) ? value : value.Length <= 4 ? new string('X', value.Length) : new string('X', value.Length - 4) + value[^4..];

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

    /// <summary>
    /// Provisions app access for a staff member who was created without it — same
    /// validation and AppUser-creation shape as Create's inline login path, just
    /// deferred to whenever the owner/manager decides this person needs a login.
    /// </summary>
    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpPost("{id:int}/grant-access")]
    public async Task<ActionResult<StaffDto>> GrantAccess(int id, GrantStaffAccessRequest req)
    {
        var staff = await db.Staff.FindAsync(id);
        if (staff is null) return NotFound();
        if (staff.UserId is not null) throw new ApiConflictException("This staff member already has app access.");

        if (string.IsNullOrWhiteSpace(req.Email) || !req.Email.Contains('@'))
            throw new ApiValidationException("A valid email is required to give this staff member app access.");
        if (string.IsNullOrWhiteSpace(req.Password) || req.Password.Length < 6)
            throw new ApiValidationException("Password must be at least 6 characters.");
        if (req.LoginRole == AppRole.Owner)
            throw new ApiValidationException("Owner accounts are created only via cafe signup, not here.");

        var normalizedEmail = req.Email.Trim().ToLowerInvariant();
        if (await db.Users.AnyAsync(u => u.Email == normalizedEmail))
            throw new ApiConflictException("An account with this email already exists.");

        var user = new AppUser
        {
            TenantId = tenant.TenantIdOrDefault,
            Email = normalizedEmail,
            Name = staff.Name,
            Role = req.LoginRole,
            PasswordHash = "",
        };
        user.PasswordHash = hasher.HashPassword(user, req.Password);
        db.Users.Add(user);
        await db.SaveChangesAsync(); // assigns user.Id before StaffMember.UserId references it

        staff.UserId = user.Id;
        staff.Email = normalizedEmail;
        await db.SaveChangesAsync();

        var actor = await CurrentUserAsync();
        await audit.LogAsync(AuditAction.Update, AuditResource.Staff, staff.Id.ToString(),
            $"{actor.Name} gave {staff.Name} app access.", AuditSeverity.High, actor.Id, actor.Name);

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

    // ---------- Screen access ----------

    /// <summary>Current screen-visibility override for one staff member's login. Automatic
    /// (the default) means "keep following the AppRole default" — see permissions.ts.
    /// 404s when this staff member has no app login (nothing to configure) so the Staff
    /// Access screen can tell "no login yet" apart from "Automatic, nothing customized".</summary>
    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpGet("{id:int}/screen-access")]
    public async Task<ActionResult<StaffScreenAccessDto>> GetScreenAccess(int id)
    {
        var staff = await db.Staff.FindAsync(id);
        if (staff is null) return NotFound();
        if (staff.UserId is null) throw new ApiValidationException("This staff member doesn't have app access yet.");

        var user = await db.Users.FindAsync(staff.UserId.Value);
        if (user is null) throw new ApiValidationException("This staff member doesn't have app access yet.");

        return StaffScreenAccessDto.From(staff.Id, user);
    }

    /// <summary>
    /// Sets which screens this staff member's login can see. Custom mode's AllowedScreens
    /// is validated against ScreenCatalog on every write — an unknown key, or a key above
    /// the tenant's current plan, is rejected outright rather than silently dropped, so an
    /// Owner gets an explicit error instead of a picker that quietly doesn't do what it
    /// showed. Switching back to Automatic clears any stored list, so a later plan
    /// downgrade can never leave a stale Plus-only key lying around. The staff member's own
    /// device picks this up within moments via an accessChanged SignalR push — see
    /// useLiveAccessSync and RealtimeNotifier.NotifyAccessChangedAsync — falling back to
    /// useLiveAccessSync's own safety-net poll if that device's socket is down.
    /// </summary>
    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpPatch("{id:int}/screen-access")]
    public async Task<ActionResult<StaffScreenAccessDto>> UpdateScreenAccess(int id, UpdateStaffScreenAccessRequest req)
    {
        var staff = await db.Staff.FindAsync(id);
        if (staff is null) return NotFound();
        if (staff.UserId is null) throw new ApiValidationException("This staff member doesn't have app access yet.");

        var user = await db.Users.FindAsync(staff.UserId.Value);
        if (user is null) throw new ApiValidationException("This staff member doesn't have app access yet.");
        if (user.Role == AppRole.Owner) throw new ApiValidationException("An Owner login always has full access and can't be restricted.");

        if (req.AccessMode == StaffAccessMode.Custom)
        {
            var sub = await db.Subscriptions.FirstOrDefaultAsync();
            var tenantPlan = (sub?.Plan ?? SubscriptionTier.FreeTrial).ToCategory();
            var requested = (req.AllowedScreens ?? []).Distinct().ToList();

            var unknown = requested.Where(k => !ScreenCatalog.IsValidKey(k)).ToList();
            if (unknown.Count > 0) throw new ApiValidationException($"Unknown screen(s): {string.Join(", ", unknown)}.");

            var abovePlan = requested.Where(k => !ScreenCatalog.IsAssignableAt(k, tenantPlan)).ToList();
            if (abovePlan.Count > 0) throw new ApiValidationException($"These screens need a higher plan than this cafe currently has: {string.Join(", ", abovePlan)}.");

            user.AllowedScreens = requested;
        }
        else
        {
            user.AllowedScreens = null;
        }
        user.AccessMode = req.AccessMode;
        await db.SaveChangesAsync();
        // Fire-and-forget, same as CafePosDbContext's ordersChanged push — a dropped push
        // just leaves useLiveAccessSync's safety-net poll to catch up.
        _ = realtime.NotifyAccessChangedAsync(user.TenantId, user.Id);

        var actor = await CurrentUserAsync();
        var summary = req.AccessMode == StaffAccessMode.Custom
            ? $"{actor.Name} set custom screen access for {staff.Name} ({(req.AllowedScreens ?? []).Count} screen(s))."
            : $"{actor.Name} reset {staff.Name}'s screen access to Automatic (role default).";
        await audit.LogAsync(AuditAction.PermissionChange, AuditResource.Staff, staff.Id.ToString(), summary, AuditSeverity.Medium, actor.Id, actor.Name);

        return StaffScreenAccessDto.From(staff.Id, user);
    }

    // ---------- Kitchen assignment (KDS station pinning) ----------

    /// <summary>Which kitchen station (if any) this staff member's login is pinned to —
    /// see AppUser.AssignedStationId. 404s when this staff member has no app login, same
    /// convention as GetScreenAccess above.</summary>
    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpGet("{id:int}/kitchen-assignment")]
    public async Task<ActionResult<StaffKitchenAssignmentDto>> GetKitchenAssignment(int id)
    {
        var staff = await db.Staff.FindAsync(id);
        if (staff is null) return NotFound();
        if (staff.UserId is null) throw new ApiValidationException("This staff member doesn't have app access yet.");

        var user = await db.Users.FindAsync(staff.UserId.Value);
        if (user is null) throw new ApiValidationException("This staff member doesn't have app access yet.");

        return StaffKitchenAssignmentDto.From(staff.Id, user);
    }

    /// <summary>
    /// Pins (or clears) which single kitchen station's KOTs this login's KDS shows —
    /// so a Chef/KitchenStaff login always lands on their own kitchen the moment they
    /// open the app, on any device, with no manual station-tab switching. An Owner login
    /// can't be pinned (always sees every kitchen, same rule as screen access above).
    /// Clearing it (null) falls back to that device's own remembered station (see
    /// kdsDeviceSettings on the client) instead of a server-side pin. Picks up on the
    /// staff member's device within moments via the same "accessChanged" SignalR push
    /// that carries AccessMode (see UpdateScreenAccess above), falling back to
    /// useLiveAccessSync's 60s safety-net poll if that device's socket is down.
    /// </summary>
    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpPatch("{id:int}/kitchen-assignment")]
    public async Task<ActionResult<StaffKitchenAssignmentDto>> UpdateKitchenAssignment(int id, UpdateStaffKitchenAssignmentRequest req)
    {
        var staff = await db.Staff.FindAsync(id);
        if (staff is null) return NotFound();
        if (staff.UserId is null) throw new ApiValidationException("This staff member doesn't have app access yet.");

        var user = await db.Users.FindAsync(staff.UserId.Value);
        if (user is null) throw new ApiValidationException("This staff member doesn't have app access yet.");
        if (user.Role == AppRole.Owner) throw new ApiValidationException("An Owner login always sees every kitchen and can't be pinned to one.");

        if (req.AssignedStationId is int stationId)
        {
            var stationExists = await db.Stations.AnyAsync(s => s.Id == stationId && s.Active);
            if (!stationExists) throw new ApiValidationException("That kitchen station doesn't exist or is no longer active.");
        }

        user.AssignedStationId = req.AssignedStationId;
        await db.SaveChangesAsync();
        // Fire-and-forget, same as UpdateScreenAccess — without this, the pin only ever
        // reached the staff member's device via the 60s poll, which read as "not working"
        // to an owner checking KDS right after saving.
        _ = realtime.NotifyAccessChangedAsync(user.TenantId, user.Id);

        var actor = await CurrentUserAsync();
        var summary = req.AssignedStationId is null
            ? $"{actor.Name} unpinned {staff.Name}'s KDS from any single kitchen."
            : $"{actor.Name} pinned {staff.Name}'s KDS to station #{req.AssignedStationId}.";
        await audit.LogAsync(AuditAction.PermissionChange, AuditResource.Staff, staff.Id.ToString(), summary, AuditSeverity.Medium, actor.Id, actor.Name);

        return StaffKitchenAssignmentDto.From(staff.Id, user);
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

    // Payroll is now its own PayrollController — see PayrollRun/PayrollLine.

    // ---------- Leave requests ----------

    /// <summary>All leave requests, newest first — optionally filtered to one status
    /// (Pending/Approved/Rejected) for the "tabs" the Leave screen shows. The Owner/Manager
    /// admin view across all staff — see GetMyLeaveRequests below for the self-service
    /// counterpart; without this gate any authenticated login could pull every staff
    /// member's leave requests (including free-text reasons) directly over the wire.</summary>
    [Authorize(Policy = Policies.RequirePlus)]
    [Authorize(Policy = Policies.OwnerOrManager)]
    // HRReports too — that screen charts leave alongside payroll runs. Deliberately NOT on
    // the "/me" self-service actions below: those are open to any login by design.
    [RequireScreen("Leave", "HRReports")]
    [HttpGet("leave-requests")]
    public async Task<IEnumerable<LeaveRequestDto>> ListLeaveRequests([FromQuery] LeaveRequestStatus? status)
    {
        var query = db.LeaveRequests.AsQueryable();
        if (status is not null) query = query.Where(l => l.Status == status);
        var requests = await query.OrderByDescending(l => l.CreatedAt).ToListAsync();
        return requests.Select(LeaveRequestDto.From);
    }

    /// <summary>Owner/Manager filing leave on a staff member's behalf via Team Portal's
    /// staff picker — accepts an arbitrary StaffId, which is why this is restricted to
    /// Owner/Manager (matches Approve/Reject/ReturnToWork below) even though Cashier/
    /// Accountant can otherwise reach Team Portal. For a staff member requesting their
    /// own leave, see CreateMyLeaveRequest below instead.</summary>
    [Authorize(Policy = Policies.RequirePlus)]
    [Authorize(Policy = Policies.OwnerOrManager)]
    [RequireScreen("Leave")]
    [HttpPost("leave-requests")]
    public async Task<ActionResult<LeaveRequestDto>> CreateLeaveRequest(CreateLeaveRequest req)
    {
        var dto = await CreateLeaveRequestInternal(req.StaffId, req.StartDate, req.EndDate, req.Type, req.Reason);
        return CreatedAtAction(nameof(ListLeaveRequests), dto);
    }

    /// <summary>Self-service leave request — always for the caller's own linked staff
    /// roster row (see CurrentStaffResolver), never an arbitrary StaffId from the client.
    /// Lets a Waiter/Chef/etc. who has no Team Portal access still request their own leave
    /// (approval still requires Owner/Manager, same as CreateLeaveRequest above).</summary>
    [Authorize(Policy = Policies.RequirePlus)]
    [HttpPost("leave-requests/me")]
    public async Task<ActionResult<LeaveRequestDto>> CreateMyLeaveRequest(CreateMyLeaveRequest req)
    {
        var staff = await CurrentStaffAsync();
        var dto = await CreateLeaveRequestInternal(staff.Id, req.StartDate, req.EndDate, req.Type, req.Reason);
        return CreatedAtAction(nameof(GetMyLeaveRequests), dto);
    }

    /// <summary>This staff member's own leave requests, newest first — the self-service
    /// counterpart to ListLeaveRequests (the Owner/Manager admin view across all staff).
    /// Its own endpoint rather than a staffId filter on ListLeaveRequests, so a Waiter's
    /// device never receives another staff member's leave data over the wire.</summary>
    [Authorize(Policy = Policies.RequirePlus)]
    [HttpGet("leave-requests/me")]
    public async Task<IEnumerable<LeaveRequestDto>> GetMyLeaveRequests([FromQuery] LeaveRequestStatus? status)
    {
        var staff = await CurrentStaffAsync();
        var query = db.LeaveRequests.Where(l => l.StaffId == staff.Id);
        if (status is not null) query = query.Where(l => l.Status == status);
        var requests = await query.OrderByDescending(l => l.CreatedAt).ToListAsync();
        return requests.Select(LeaveRequestDto.From);
    }

    private async Task<LeaveRequestDto> CreateLeaveRequestInternal(int staffId, DateOnly startDate, DateOnly endDate, LeaveType type, string? reason)
    {
        var staff = await db.Staff.FindAsync(staffId);
        if (staff is null) throw new ApiValidationException("Staff member not found.");
        if (endDate < startDate) throw new ApiValidationException("End date must be on or after the start date.");

        var leave = new LeaveRequest
        {
            StaffId = staff.Id,
            StaffName = staff.Name,
            StartDate = startDate,
            EndDate = endDate,
            Type = type,
            Reason = reason?.Trim(),
        };
        db.LeaveRequests.Add(leave);
        await db.SaveChangesAsync(); // assigns leave.Id before the mirror below references it

        // Mirrored into the unified Approvals inbox (ApprovalsController) so an Owner/Manager
        // sees leave requests alongside Refund/Discount/Expense approvals in one place.
        // LeaveRequest.Status stays the one source of truth for "is this staff member
        // actually on leave" — Approve/Reject here and via ApprovalsController.Approve/
        // Reject (Type == Leave) each update both records, in either direction.
        var actor = await CurrentUserAsync();
        db.Approvals.Add(new ApprovalRequest
        {
            Type = ApprovalType.Leave,
            RequestedById = actor.Id,
            Title = $"{type} leave — {staff.Name}",
            Description = $"{startDate:MMM d} to {endDate:MMM d}" + (string.IsNullOrWhiteSpace(reason) ? "" : $" — {reason.Trim()}"),
            LinkedEntityId = leave.Id,
        });
        await db.SaveChangesAsync();

        return LeaveRequestDto.From(leave);
    }

    /// <summary>Finds this leave request's mirrored ApprovalRequest (see CreateLeaveRequest),
    /// if one exists — old leave requests from before this mirroring existed won't have one.</summary>
    private Task<ApprovalRequest?> LinkedApprovalAsync(int leaveId) =>
        db.Approvals.FirstOrDefaultAsync(a => a.Type == ApprovalType.Leave && a.LinkedEntityId == leaveId);

    /// <summary>Approving immediately puts the staff member on leave (see LeaveRequest's
    /// doc comment for why this is manual rather than date-triggered).</summary>
    [Authorize(Policy = Policies.RequirePlus)]
    [Authorize(Policy = Policies.OwnerOrManager)]
    [RequireScreen("Leave")]
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

        var linkedApproval = await LinkedApprovalAsync(leave.Id);
        if (linkedApproval is not null)
        {
            linkedApproval.Status = ApprovalStatus.Approved;
            linkedApproval.ResolvedAt = DateTime.UtcNow;
            linkedApproval.ResolvedById = reviewer.Id;
        }

        await db.SaveChangesAsync();
        return LeaveRequestDto.From(leave);
    }

    [Authorize(Policy = Policies.RequirePlus)]
    [Authorize(Policy = Policies.OwnerOrManager)]
    [RequireScreen("Leave")]
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

        var linkedApproval = await LinkedApprovalAsync(leave.Id);
        if (linkedApproval is not null)
        {
            linkedApproval.Status = ApprovalStatus.Rejected;
            linkedApproval.ResolvedAt = DateTime.UtcNow;
            linkedApproval.ResolvedById = reviewer.Id;
            linkedApproval.Notes = req.Note;
        }

        await db.SaveChangesAsync();
        return LeaveRequestDto.From(leave);
    }

    /// <summary>Ends an approved leave early / marks the staff member back at work —
    /// the counterpart to Approve's OnLeave flip.</summary>
    [Authorize(Policy = Policies.RequirePlus)]
    [Authorize(Policy = Policies.OwnerOrManager)]
    [RequireScreen("Leave")]
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

    private Task<StaffMember> CurrentStaffAsync() => this.GetOrCreateCurrentStaffAsync(db);
}
