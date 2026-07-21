using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CafePOS.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddStaffOrderConfirmation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // defaultValue: true (not the AddColumn-generated false) so cafes already live
            // before this migration get the safer default too, matching CafeSettings'
            // C# property initializer — not just cafes created after this deploys.
            migrationBuilder.AddColumn<bool>(
                name: "RequireStaffOrderConfirmation",
                table: "Settings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "PendingStaffConfirmation",
                table: "Orders",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RequireStaffOrderConfirmation",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "PendingStaffConfirmation",
                table: "Orders");
        }
    }
}
