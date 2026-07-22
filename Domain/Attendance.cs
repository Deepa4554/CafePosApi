namespace CafePOS.Api.Domain;

// ---------- Attendance ----------

public enum AttendanceLogType { PunchIn, PunchOut, BreakStart, BreakEnd }
public enum AttendanceLogSource { Staff, ManagerCorrection }

/// <summary>Raw, append-only punch events — the audit trail behind AttendanceRecord,
/// same "ledger + derived summary" split as InventoryTransaction → InventoryItem.
/// CurrentStock.</summary>
public class AttendanceLog : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public int StaffId { get; set; }
    public AttendanceLogType Type { get; set; }
    public DateTime Timestamp { get; set; }
    /// <summary>Captured if the device supplies it, never enforced/geofenced — no
    /// biometric/GPS hardware integration exists, this is just a courtesy tag on the
    /// event for a future geofence feature to build on.</summary>
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
    public AttendanceLogSource Source { get; set; } = AttendanceLogSource.Staff;
    public string? Note { get; set; }
    public int? RecordedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public enum AttendanceStatus { Present, Late, HalfDay, Absent, OnLeave, Holiday }

/// <summary>
/// One row per staff per local business day. Derived fields (WorkedMinutes/Status/
/// LateMinutes/OvertimeMinutes) are (re)computed from the raw AttendanceLog rows for
/// that day at punch-out / break-end / manual-edit time — never recomputed silently
/// later, so a Payroll snapshot generated from this row can never drift under it (see
/// PayrollLine's doc comment). Absent is never stored here — it's computed on the fly
/// by diffing Shifts vs AttendanceRecords vs approved LeaveRequests, since this app has
/// no background job runner to write one in (same constraint LeaveRequest already
/// documents for itself).
/// </summary>
public class AttendanceRecord : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public int StaffId { get; set; }
    public DateOnly Date { get; set; }
    public int? ShiftId { get; set; }
    public DateTime? PunchInAt { get; set; }
    public DateTime? PunchOutAt { get; set; }
    public int BreakMinutes { get; set; }
    /// <summary>Null until punched out.</summary>
    public int? WorkedMinutes { get; set; }
    public AttendanceStatus Status { get; set; } = AttendanceStatus.Present;
    public int LateMinutes { get; set; }
    public int OvertimeMinutes { get; set; }
    public bool IsManuallyEdited { get; set; }
    public int? EditedByUserId { get; set; }
    public string? EditNote { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
