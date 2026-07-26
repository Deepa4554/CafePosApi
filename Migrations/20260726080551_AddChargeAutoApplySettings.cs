using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CafePOS.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddChargeAutoApplySettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "DeliveryChargeAutoApplyDelivery",
                table: "Settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "DeliveryChargeAutoApplyDineIn",
                table: "Settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "DeliveryChargeAutoApplyTakeaway",
                table: "Settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "DeliveryChargeDefaultAmount",
                table: "Settings",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "PackingChargeAutoApplyDelivery",
                table: "Settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "PackingChargeAutoApplyDineIn",
                table: "Settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "PackingChargeAutoApplyTakeaway",
                table: "Settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "PackingChargeDefaultAmount",
                table: "Settings",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ServiceChargeAutoApplyDelivery",
                table: "Settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ServiceChargeAutoApplyDineIn",
                table: "Settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ServiceChargeAutoApplyTakeaway",
                table: "Settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "ServiceChargeDefaultPct",
                table: "Settings",
                type: "numeric",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeliveryChargeAutoApplyDelivery",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "DeliveryChargeAutoApplyDineIn",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "DeliveryChargeAutoApplyTakeaway",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "DeliveryChargeDefaultAmount",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "PackingChargeAutoApplyDelivery",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "PackingChargeAutoApplyDineIn",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "PackingChargeAutoApplyTakeaway",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "PackingChargeDefaultAmount",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "ServiceChargeAutoApplyDelivery",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "ServiceChargeAutoApplyDineIn",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "ServiceChargeAutoApplyTakeaway",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "ServiceChargeDefaultPct",
                table: "Settings");
        }
    }
}
