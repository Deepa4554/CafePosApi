using CafePOS.Api.Domain;

namespace CafePOS.Api.Contracts;

public record AttendanceLogDto(int Id, string Type, DateTime Timestamp, decimal? Latitude, decimal? Longitude, string Source, string? Note)
{
    public static AttendanceLogDto From(AttendanceLog l) => new(
        l.Id, l.Type.ToString().ToUpperInvariant(), l.Timestamp, l.Latitude, l.Longitude, l.Source.ToString().ToUpperInvariant(), l.Note);
}

public record AttendanceRecordDto(
    int Id, int StaffId, string StaffName, DateOnly Date, int? ShiftId, DateTime? PunchInAt, DateTime? PunchOutAt,
    int BreakMinutes, int? WorkedMinutes, string Status, int LateMinutes, int OvertimeMinutes, bool IsManuallyEdited, string? EditNote)
{
    public static AttendanceRecordDto From(AttendanceRecord r, string staffName) => new(
        r.Id, r.StaffId, staffName, r.Date, r.ShiftId, r.PunchInAt, r.PunchOutAt,
        r.BreakMinutes, r.WorkedMinutes, r.Status.ToString().ToUpperInvariant(), r.LateMinutes, r.OvertimeMinutes, r.IsManuallyEdited, r.EditNote);
}

/// <summary>LocalDate is supplied by the client (resolved to local/IST, same pattern
/// as StaffController.Performance's period params) rather than guessed server-side
/// from UTC "now" — avoids a punch near midnight landing on the wrong day. OccurredAt
/// defaults to server UtcNow when omitted. Latitude/Longitude stay nullable on the DTO
/// (break-start/break-end don't require them), but AttendanceController.
/// EnsureWithinGeofenceAsync rejects a null coordinate on punch-in/punch-out — see
/// there for why.</summary>
public record PunchRequest(DateOnly LocalDate, DateTime? OccurredAt, decimal? Latitude, decimal? Longitude);

public record ManualAttendanceRequest(int StaffId, DateOnly Date, DateTime? PunchInAt, DateTime? PunchOutAt, int BreakMinutes, string EditNote);

public record CorrectAttendanceRequest(DateTime? PunchInAt, DateTime? PunchOutAt, int? BreakMinutes, AttendanceStatus? Status, string EditNote);
