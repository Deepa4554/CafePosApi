using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CafePOS.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddHsnChargeTaxAndInputTax : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DefaultHsnCode",
                table: "Settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsCompositionScheme",
                table: "Settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "TaxChargesEnabled",
                table: "Settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "TaxRatePct",
                table: "PurchaseItems",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ChargesTaxAmount",
                table: "Orders",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "ChargesTaxRatePct",
                table: "Orders",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ChargesTaxableAmount",
                table: "Orders",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "HsnCode",
                table: "OrderItems",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HsnCode",
                table: "MenuItems",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TaxRatePct",
                table: "CafeExpenses",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VendorGstin",
                table: "CafeExpenses",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DefaultHsnCode",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "IsCompositionScheme",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "TaxChargesEnabled",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "TaxRatePct",
                table: "PurchaseItems");

            migrationBuilder.DropColumn(
                name: "ChargesTaxAmount",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "ChargesTaxRatePct",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "ChargesTaxableAmount",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "HsnCode",
                table: "OrderItems");

            migrationBuilder.DropColumn(
                name: "HsnCode",
                table: "MenuItems");

            migrationBuilder.DropColumn(
                name: "TaxRatePct",
                table: "CafeExpenses");

            migrationBuilder.DropColumn(
                name: "VendorGstin",
                table: "CafeExpenses");
        }
    }
}
