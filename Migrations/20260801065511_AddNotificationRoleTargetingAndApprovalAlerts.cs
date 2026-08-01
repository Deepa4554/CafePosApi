using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CafePOS.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddNotificationRoleTargetingAndApprovalAlerts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // defaultValue: true, same reasoning as AddNotificationPreferenceToggles — the C#
            // initializer (`= true`) only applies to newly-constructed CafeSettings, never to
            // this ALTER TABLE's backfill, and an existing tenant must not silently land with
            // its brand-new approval alerts already switched off.
            migrationBuilder.AddColumn<bool>(
                name: "ApprovalAlertsEnabled",
                table: "Settings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetRolesCsv",
                table: "Notifications",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ApprovalAlertsEnabled",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "TargetRolesCsv",
                table: "Notifications");
        }
    }
}
