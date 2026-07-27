using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CafePOS.Api.Migrations
{
    /// <summary>
    /// Data repair, no schema change: guest QR orders created before the TenantId-stamping
    /// fix in OrderBuildingService left their child rows (OrderItems / OrderItemModifiers /
    /// OrderFireBatches) stamped with the DEFAULT tenant instead of the scanned cafe's —
    /// the ambient tenant query filter then hid those items from the cafe's own staff
    /// ("0 items" orders whose Confirm always failed). Re-align every child row to its
    /// parent Order's TenantId. Idempotent by construction (the WHERE clause matches
    /// nothing once repaired).
    /// </summary>
    public partial class FixGuestOrderChildTenantIds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE "OrderItems" oi SET "TenantId" = o."TenantId"
                FROM "Orders" o
                WHERE oi."OrderId" = o."Id" AND oi."TenantId" <> o."TenantId";
                """);
            migrationBuilder.Sql("""
                UPDATE "OrderItemModifiers" m SET "TenantId" = oi."TenantId"
                FROM "OrderItems" oi
                WHERE m."OrderItemId" = oi."Id" AND m."TenantId" <> oi."TenantId";
                """);
            migrationBuilder.Sql("""
                UPDATE "OrderFireBatches" b SET "TenantId" = o."TenantId"
                FROM "Orders" o
                WHERE b."OrderId" = o."Id" AND b."TenantId" <> o."TenantId";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Data repair — nothing sensible to restore; the old values were wrong.
        }
    }
}
