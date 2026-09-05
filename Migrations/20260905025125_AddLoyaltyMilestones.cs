using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace CafePOS.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddLoyaltyMilestones : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "MilestoneDiscountAmount",
                table: "Orders",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "MilestonePreviousClaimedThreshold",
                table: "Orders",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MilestoneThresholdApplied",
                table: "Orders",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MilestoneClaimedThreshold",
                table: "Customers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "LoyaltyMilestones",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    ThresholdPoints = table.Column<int>(type: "integer", nullable: false),
                    DiscountPct = table.Column<decimal>(type: "numeric", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoyaltyMilestones", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LoyaltyMilestones_TenantId",
                table: "LoyaltyMilestones",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LoyaltyMilestones");

            migrationBuilder.DropColumn(
                name: "MilestoneDiscountAmount",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "MilestonePreviousClaimedThreshold",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "MilestoneThresholdApplied",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "MilestoneClaimedThreshold",
                table: "Customers");
        }
    }
}
