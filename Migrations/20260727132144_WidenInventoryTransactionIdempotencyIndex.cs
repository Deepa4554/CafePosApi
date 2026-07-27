using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CafePOS.Api.Migrations
{
    /// <inheritdoc />
    public partial class WidenInventoryTransactionIdempotencyIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_InventoryTransactions_OrderItemId_InventoryItemId",
                table: "InventoryTransactions");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransactions_OrderItemId_InventoryItemId_Inventory~",
                table: "InventoryTransactions",
                columns: new[] { "OrderItemId", "InventoryItemId", "InventoryBatchId" },
                unique: true,
                filter: "\"Type\" = 'Sale' AND \"OrderItemId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_InventoryTransactions_OrderItemId_InventoryItemId_Inventory~",
                table: "InventoryTransactions");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransactions_OrderItemId_InventoryItemId",
                table: "InventoryTransactions",
                columns: new[] { "OrderItemId", "InventoryItemId" },
                unique: true,
                filter: "\"Type\" = 'Sale' AND \"OrderItemId\" IS NOT NULL");
        }
    }
}
