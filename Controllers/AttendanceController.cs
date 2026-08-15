using CafePOS.Api.Contracts;
using CafePOS.Api.Data;
using CafePOS.Api.Domain;
using CafePOS.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CafePOS.Api.Controllers;

/// <summary>
/// Punch in/out is an in-app button tap, GPS-tagged and geofenced (see
/// EnsureWithinGeofenceAsync) — there's no biometric/QR hardware integration to build
/// against, and self-service punching happens from a staff member's own phone (not a
/// fixed on-premise device), so location is required and checked against the cafe's
/// registered coordinates (CafeSettings.Latitude/Longitude) rather than merely tagged
/// along. Self-service endpoints resolve StaffId from the JWT (mirrors
/// StaffController.Me()) so a staff member can never punch in/out or view attendance
/// for anyone but themselves. Nothing here ever checks payroll-lock status — see
/// PayrollLine's doc comment for why a locked payroll number can never drift under a
/// later attendance correction.
/// </summary>
[ApiController]
[Route("api/attendance")]
// Covers the self-service My Attendance screen as well — it's guarded by the same
// "Attendance" catalog key on the client (see AppNavigator's MyAttendanceScreenComp).
[RequireScreen("Attendance")]
public class AttendanceController(CafePosDbContext db, IAuditService audit) : ControllerBase
{
    /// <summary>Punch in/out must happen within this radius of the cafe's registered
    /// location — loose enough to absorb ordinary phone-GPS drift, tight enough to stop
    /// a punch from home. A cafe with no registered location can't punch at all (staff
    /// are told to get the Owner to set it first) — see EnsureWithinGeofenceAsync.</summary>
    private const double GeofenceRadiusMeters = 500;

    /// <summary>A roll-call mark for a day with no scheduled Shift has to start the
    /// worked window somewhere — 9 AM cafe time, the same "we had to pick something"
    /// role CafeSettings.StandardShiftHours plays for its length. Only the duration
    /// reaches payroll, so the choice of start only shows as the row's displayed
    /// punch-in time.</summary>
    private static readonly TimeSpan DefaultDayStartIst = TimeSpan.FromHours(9);

    /// <summary>Ceiling on one roll-call batch — comfortably above any single cafe's
    /// roster, low enough that a hand-crafted request can't turn one call into thousands
    /// of shift lookups.</summary>
    private const int MaxMarkEntries = 200;

    // ---------- Self-service punch state machine ----------

    [Authorize(Policy = Policies.RequirePlus)]
    [HttpPost("punch-in")]
    public async Task<ActionResult<AttendanceRecordDto>> PunchIn(PunchRequest req)
    {
        var staff = await CurrentStaffAsync();
        // Self-service punch-in is strictly "now, for today" — the date must be today's
        // (cafe clock, see IstClock) and the timestamp is ALWAYS the server's. The
        // request's OccurredAt is deliberately ignored: attendance drives payroll, and
        // trusting a client-supplied time would let anyone erase a late mark or inflate
        // hours with a hand-crafted API call. Backdating is the Owner/Manager-gated,
        // note-required, audited manual/correct endpoints' job.
        if (req.LocalDate != TodayIst())
            throw new ApiValidationException("Punch-in can only be recorded for today.");
        // Self-service punch-in stays shift-blind by default (General) — a staff member
        // punching in from their own phone doesn't pick a shift explicitly today; that
        // only matters once/if the mobile app grows a shift picker of its own.
        var shiftKind = req.ShiftKind ?? ShiftKind.General;
        var existing = await db.AttendanceRecords.FirstOrDefaultAsync(a => a.StaffId == staff.Id && a.Date == req.LocalDate && a.ShiftKind == shiftKind);
        if (existing?.PunchInAt is not null) throw new ApiConflictException("Already punched in for this day.");

        var occurredAt = DateTime.UtcNow;
        var shift = await FindShiftForDay(staff.Id, req.LocalDate);
        var settings = await CurrentSettingsAsync();
        await EnsureWithinGeofenceAsync(req, settings);

        var record = existing ?? new AttendanceRecord { StaffId = staff.Id, Date = req.LocalDate, ShiftKind = shiftKind };
        record.ShiftId = shift?.Id;
        record.PunchInAt = occurredAt;
        record.LateMinutes = ComputeLateMinutes(occurredAt, shift, settings.LateGraceMinutes);
        record.Status = record.LateMinutes > 0 ? AttendanceStatus.Late : AttendanceStatus.Present;
        if (existing is null) db.AttendanceRecords.Add(record);

        db.AttendanceLogs.Add(new AttendanceLog
        {
            StaffId = staff.Id, Type = AttendanceLogType.PunchIn, Timestamp = occurredAt,
            Latitude = req.Latitude, Longitude = req.Longitude, Source = AttendanceLogSource.Staff,
        });
        await db.SaveChangesAsync();
        return AttendanceRecordDto.From(record, staff.Name);
    }

    [Authorize(Policy = Policies.RequirePlus)]
    [HttpPost("punch-out")]
    public async Task<ActionResult<AttendanceRecordDto>> PunchOut(PunchRequest req)
    {
        var staff = await CurrentStaffAsync();
        // Server time only — same anti-spoofing rule as PunchIn. FindOpenRecordAsync
        // also handles the overnight case: a shift that crossed midnight lives on
        // yesterday's record, and "punch out" at 1 AM must close THAT session instead of
        // failing with "not punched in" against today's empty date.
        EnsureTodayOrYesterday(req.LocalDate);
        var record = await FindOpenRecordAsync(staff.Id, req.LocalDate);
        if (record?.PunchInAt is null) throw new ApiConflictException("Not punched in for this day.");
        if (record.PunchOutAt is not null) throw new ApiConflictException("Already punched out for this day.");

        var occurredAt = DateTime.UtcNow;
        if (occurredAt <= record.PunchInAt.Value) throw new ApiValidationException("Punch-out must be after punch-in.");

        var settings = await CurrentSettingsAsync();
        await EnsureWithinGeofenceAsync(req, settings);
        await AutoCloseOpenBreakAsync(staff.Id, record, occurredAt);

        var shift = record.ShiftId is int shiftId ? await db.Shifts.FindAsync(shiftId) : null;

        record.PunchOutAt = occurredAt;
        var totalMinutes = (int)Math.Max(0, (occurredAt - record.PunchInAt.Value).TotalMinutes - record.BreakMinutes);
        record.WorkedMinutes = totalMinutes;

        var scheduledEnd = shift?.EndsAt ?? record.PunchInAt.Value.AddHours(settings.StandardShiftHours);
        record.OvertimeMinutes = (int)Math.Max(0, (occurredAt - scheduledEnd).TotalMinutes);

        record.Status = totalMinutes < settings.HalfDayThresholdHours * 60
            ? AttendanceStatus.HalfDay
            : record.LateMinutes > 0 ? AttendanceStatus.Late : AttendanceStatus.Present;

        db.AttendanceLogs.Add(new AttendanceLog
        {
            StaffId = staff.Id, Type = AttendanceLogType.PunchOut, Timestamp = occurredAt,
            Latitude = req.Latitude, Longitude = req.Longitude, Source = AttendanceLogSource.Staff,
        });

        // Gated by Settings.ShiftReportsEnabled centrally (see CafePosDbContext.
        // SaveChangesAsync's NotificationGates) — created unconditionally here, same as
        // OrderBuildingService's notifications.
        var hours = totalMinutes / 60;
        var mins = totalMinutes % 60;
        var extras = new List<string>();
        if (record.LateMinutes > 0) extras.Add($"{record.LateMinutes}m late");
        if (record.OvertimeMinutes > 0) extras.Add($"{record.OvertimeMinutes}m overtime");
        db.Notifications.Add(new AppNotification
        {
            Title = "Shift report",
            Body = $"{staff.Name} clocked out — {hours}h {mins}m worked" + (extras.Count > 0 ? $" ({string.Join(", ", extras)})" : "") + ".",
            Category = NotificationCategory.Staff,
            Channel = NotificationChannel.InApp,
            ActionUrl = "/attendance",
            TargetRolesCsv = NotificationAudience.Management,
        });

        await db.SaveChangesAsync();
        return AttendanceRecordDto.From(record, staff.Name);
    }

    [Authorize(Policy = Policies.RequirePlus)]
    [HttpPost("break-start")]
    public async Task<IActionResult> BreakStart(PunchRequest req)
    {
        var staff = await CurrentStaffAsync();
        // Same rules as PunchOut: server time only, and the open session may live on
        // yesterday's record (overnight shift) — see FindOpenRecordAsync.
        EnsureTodayOrYesterday(req.LocalDate);
        var record = await FindOpenRecordAsync(staff.Id, req.LocalDate);
        if (record?.PunchInAt is null || record.PunchOutAt is not null) throw new ApiConflictException("Not currently punched in.");

        var lastLog = await LastLogSinceAsync(staff.Id, record.PunchInAt.Value);
        if (lastLog?.Type == AttendanceLogType.BreakStart) throw new ApiConflictException("A break is already in progress.");

        db.AttendanceLogs.Add(new AttendanceLog
        {
            StaffId = staff.Id, Type = AttendanceLogType.BreakStart, Timestamp = DateTime.UtcNow,
            Latitude = req.Latitude, Longitude = req.Longitude, Source = AttendanceLogSource.Staff,
        });
        await db.SaveChangesAsync();
        return NoContent();
    }

    [Authorize(Policy = Policies.RequirePlus)]
    [HttpPost("break-end")]
    public async Task<ActionResult<AttendanceRecordDto>> BreakEnd(PunchRequest req)
    {
        var staff = await CurrentStaffAsync();
        EnsureTodayOrYesterday(req.LocalDate);
        var record = await FindOpenRecordAsync(staff.Id, req.LocalDate);
        if (record?.PunchInAt is null || record.PunchOutAt is not null) throw new ApiConflictException("Not currently punched in.");

        var lastLog = await LastLogSinceAsync(staff.Id, record.PunchInAt.Value);
        if (lastLog?.Type != AttendanceLogType.BreakStart) throw new ApiConflictException("No break is currently in progress.");

        var occurredAt = DateTime.UtcNow;
        record.BreakMinutes += (int)Math.Max(0, (occurredAt - lastLog.Timestamp).TotalMinutes);

        db.AttendanceLogs.Add(new AttendanceLog
        {
            StaffId = staff.Id, Type = AttendanceLogType.BreakEnd, Timestamp = occurredAt,
            Latitude = req.Latitude, Longitude = req.Longitude, Source = AttendanceLogSource.Staff,
        });
        await db.SaveChangesAsync();
        return AttendanceRecordDto.From(record, staff.Name);
    }

    [Authorize(Policy = Policies.RequirePlus)]
    [HttpGet("me")]
    public async Task<IEnumerable<AttendanceRecordDto>> GetMine([FromQuery] DateOnly? periodStart, [FromQuery] DateOnly? periodEnd)
    {
        var staff = await CurrentStaffAsync();
        var query = db.AttendanceRecords.Where(a => a.StaffId == staff.Id);
        if (periodStart is DateOnly ps) query = query.Where(a => a.Date >= ps);
        if (periodEnd is DateOnly pe) query = query.Where(a => a.Date <= pe);
        var records = await query.OrderByDescending(a => a.Date).ToListAsync();
        return records.Select(r => AttendanceRecordDto.From(r, staff.Name));
    }

    // ---------- Admin ----------

    [Authorize(Policy = Policies.RequirePlus)]
    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpGet]
    public async Task<IEnumerable<AttendanceRecordDto>> List([FromQuery] int? staffId, [FromQuery] DateOnly? date, [FromQuery] DateOnly? periodStart, [FromQuery] DateOnly? periodEnd, [FromQuery] ShiftKind? shiftKind)
    {
        var query = db.AttendanceRecords.AsQueryable();
        if (staffId is int sid) query = query.Where(a => a.StaffId == sid);
        if (date is DateOnly d) query = query.Where(a => a.Date == d);
        if (periodStart is DateOnly ps) query = query.Where(a => a.Date >= ps);
        if (periodEnd is DateOnly pe) query = query.Where(a => a.Date <= pe);
        // Scopes the roster to exactly one shift — the Attendance screen's shift tab
        // relies on this to keep "one record per staffId" true for a given date, since
        // a staff member can now have up to 4 rows (one per shift) on the same day.
        if (shiftKind is ShiftKind sk) query = query.Where(a => a.ShiftKind == sk);
        var records = await query.OrderByDescending(a => a.Date).ToListAsync();

        var staffIds = records.Select(r => r.StaffId).Distinct().ToList();
        var names = await db.Staff.Where(s => staffIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name);
        return records.Select(r => AttendanceRecordDto.From(r, names.GetValueOrDefault(r.StaffId, "Unknown")));
    }

    [Authorize(Policy = Policies.RequirePlus)]
    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpGet("{id:int}")]
    public async Task<ActionResult<AttendanceRecordDto>> Get(int id)
    {
        var record = await db.AttendanceRecords.FindAsync(id);
        if (record is null) return NotFound();
        var staff = await db.Staff.FindAsync(record.StaffId);
        return AttendanceRecordDto.From(record, staff?.Name ?? "Unknown");
    }

    [Authorize(Policy = Policies.RequirePlus)]
    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpPost("manual")]
    public async Task<ActionResult<AttendanceRecordDto>> CreateManual(ManualAttendanceRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.EditNote)) throw new ApiValidationException("A note explaining this manual entry is required.");
        var staff = await db.Staff.FindAsync(req.StaffId);
        if (staff is null) throw new ApiValidationException("Staff member not found.");
        if (req.PunchOutAt is not null && req.PunchInAt is not null && req.PunchOutAt <= req.PunchInAt)
            throw new ApiValidationException("Punch-out must be after punch-in.");

        var shiftKind = req.ShiftKind ?? ShiftKind.General;
        var record = await db.AttendanceRecords.FirstOrDefaultAsync(a => a.StaffId == req.StaffId && a.Date == req.Date && a.ShiftKind == shiftKind)
            ?? new AttendanceRecord { StaffId = req.StaffId, Date = req.Date, ShiftKind = shiftKind };
        var isNew = record.Id == 0;

        var shift = record.ShiftId is int existingShiftId ? await db.Shifts.FindAsync(existingShiftId) : await FindShiftForDay(req.StaffId, req.Date);
        record.ShiftId = shift?.Id;

        var actor = await CurrentUserAsync();
        await ApplyTimesAsync(record, req.PunchInAt, req.PunchOutAt, req.BreakMinutes, shift, actor.Id, req.EditNote);

        if (isNew) db.AttendanceRecords.Add(record);
        // Staged before the save below so both commit in one round trip. A brand-new
        // record's Id isn't assigned until SaveChanges runs, so resourceId stays null
        // for that case — an existing record being corrected here already has its Id.
        audit.Stage(AuditAction.Update, AuditResource.Attendance, isNew ? null : record.Id.ToString(),
            $"{actor.Name} manually recorded attendance for {staff.Name} on {req.Date}.", AuditSeverity.Medium, actor.Id, actor.Name);
        await db.SaveChangesAsync();

        return AttendanceRecordDto.From(record, staff.Name);
    }

    [Authorize(Policy = Policies.RequirePlus)]
    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpPatch("{id:int}")]
    public async Task<ActionResult<AttendanceRecordDto>> Correct(int id, CorrectAttendanceRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.EditNote)) throw new ApiValidationException("A note explaining this correction is required.");
        var record = await db.AttendanceRecords.FindAsync(id);
        if (record is null) return NotFound();
        var staff = await db.Staff.FindAsync(record.StaffId);
        if (staff is null) return NotFound();

        var punchIn = req.PunchInAt ?? record.PunchInAt;
        var punchOut = req.PunchOutAt ?? record.PunchOutAt;
        if (punchOut is not null && punchIn is not null && punchOut <= punchIn)
            throw new ApiValidationException("Punch-out must be after punch-in.");

        var shift = record.ShiftId is int shiftId ? await db.Shifts.FindAsync(shiftId) : null;
        var actor = await CurrentUserAsync();
        await ApplyTimesAsync(record, punchIn, punchOut, req.BreakMinutes ?? record.BreakMinutes, shift, actor.Id, req.EditNote);
        if (req.Status is AttendanceStatus explicitStatus) record.Status = explicitStatus;

        // record.Id is already known (this is always an existing row — see FindAsync
        // above), so staging it before the save carries zero behavior change, purely
        // one fewer round trip.
        audit.Stage(AuditAction.Update, AuditResource.Attendance, record.Id.ToString(),
            $"{actor.Name} corrected {staff.Name}'s attendance for {record.Date}: {req.EditNote}", AuditSeverity.Medium, actor.Id, actor.Name);
        await db.SaveChangesAsync();
        return AttendanceRecordDto.From(record, staff.Name);
    }

    /// <summary>
    /// Roll-call marking: one tap sets a staff member's status for a day, no punch times
    /// and no note required (CreateManual's note is mandatory because it invents times
    /// out of nothing; here the times are derived from the day's Shift, so the note is
    /// generated). Entries are batched so "Mark all present" is a single round trip.
    /// Real punches always win — a day the staff member actually punched keeps its
    /// timestamps and only has its Status/derived fields recomputed, so marking someone
    /// Present can never quietly erase the fact that they clocked in at 11:40.
    /// </summary>
    [Authorize(Policy = Policies.RequirePlus)]
    [Authorize(Policy = Policies.OwnerOrManager)]
    [HttpPost("mark")]
    public async Task<ActionResult<IEnumerable<AttendanceRecordDto>>> Mark(MarkAttendanceRequest req)
    {
        if (req.Entries is null || req.Entries.Count == 0)
            throw new ApiValidationException("Choose at least one staff member to mark.");
        if (req.Entries.Count > MaxMarkEntries)
            throw new ApiValidationException($"Attendance can be marked for at most {MaxMarkEntries} staff at a time.");
        // Backdating is fine (that's the point — yesterday's roll call gets filled in this
        // morning), but a future day has no attendance to record yet.
        if (req.Date > TodayIst())
            throw new ApiValidationException("Attendance can't be marked for a future date.");

        var entries = req.Entries.DistinctBy(e => e.StaffId).ToList();
        var staffIds = entries.Select(e => e.StaffId).ToList();
        var staff = await db.Staff.Where(s => staffIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id);
        if (staff.Count != staffIds.Count) throw new ApiValidationException("Staff member not found.");

        var settings = await CurrentSettingsAsync();
        var actor = await CurrentUserAsync();
        var existing = await db.AttendanceRecords
            .Where(a => a.Date == req.Date && a.ShiftKind == req.ShiftKind && staffIds.Contains(a.StaffId)).ToListAsync();

        var records = new List<AttendanceRecord>();
        foreach (var entry in entries)
        {
            var record = existing.FirstOrDefault(a => a.StaffId == entry.StaffId);
            if (record is null)
            {
                record = new AttendanceRecord { StaffId = entry.StaffId, Date = req.Date, ShiftKind = req.ShiftKind };
                db.AttendanceRecords.Add(record);
            }

            var shift = record.ShiftId is int shiftId ? await db.Shifts.FindAsync(shiftId) : await FindShiftForDay(entry.StaffId, req.Date);
            record.ShiftId = shift?.Id;
            ApplyMarkedStatus(record, entry.Status, shift, settings);
            record.IsManuallyEdited = true;
            record.EditedByUserId = actor.Id;
            record.EditNote = $"Marked {StatusLabel(entry.Status)} by {actor.Name}.";
            records.Add(record);
        }

        var summary = string.Join(", ", records.Select(r => $"{staff[r.StaffId].Name}: {StatusLabel(r.Status)}"));
        if (summary.Length > 400) summary = summary[..400] + "…";
        // Staged, not logged: a brand-new record's Id isn't assigned until the
        // SaveChangesAsync below runs, so this always logs against the day as a whole
        // (null resourceId) — matching what the multi-entry case already did. Staging
        // it here instead of calling LogAsync after the save is what turns "tap
        // Present" from two DB round trips into one (see AuditService.Stage).
        audit.Stage(AuditAction.Update, AuditResource.Attendance, null,
            $"{actor.Name} marked attendance for {req.Date} — {summary}", AuditSeverity.Medium, actor.Id, actor.Name);

        await db.SaveChangesAsync();

        return records.Select(r => AttendanceRecordDto.From(r, staff[r.StaffId].Name)).ToList();
    }

    // ---------- Shared derivation logic ----------

    /// <summary>Fills in the times a roll-call mark implies, then stamps the status the
    /// Owner/Manager actually chose (unlike ApplyTimesAsync, which derives the status
    /// from the times — here the human's choice is the input, not the output). Present/
    /// Late/Half Day get a full/half scheduled day; Absent/On Leave/Holiday clear the
    /// times so payroll counts no worked day (see PayrollController.BuildLineAsync,
    /// which counts a day as worked exactly when WorkedMinutes is non-null).</summary>
    private static void ApplyMarkedStatus(AttendanceRecord record, AttendanceStatus status, Shift? shift, CafeSettings settings)
    {
        var workedDay = status is AttendanceStatus.Present or AttendanceStatus.Late or AttendanceStatus.HalfDay;
        if (!workedDay)
        {
            record.PunchInAt = null;
            record.PunchOutAt = null;
            record.BreakMinutes = 0;
            record.WorkedMinutes = null;
            record.LateMinutes = 0;
            record.OvertimeMinutes = 0;
            record.Status = status;
            return;
        }

        var scheduledMinutes = shift is not null
            ? Math.Max(1, (int)(shift.EndsAt - shift.StartsAt).TotalMinutes)
            : settings.StandardShiftHours * 60;
        var targetMinutes = status == AttendanceStatus.HalfDay ? scheduledMinutes / 2 : scheduledMinutes;

        // An existing punch-in is kept as-is; only a day with no punch at all invents a
        // start, from the shift if there is one and otherwise from DefaultDayStartIst.
        var punchIn = record.PunchInAt ?? shift?.StartsAt ?? IstClock.IstDateStartUtc(record.Date) + DefaultDayStartIst;
        record.PunchInAt = punchIn;
        // Someone who punched in and forgot to punch out gets closed out here — leaving
        // PunchOutAt null would leave WorkedMinutes null and payroll would ignore the day
        // the manager just said was worked.
        if (record.PunchOutAt is null || record.PunchOutAt <= punchIn)
            record.PunchOutAt = punchIn.AddMinutes(targetMinutes + record.BreakMinutes);

        record.WorkedMinutes = (int)Math.Max(0, (record.PunchOutAt.Value - punchIn).TotalMinutes - record.BreakMinutes);
        record.LateMinutes = ComputeLateMinutes(punchIn, shift, settings.LateGraceMinutes);
        var scheduledEnd = shift?.EndsAt ?? punchIn.AddHours(settings.StandardShiftHours);
        record.OvertimeMinutes = (int)Math.Max(0, (record.PunchOutAt.Value - scheduledEnd).TotalMinutes);
        record.Status = status;
    }

    private static string StatusLabel(AttendanceStatus status) => status switch
    {
        AttendanceStatus.HalfDay => "Half Day",
        AttendanceStatus.OnLeave => "On Leave",
        _ => status.ToString(),
    };

    private async Task ApplyTimesAsync(AttendanceRecord record, DateTime? punchIn, DateTime? punchOut, int breakMinutes, Shift? shift, int editedByUserId, string editNote)
    {
        var settings = await CurrentSettingsAsync();
        record.PunchInAt = punchIn;
        record.PunchOutAt = punchOut;
        record.BreakMinutes = Math.Max(0, breakMinutes);
        record.IsManuallyEdited = true;
        record.EditedByUserId = editedByUserId;
        record.EditNote = editNote;

        if (punchIn is null)
        {
            record.WorkedMinutes = null;
            record.LateMinutes = 0;
            record.OvertimeMinutes = 0;
            record.Status = AttendanceStatus.Absent;
            return;
        }

        record.LateMinutes = ComputeLateMinutes(punchIn.Value, shift, settings.LateGraceMinutes);

        if (punchOut is null)
        {
            record.WorkedMinutes = null;
            record.OvertimeMinutes = 0;
            record.Status = record.LateMinutes > 0 ? AttendanceStatus.Late : AttendanceStatus.Present;
            return;
        }

        var totalMinutes = (int)Math.Max(0, (punchOut.Value - punchIn.Value).TotalMinutes - record.BreakMinutes);
        record.WorkedMinutes = totalMinutes;
        var scheduledEnd = shift?.EndsAt ?? punchIn.Value.AddHours(settings.StandardShiftHours);
        record.OvertimeMinutes = (int)Math.Max(0, (punchOut.Value - scheduledEnd).TotalMinutes);
        record.Status = totalMinutes < settings.HalfDayThresholdHours * 60
            ? AttendanceStatus.HalfDay
            : record.LateMinutes > 0 ? AttendanceStatus.Late : AttendanceStatus.Present;
    }

    private static int ComputeLateMinutes(DateTime punchInAt, Shift? shift, int graceMinutes)
    {
        if (shift is null) return 0;
        var minutesLate = (int)(punchInAt - shift.StartsAt).TotalMinutes;
        return minutesLate > graceMinutes ? minutesLate : 0;
    }

    private async Task<Shift?> FindShiftForDay(int staffId, DateOnly date)
    {
        var dayStart = date.ToDateTime(TimeOnly.MinValue);
        var dayEnd = dayStart.AddDays(1);
        return await db.Shifts.Where(s => s.StaffId == staffId && s.StartsAt < dayEnd && s.EndsAt > dayStart)
            .OrderBy(s => s.StartsAt).FirstOrDefaultAsync();
    }

    /// <summary>Today's date on the cafe's clock (IST — see IstClock), which is what a
    /// punch's LocalDate is validated against.</summary>
    private static DateOnly TodayIst() => DateOnly.FromDateTime(IstClock.NowIst);

    /// <summary>Self-service punches may only target the current shift: today, or
    /// yesterday for an overnight session crossing midnight (see FindOpenRecordAsync).
    /// Anything older is a correction — that's the Owner/Manager-gated manual/correct
    /// endpoints' job, not a staff member's own button.</summary>
    private static void EnsureTodayOrYesterday(DateOnly localDate)
    {
        var today = TodayIst();
        if (localDate != today && localDate != today.AddDays(-1))
            throw new ApiValidationException("Punches can only be recorded for the current shift.");
    }

    /// <summary>The attendance record the caller's CURRENT session lives on: the given
    /// date's record if it's open (punched in, not yet out), otherwise yesterday's if
    /// THAT one is still open — a night shift that crossed midnight punches out at 1 AM
    /// against yesterday's record, not today's empty date. Falls back to the given
    /// date's record (possibly null/closed) so callers' error messages still apply.</summary>
    private async Task<AttendanceRecord?> FindOpenRecordAsync(int staffId, DateOnly localDate)
    {
        var record = await db.AttendanceRecords.FirstOrDefaultAsync(a => a.StaffId == staffId && a.Date == localDate);
        if (record?.PunchInAt is not null && record.PunchOutAt is null) return record;

        var previous = await db.AttendanceRecords.FirstOrDefaultAsync(a => a.StaffId == staffId && a.Date == localDate.AddDays(-1));
        if (previous?.PunchInAt is not null && previous.PunchOutAt is null) return previous;

        return record;
    }

    /// <summary>Latest log of this session — everything since its punch-in. Replaces the
    /// old calendar-day window, which treated the IST date as a UTC range (logs between
    /// 00:00–05:30 IST fell outside it) and broke for sessions crossing midnight; the
    /// punch-in timestamp bounds the session exactly regardless of either.</summary>
    private async Task<AttendanceLog?> LastLogSinceAsync(int staffId, DateTime since) =>
        await db.AttendanceLogs.Where(l => l.StaffId == staffId && l.Timestamp >= since)
            .OrderByDescending(l => l.Timestamp).FirstOrDefaultAsync();

    /// <summary>If the staff member forgot to end a break before punching out, close it
    /// automatically at punch-out time rather than blocking the punch-out entirely.</summary>
    private async Task AutoCloseOpenBreakAsync(int staffId, AttendanceRecord record, DateTime punchOutAt)
    {
        var lastLog = await LastLogSinceAsync(staffId, record.PunchInAt!.Value);
        if (lastLog?.Type != AttendanceLogType.BreakStart) return;

        record.BreakMinutes += (int)Math.Max(0, (punchOutAt - lastLog.Timestamp).TotalMinutes);
        db.AttendanceLogs.Add(new AttendanceLog
        {
            StaffId = staffId, Type = AttendanceLogType.BreakEnd, Timestamp = punchOutAt,
            Source = AttendanceLogSource.Staff, Note = "Auto-closed at punch-out.",
        });
    }

    private async Task<CafeSettings> CurrentSettingsAsync() =>
        await db.Settings.FirstOrDefaultAsync() ?? new CafeSettings();

    /// <summary>Location is mandatory on every punch — a missing coordinate always 400s.
    /// The cafe's own registered coordinates (Cafe Profile's "Use Current Location") are
    /// mandatory too: without them the geofence can't mean anything, so rather than
    /// silently accepting punches from anywhere, punching is blocked with a message
    /// telling the staff member to get the Owner to set the cafe's location first.</summary>
    private static Task EnsureWithinGeofenceAsync(PunchRequest req, CafeSettings settings)
    {
        if (req.Latitude is null || req.Longitude is null)
            throw new ApiValidationException("Location is required to punch in/out — please enable location access.");

        if (settings.Latitude is not decimal cafeLat || settings.Longitude is not decimal cafeLon)
            throw new ApiValidationException(
                "The cafe's location isn't set yet, so attendance can't be verified. Ask the owner to set it first in Settings → Cafe Profile → 'Use Current Location'.");

        var distance = GeoDistance.Meters(req.Latitude.Value, req.Longitude.Value, cafeLat, cafeLon);
        if (distance > GeofenceRadiusMeters)
            throw new ApiValidationException($"You must be within {GeofenceRadiusMeters:0}m of the cafe to punch in/out.");

        return Task.CompletedTask;
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
