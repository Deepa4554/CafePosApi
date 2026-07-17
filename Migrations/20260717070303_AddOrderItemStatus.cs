using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CafePOS.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderItemStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "OrderItems",
                type: "text",
                nullable: false,
                defaultValue: "New");

            // Backfill: each already-fired item inherits its fire batch's current status —
            // the old batch-level model applied that one status to every item in the round,
            // so copying it down is fully faithful. Unfired items (FireBatch 0) keep the
            // "New" default set above.
            migrationBuilder.Sql(
                """
                UPDATE "OrderItems" i
                SET "Status" = b."Status"
                FROM "OrderFireBatches" b
                WHERE b."OrderId" = i."OrderId" AND b."BatchNumber" = i."FireBatch";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Status",
                table: "OrderItems");
        }
    }
}
