using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace CafePOS.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddInventoryBatches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "ExpiryDate",
                table: "PurchaseItems",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "InventoryBatchId",
                table: "InventoryTransactions",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "InventoryBatches",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    InventoryItemId = table.Column<int>(type: "integer", nullable: false),
                    Quantity = table.Column<double>(type: "double precision", nullable: false),
                    UnitCost = table.Column<decimal>(type: "numeric", nullable: false),
                    ExpiryDate = table.Column<DateOnly>(type: "date", nullable: true),
                    ReceivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SourceReferenceId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryBatches", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransactions_InventoryBatchId",
                table: "InventoryTransactions",
                column: "InventoryBatchId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryBatches_InventoryItemId_ExpiryDate_ReceivedAt",
                table: "InventoryBatches",
                columns: new[] { "InventoryItemId", "ExpiryDate", "ReceivedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryBatches_TenantId",
                table: "InventoryBatches",
                column: "TenantId");

            migrationBuilder.AddForeignKey(
                name: "FK_InventoryTransactions_InventoryBatches_InventoryBatchId",
                table: "InventoryTransactions",
                column: "InventoryBatchId",
                principalTable: "InventoryBatches",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_InventoryTransactions_InventoryBatches_InventoryBatchId",
                table: "InventoryTransactions");

            migrationBuilder.DropTable(
                name: "InventoryBatches");

            migrationBuilder.DropIndex(
                name: "IX_InventoryTransactions_InventoryBatchId",
                table: "InventoryTransactions");

            migrationBuilder.DropColumn(
                name: "ExpiryDate",
                table: "PurchaseItems");

            migrationBuilder.DropColumn(
                name: "InventoryBatchId",
                table: "InventoryTransactions");
        }
    }
}
