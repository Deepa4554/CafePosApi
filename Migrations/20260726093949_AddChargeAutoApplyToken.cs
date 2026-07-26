using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CafePOS.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddChargeAutoApplyToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // IF NOT EXISTS — see AddChargeAutoApplySettings for why: keeps this migration
            // safe to re-run against a shared dev database that may already have the column.
            migrationBuilder.Sql(@"
                ALTER TABLE ""Settings"" ADD COLUMN IF NOT EXISTS ""DeliveryChargeAutoApplyToken"" boolean NOT NULL DEFAULT false;
                ALTER TABLE ""Settings"" ADD COLUMN IF NOT EXISTS ""PackingChargeAutoApplyToken"" boolean NOT NULL DEFAULT false;
                ALTER TABLE ""Settings"" ADD COLUMN IF NOT EXISTS ""ServiceChargeAutoApplyToken"" boolean NOT NULL DEFAULT false;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeliveryChargeAutoApplyToken",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "PackingChargeAutoApplyToken",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "ServiceChargeAutoApplyToken",
                table: "Settings");
        }
    }
}
