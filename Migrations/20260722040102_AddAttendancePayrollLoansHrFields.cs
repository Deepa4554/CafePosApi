using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace CafePOS.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddAttendancePayrollLoansHrFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Aadhaar",
                table: "Staff",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BankAccountNumber",
                table: "Staff",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BankIfsc",
                table: "Staff",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BankName",
                table: "Staff",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "BasicSalary",
                table: "Staff",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Department",
                table: "Staff",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Designation",
                table: "Staff",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Pan",
                table: "Staff",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SalaryType",
                table: "Staff",
                type: "text",
                nullable: false,
                defaultValue: "Monthly");

            migrationBuilder.AddColumn<int>(
                name: "HalfDayThresholdHours",
                table: "Settings",
                type: "integer",
                nullable: false,
                defaultValue: 4);

            migrationBuilder.AddColumn<int>(
                name: "LateGraceMinutes",
                table: "Settings",
                type: "integer",
                nullable: false,
                defaultValue: 10);

            migrationBuilder.AddColumn<int>(
                name: "StandardShiftHours",
                table: "Settings",
                type: "integer",
                nullable: false,
                defaultValue: 8);

            migrationBuilder.CreateTable(
                name: "AttendanceLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    StaffId = table.Column<int>(type: "integer", nullable: false),
                    Type = table.Column<string>(type: "text", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Latitude = table.Column<decimal>(type: "numeric", nullable: true),
                    Longitude = table.Column<decimal>(type: "numeric", nullable: true),
                    Source = table.Column<string>(type: "text", nullable: false),
                    Note = table.Column<string>(type: "text", nullable: true),
                    RecordedByUserId = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AttendanceLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AttendanceRecords",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    StaffId = table.Column<int>(type: "integer", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    ShiftId = table.Column<int>(type: "integer", nullable: true),
                    PunchInAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PunchOutAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    BreakMinutes = table.Column<int>(type: "integer", nullable: false),
                    WorkedMinutes = table.Column<int>(type: "integer", nullable: true),
                    Status = table.Column<string>(type: "text", nullable: false),
                    LateMinutes = table.Column<int>(type: "integer", nullable: false),
                    OvertimeMinutes = table.Column<int>(type: "integer", nullable: false),
                    IsManuallyEdited = table.Column<bool>(type: "boolean", nullable: false),
                    EditedByUserId = table.Column<int>(type: "integer", nullable: true),
                    EditNote = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AttendanceRecords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PayrollRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    PeriodStart = table.Column<DateOnly>(type: "date", nullable: false),
                    PeriodEnd = table.Column<DateOnly>(type: "date", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    GeneratedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    GeneratedByUserId = table.Column<int>(type: "integer", nullable: false),
                    GeneratedByName = table.Column<string>(type: "text", nullable: false),
                    LockedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LockedByUserId = table.Column<int>(type: "integer", nullable: true),
                    PaidAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PaidByUserId = table.Column<int>(type: "integer", nullable: true),
                    Notes = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PayrollRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StaffLoans",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    StaffId = table.Column<int>(type: "integer", nullable: false),
                    StaffName = table.Column<string>(type: "text", nullable: false),
                    Type = table.Column<string>(type: "text", nullable: false),
                    PrincipalAmount = table.Column<decimal>(type: "numeric", nullable: false),
                    MonthlyDeduction = table.Column<decimal>(type: "numeric", nullable: false),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    OutstandingBalance = table.Column<decimal>(type: "numeric", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: true),
                    ApprovedByUserId = table.Column<int>(type: "integer", nullable: false),
                    ApprovedByName = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ClosedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StaffLoans", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PayrollLines",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    PayrollRunId = table.Column<int>(type: "integer", nullable: false),
                    StaffId = table.Column<int>(type: "integer", nullable: false),
                    StaffName = table.Column<string>(type: "text", nullable: false),
                    SalaryType = table.Column<string>(type: "text", nullable: false),
                    BasicSalary = table.Column<decimal>(type: "numeric", nullable: false),
                    HourlyRate = table.Column<decimal>(type: "numeric", nullable: true),
                    PresentDays = table.Column<int>(type: "integer", nullable: false),
                    LateDays = table.Column<int>(type: "integer", nullable: false),
                    HalfDays = table.Column<int>(type: "integer", nullable: false),
                    AbsentDays = table.Column<int>(type: "integer", nullable: false),
                    PaidLeaveDays = table.Column<int>(type: "integer", nullable: false),
                    UnpaidLeaveDays = table.Column<int>(type: "integer", nullable: false),
                    OvertimeHours = table.Column<double>(type: "double precision", nullable: false),
                    OvertimePay = table.Column<decimal>(type: "numeric", nullable: false),
                    Allowances = table.Column<string>(type: "text", nullable: false),
                    AllowancesTotal = table.Column<decimal>(type: "numeric", nullable: false),
                    GrossEarnings = table.Column<decimal>(type: "numeric", nullable: false),
                    LeaveDeduction = table.Column<decimal>(type: "numeric", nullable: false),
                    LateDeduction = table.Column<decimal>(type: "numeric", nullable: false),
                    LoanDeduction = table.Column<decimal>(type: "numeric", nullable: false),
                    PfDeduction = table.Column<decimal>(type: "numeric", nullable: false),
                    EsicDeduction = table.Column<decimal>(type: "numeric", nullable: false),
                    ProfessionalTaxDeduction = table.Column<decimal>(type: "numeric", nullable: false),
                    TotalDeductions = table.Column<decimal>(type: "numeric", nullable: false),
                    NetSalary = table.Column<decimal>(type: "numeric", nullable: false),
                    BankAccountNumberSnapshot = table.Column<string>(type: "text", nullable: true),
                    BankIfscSnapshot = table.Column<string>(type: "text", nullable: true),
                    IsEdited = table.Column<bool>(type: "boolean", nullable: false),
                    EditedByUserId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PayrollLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PayrollLines_PayrollRuns_PayrollRunId",
                        column: x => x.PayrollRunId,
                        principalTable: "PayrollRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceLogs_TenantId",
                table: "AttendanceLogs",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceLogs_TenantId_StaffId_Timestamp",
                table: "AttendanceLogs",
                columns: new[] { "TenantId", "StaffId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceRecords_TenantId",
                table: "AttendanceRecords",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceRecords_TenantId_StaffId_Date",
                table: "AttendanceRecords",
                columns: new[] { "TenantId", "StaffId", "Date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PayrollLines_PayrollRunId_StaffId",
                table: "PayrollLines",
                columns: new[] { "PayrollRunId", "StaffId" });

            migrationBuilder.CreateIndex(
                name: "IX_PayrollLines_TenantId",
                table: "PayrollLines",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_PayrollRuns_TenantId",
                table: "PayrollRuns",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_PayrollRuns_TenantId_PeriodStart_PeriodEnd",
                table: "PayrollRuns",
                columns: new[] { "TenantId", "PeriodStart", "PeriodEnd" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StaffLoans_TenantId",
                table: "StaffLoans",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_StaffLoans_TenantId_StaffId_Status",
                table: "StaffLoans",
                columns: new[] { "TenantId", "StaffId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AttendanceLogs");

            migrationBuilder.DropTable(
                name: "AttendanceRecords");

            migrationBuilder.DropTable(
                name: "PayrollLines");

            migrationBuilder.DropTable(
                name: "StaffLoans");

            migrationBuilder.DropTable(
                name: "PayrollRuns");

            migrationBuilder.DropColumn(
                name: "Aadhaar",
                table: "Staff");

            migrationBuilder.DropColumn(
                name: "BankAccountNumber",
                table: "Staff");

            migrationBuilder.DropColumn(
                name: "BankIfsc",
                table: "Staff");

            migrationBuilder.DropColumn(
                name: "BankName",
                table: "Staff");

            migrationBuilder.DropColumn(
                name: "BasicSalary",
                table: "Staff");

            migrationBuilder.DropColumn(
                name: "Department",
                table: "Staff");

            migrationBuilder.DropColumn(
                name: "Designation",
                table: "Staff");

            migrationBuilder.DropColumn(
                name: "Pan",
                table: "Staff");

            migrationBuilder.DropColumn(
                name: "SalaryType",
                table: "Staff");

            migrationBuilder.DropColumn(
                name: "HalfDayThresholdHours",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "LateGraceMinutes",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "StandardShiftHours",
                table: "Settings");
        }
    }
}
