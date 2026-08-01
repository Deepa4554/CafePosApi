using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CafePOS.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderHotPathIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Orders_TenantId_BranchId_CreatedAt",
                table: "Orders",
                columns: new[] { "TenantId", "BranchId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Orders_TenantId_CreatedAt",
                table: "Orders",
                columns: new[] { "TenantId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Orders_TenantId_Paid_Status",
                table: "Orders",
                columns: new[] { "TenantId", "Paid", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Orders_TenantId_TableCode",
                table: "Orders",
                columns: new[] { "TenantId", "TableCode" });

            migrationBuilder.CreateIndex(
                name: "IX_OrderItems_TenantId_MenuItemId",
                table: "OrderItems",
                columns: new[] { "TenantId", "MenuItemId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Orders_TenantId_BranchId_CreatedAt",
                table: "Orders");

            migrationBuilder.DropIndex(
                name: "IX_Orders_TenantId_CreatedAt",
                table: "Orders");

            migrationBuilder.DropIndex(
                name: "IX_Orders_TenantId_Paid_Status",
                table: "Orders");

            migrationBuilder.DropIndex(
                name: "IX_Orders_TenantId_TableCode",
                table: "Orders");

            migrationBuilder.DropIndex(
                name: "IX_OrderItems_TenantId_MenuItemId",
                table: "OrderItems");
        }
    }
}
