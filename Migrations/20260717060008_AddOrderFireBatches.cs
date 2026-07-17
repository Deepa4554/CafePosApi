using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace CafePOS.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderFireBatches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OrderFireBatches",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    OrderId = table.Column<int>(type: "integer", nullable: false),
                    BatchNumber = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    FiredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderFireBatches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OrderFireBatches_Orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "Orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OrderFireBatches_OrderId_BatchNumber",
                table: "OrderFireBatches",
                columns: new[] { "OrderId", "BatchNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OrderFireBatches_TenantId",
                table: "OrderFireBatches",
                column: "TenantId");

            // Backfill: every in-flight order (not yet paid, fired at least once) gets exactly
            // one fire-batch row for its current batch, carrying over its existing Status —
            // the old single-status model never tracked more granularity than this, so this is
            // a fully faithful backfill with no data loss. Paid orders never touch
            // OrderFireBatches again, so they're intentionally skipped.
            migrationBuilder.Sql(
                """
                INSERT INTO "OrderFireBatches" ("TenantId", "OrderId", "BatchNumber", "Status", "FiredAt")
                SELECT "TenantId", "Id", "CurrentFireBatch", "Status", "CreatedAt"
                FROM "Orders"
                WHERE "CurrentFireBatch" > 0 AND "Paid" = false;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OrderFireBatches");
        }
    }
}
