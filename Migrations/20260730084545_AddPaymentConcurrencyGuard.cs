using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CafePOS.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentConcurrencyGuard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PaymentVersion",
                table: "Orders",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "LedgerIndex",
                table: "OrderPayments",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Every existing row lands on the default 0, so any bill already settled with a
            // split (two or more tenders) would break the unique index below the moment it's
            // created. Number each order's existing tenders in the order they were taken so
            // the historical ledger reads the same way a new one will, and so
            // OrdersController.Pay's `LedgerIndex = order.Payments.Count` continues the
            // sequence correctly for a later top-up on an old order.
            migrationBuilder.Sql("""
                UPDATE "OrderPayments" p
                SET "LedgerIndex" = s.rn
                FROM (
                    SELECT "Id",
                           ROW_NUMBER() OVER (PARTITION BY "TenantId", "OrderId"
                                              ORDER BY "CreatedAt", "Id") - 1 AS rn
                    FROM "OrderPayments"
                ) s
                WHERE p."Id" = s."Id";
                """);

            migrationBuilder.CreateIndex(
                name: "IX_OrderPayments_TenantId_OrderId_LedgerIndex",
                table: "OrderPayments",
                columns: new[] { "TenantId", "OrderId", "LedgerIndex" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OrderPayments_TenantId_OrderId_LedgerIndex",
                table: "OrderPayments");

            migrationBuilder.DropColumn(
                name: "PaymentVersion",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "LedgerIndex",
                table: "OrderPayments");
        }
    }
}
