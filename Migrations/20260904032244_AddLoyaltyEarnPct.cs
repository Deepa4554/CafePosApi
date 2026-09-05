using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CafePOS.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddLoyaltyEarnPct : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 100, not the 0 EF scaffolds from an unset column: every existing cafe has been
            // earning a point per ₹1 spent, and 0 would silently switch loyalty off for all of
            // them on deploy. This backfills the rate they already had — see
            // CafeSettings.LoyaltyEarnPct.
            migrationBuilder.AddColumn<decimal>(
                name: "LoyaltyEarnPct",
                table: "Settings",
                type: "numeric",
                nullable: false,
                defaultValue: 100m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LoyaltyEarnPct",
                table: "Settings");
        }
    }
}
