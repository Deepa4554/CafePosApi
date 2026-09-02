using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CafePOS.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddTaxByPaymentModeAndReviewQr : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "GoogleReviewUrl",
                table: "Settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "TaxByPaymentModeEnabled",
                table: "Settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "TaxablePaymentModes",
                table: "Settings",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "TaxSuppressed",
                table: "Orders",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GoogleReviewUrl",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "TaxByPaymentModeEnabled",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "TaxablePaymentModes",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "TaxSuppressed",
                table: "Orders");
        }
    }
}
