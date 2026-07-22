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
public class AttendanceController(CafePosDbContext db, IAuditService audit) : ControllerBase
{
    /// <summary>Punch in/out must happen within this radius of the cafe's registered
    /// location — loose enough to absorb ordinary phone-GPS drift, tight enough to stop
    /// a punch from home. Skipped entirely (not just widened) when the cafe hasn't
    /// registered a location yet — see EnsureWithinGeofenceAsync.</summary>
    private const double GeofenceRadiusMeters = 500;

    // ---------- Self-service punch state machine ----------

    [Authorize(Policy = Policies.RequirePlus)]
    [HttpPost("punch-in")]
    public async Task<ActionResult<AttendanceRecordDto>> PunchIn(PunchRequest req)
    {
        var staff = await CurrentStaffAsync();
        var existing = await db.AttendanceRecords.FirstOrDefaultAsync(a => a.StaffId == staff.Id && a.Date == req.LocalDate);
        if (existing?.PunchInAt is not null) throw new ApiConflictException("Already punched in for this day.");

        var occurredAt = req.OccurredAt ?? DateTime.UtcNow;
        var shift = await FindShiftForDay(staff.Id, req.LocalDate);
        var settings = await CurrentSettingsAsync();
        await EnsureWithinGeofenceAsync(req, settings);

        var record = existing ?? new AttendanceRecord { StaffId = staff.Id, Date = req.LocalDate };
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
        var record = await db.AttendanceRecords.FirstOrDefaultAsync(a => a.StaffId == staff.Id && a.Date == req.LocalDate);
        if (record?.PunchInAt is null) throw new ApiConflictException("Not punched in for this day.");
        if (record.PunchOutAt is not null) throw new ApiConflictException("Already punched out for this day.");

        var occurredAt = req.OccurredAt ?? DateTime.UtcNow;
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
        });

        await db.SaveChangesAsync();
        return AttendanceRecordDto.From(record, staff.Name);
    }

    [Authorize(Policy = Policies.RequirePlus)]
    [HttpPost("break-start")]
    public async Task<IActionResult> BreakStart(PunchRequest req)
    {
        var staff = await CurrentStaffAsync();
        var record = await db.AttendanceRecords.FirstOrDefaultAsync(a => a.StaffId == staff.Id && a.Date == req.LocalDate);
        if (record?.PunchInAt is null || record.PunchOutAt is not null) throw new ApiConflictException("Not currently punched in.");

        var lastLog = await LastLogForDayAsync(staff.Id, req.LocalDate);
        if (lastLog?.Type == AttendanceLogType.BreakStart) throw new ApiConflictException("A break is already in progress.");

        db.AttendanceLogs.Add(new AttendanceLog
        {
            StaffId = staff.Id, Type = AttendanceLogType.BreakStart, Timestamp = req.OccurredAt ?? DateTime.UtcNow,
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
        var record = await db.AttendanceRecords.FirstOrDefaultAsync(a => a.StaffId == staff.Id && a.Date == req.LocalDate);
        if (record?.PunchInAt is null || record.PunchOutAt is not null) throw new ApiConflictException("Not currently punched in.");

        var lastLog = await LastLogForDayAsync(staff.Id, req.LocalDate);
        if (lastLog?.Type != AttendanceLogType.BreakStart) throw new ApiConflictException("No break is currently in progress.");

        var occurredAt = req.OccurredAt ?? DateTime.UtcNow;
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
    public async Task<IEnumerable<AttendanceRecordDto>> List([FromQuery] int? staffId, [FromQuery] DateOnly? date, [FromQuery] DateOnly? periodStart, [FromQuery] DateOnly? periodEnd)
    {
        var query = db.AttendanceRecords.AsQueryable();
        if (staffId is int sid) query = query.Where(a => a.StaffId == sid);
        if (date is DateOnly d) query = query.Where(a => a.Date == d);
        if (periodStart is DateOnly ps) query = query.Where(a => a.Date >= ps);
        if (periodEnd is DateOnly pe) query = query.Where(a => a.Date <= pe);
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

        var record = await db.AttendanceRecords.FirstOrDefaultAsync(a => a.StaffId == req.StaffId && a.Date == req.Date)
            ?? new AttendanceRecord { StaffId = req.StaffId, Date = req.Date };
        var isNew = record.Id == 0;

        var shift = record.ShiftId is int existingShiftId ? await db.Shifts.FindAsync(existingShiftId) : await FindShiftForDay(req.StaffId, req.Date);
        record.ShiftId = shift?.Id;

        var actor = await CurrentUserAsync();
        await ApplyTimesAsync(record, req.PunchInAt, req.PunchOutAt, req.BreakMinutes, shift, actor.Id, req.EditNote);

        if (isNew) db.AttendanceRecords.Add(record);
        await db.SaveChangesAsync();

        await audit.LogAsync(AuditAction.Update, AuditResource.Attendance, record.Id.ToString(),
            $"{actor.Name} manually recorded attendance for {staff.Name} on {req.Date}.", AuditSeverity.Medium, actor.Id, actor.Name);
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

        await db.SaveChangesAsync();
        await audit.LogAsync(AuditAction.Update, AuditResource.Attendance, record.Id.ToString(),
            $"{actor.Name} corrected {staff.Name}'s attendance for {record.Date}: {req.EditNote}", AuditSeverity.Medium, actor.Id, actor.Name);
        return AttendanceRecordDto.From(record, staff.Name);
    }

    // ---------- Shared derivation logic ----------

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

    private async Task<AttendanceLog?> LastLogForDayAsync(int staffId, DateOnly date)
    {
        var dayStart = date.ToDateTime(TimeOnly.MinValue);
        var dayEnd = dayStart.AddDays(1);
        return await db.AttendanceLogs.Where(l => l.StaffId == staffId && l.Timestamp >= dayStart && l.Timestamp < dayEnd)
            .OrderByDescending(l => l.Timestamp).FirstOrDefaultAsync();
    }

    /// <summary>If the staff member forgot to end a break before punching out, close it
    /// automatically at punch-out time rather than blocking the punch-out entirely.</summary>
    private async Task AutoCloseOpenBreakAsync(int staffId, AttendanceRecord record, DateTime punchOutAt)
    {
        var lastLog = await LastLogForDayAsync(staffId, record.Date);
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
    /// The distance check itself only runs once the cafe has actually registered its own
    /// coordinates (Cafe Profile's "Use Current Location"); until then there's nothing to
    /// compare against, so punches are accepted (and still geotagged) without blocking
    /// day-1 use before an Owner has set the cafe's location.</summary>
    private static Task EnsureWithinGeofenceAsync(PunchRequest req, CafeSettings settings)
    {
        if (req.Latitude is null || req.Longitude is null)
            throw new ApiValidationException("Location is required to punch in/out — please enable location access.");

        if (settings.Latitude is decimal cafeLat && settings.Longitude is decimal cafeLon)
        {
            var distance = GeoDistance.Meters(req.Latitude.Value, req.Longitude.Value, cafeLat, cafeLon);
            if (distance > GeofenceRadiusMeters)
                throw new ApiValidationException($"You must be within {GeofenceRadiusMeters:0}m of the cafe to punch in/out.");
        }

        return Task.CompletedTask;
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
