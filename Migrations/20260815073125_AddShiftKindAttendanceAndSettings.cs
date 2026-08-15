using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CafePOS.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddShiftKindAttendanceAndSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AttendanceRecords_TenantId_StaffId_Date",
                table: "AttendanceRecords");

            // EF's scaffolder doesn't read C# property initializers (`= true`) as SQL
            // defaults — spelled out explicitly here so every existing tenant's row gets
            // all 4 shifts enabled (today's implicit behavior) instead of all-off.
            migrationBuilder.AddColumn<bool>(
                name: "EveningShiftEnabled",
                table: "Settings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "GeneralShiftEnabled",
                table: "Settings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "MorningShiftEnabled",
                table: "Settings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "NightShiftEnabled",
                table: "Settings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            // Same reasoning: every existing AttendanceRecord row predates ShiftKind and
            // represents the pre-multi-shift "one row per day" behavior, so it backfills
            // as General — one of the 4 real options, not an empty/invalid sentinel.
            migrationBuilder.AddColumn<string>(
                name: "ShiftKind",
                table: "AttendanceRecords",
                type: "text",
                nullable: false,
                defaultValue: "General");

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceRecords_TenantId_StaffId_Date_ShiftKind",
                table: "AttendanceRecords",
                columns: new[] { "TenantId", "StaffId", "Date", "ShiftKind" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AttendanceRecords_TenantId_StaffId_Date_ShiftKind",
                table: "AttendanceRecords");

            migrationBuilder.DropColumn(
                name: "EveningShiftEnabled",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "GeneralShiftEnabled",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "MorningShiftEnabled",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "NightShiftEnabled",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "ShiftKind",
                table: "AttendanceRecords");

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceRecords_TenantId_StaffId_Date",
                table: "AttendanceRecords",
                columns: new[] { "TenantId", "StaffId", "Date" },
                unique: true);
        }
    }
}
