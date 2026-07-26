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
            // IF NOT EXISTS (rather than plain AddColumn) because these columns also
            // already landed directly on the shared dev database from an earlier run of
            // this migration that crashed before EF could record it in the history table
            // — idempotent here so a fresh database and this already-patched one both end
            // up correct.
            migrationBuilder.Sql(@"
                ALTER TABLE ""Settings"" ADD COLUMN IF NOT EXISTS ""DeliveryChargeAutoApplyDelivery"" boolean NOT NULL DEFAULT true;
                ALTER TABLE ""Settings"" ADD COLUMN IF NOT EXISTS ""DeliveryChargeAutoApplyDineIn"" boolean NOT NULL DEFAULT false;
                ALTER TABLE ""Settings"" ADD COLUMN IF NOT EXISTS ""DeliveryChargeAutoApplyTakeaway"" boolean NOT NULL DEFAULT false;
                ALTER TABLE ""Settings"" ADD COLUMN IF NOT EXISTS ""DeliveryChargeDefaultAmount"" numeric;
                ALTER TABLE ""Settings"" ADD COLUMN IF NOT EXISTS ""PackingChargeAutoApplyDelivery"" boolean NOT NULL DEFAULT true;
                ALTER TABLE ""Settings"" ADD COLUMN IF NOT EXISTS ""PackingChargeAutoApplyDineIn"" boolean NOT NULL DEFAULT false;
                ALTER TABLE ""Settings"" ADD COLUMN IF NOT EXISTS ""PackingChargeAutoApplyTakeaway"" boolean NOT NULL DEFAULT true;
                ALTER TABLE ""Settings"" ADD COLUMN IF NOT EXISTS ""PackingChargeDefaultAmount"" numeric;
                ALTER TABLE ""Settings"" ADD COLUMN IF NOT EXISTS ""ServiceChargeAutoApplyDelivery"" boolean NOT NULL DEFAULT false;
                ALTER TABLE ""Settings"" ADD COLUMN IF NOT EXISTS ""ServiceChargeAutoApplyDineIn"" boolean NOT NULL DEFAULT true;
                ALTER TABLE ""Settings"" ADD COLUMN IF NOT EXISTS ""ServiceChargeAutoApplyTakeaway"" boolean NOT NULL DEFAULT false;
                ALTER TABLE ""Settings"" ADD COLUMN IF NOT EXISTS ""ServiceChargeDefaultPct"" numeric;
            ");
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
