using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CafePOS.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderItemStageQty : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PreparingQty",
                table: "OrderItems",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ReadQty",
                table: "OrderItems",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ReadyQty",
                table: "OrderItems",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ServedQty",
                table: "OrderItems",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Backfill: each line's whole quantity starts at the stage its old single Status
            // pointed to (all units together — faithful to the pre-partial-quantity model).
            // New-status lines keep all counters 0 (all units New). "Read" didn't exist before.
            migrationBuilder.Sql(
                """
                UPDATE "OrderItems" SET "PreparingQty" = "Qty" WHERE "Status" = 'Preparing';
                UPDATE "OrderItems" SET "ReadyQty"     = "Qty" WHERE "Status" = 'Ready';
                UPDATE "OrderItems" SET "ServedQty"    = "Qty" WHERE "Status" = 'Served';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PreparingQty",
                table: "OrderItems");

            migrationBuilder.DropColumn(
                name: "ReadQty",
                table: "OrderItems");

            migrationBuilder.DropColumn(
                name: "ReadyQty",
                table: "OrderItems");

            migrationBuilder.DropColumn(
                name: "ServedQty",
                table: "OrderItems");
        }
    }
}
