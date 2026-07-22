using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CafePOS.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddNotificationPreferenceToggles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // defaultValue: true on all three — these notifications already fire
            // unconditionally today (see OrderBuildingService), so existing tenants must
            // land with them still on; EF's own C# property initializers (`= true`) only
            // apply to newly-constructed objects, never to this ALTER TABLE's default for
            // pre-existing rows, so it has to be spelled out here explicitly.
            migrationBuilder.AddColumn<bool>(
                name: "OrderPendingConfirmationAlertsEnabled",
                table: "Settings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "OrderPlacedAlertsEnabled",
                table: "Settings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "OrderReadyAlertsEnabled",
                table: "Settings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "LowStockNotified",
                table: "InventoryItems",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OrderPendingConfirmationAlertsEnabled",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "OrderPlacedAlertsEnabled",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "OrderReadyAlertsEnabled",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "LowStockNotified",
                table: "InventoryItems");
        }
    }
}
