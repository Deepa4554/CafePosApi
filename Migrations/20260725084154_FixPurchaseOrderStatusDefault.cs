using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CafePOS.Api.Migrations
{
    /// <inheritdoc />
    public partial class FixPurchaseOrderStatusDefault : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Change the Status column default from 'Received' to 'Ordered' so new orders
            // start in pending state and must be explicitly received before stock updates
            migrationBuilder.Sql(@"ALTER TABLE ""PurchaseOrders"" ALTER COLUMN ""Status"" SET DEFAULT 'Ordered';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"ALTER TABLE ""PurchaseOrders"" ALTER COLUMN ""Status"" SET DEFAULT 'Received';");
        }
    }
}
