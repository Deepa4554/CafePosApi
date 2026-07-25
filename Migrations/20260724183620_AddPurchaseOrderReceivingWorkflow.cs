using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CafePOS.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddPurchaseOrderReceivingWorkflow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ReceivedAt",
                table: "PurchaseOrders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReceivedByName",
                table: "PurchaseOrders",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReceivedByUserId",
                table: "PurchaseOrders",
                type: "integer",
                nullable: true);

            // Existing rows predate this workflow and already had their stock applied
            // immediately on creation (the old Create behavior) — semantically that's
            // "Received", not a still-pending "Ordered".
            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "PurchaseOrders",
                type: "text",
                nullable: false,
                defaultValue: "Ordered");

            migrationBuilder.AlterColumn<decimal>(
                name: "UnitCost",
                table: "PurchaseItems",
                type: "numeric",
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "numeric");

            migrationBuilder.AddColumn<double>(
                name: "ReceivedQuantity",
                table: "PurchaseItems",
                type: "double precision",
                nullable: true);

            // Backfill: legacy rows already had their stock applied at the recorded
            // Quantity/CreatedAt/CreatedBy, so mark them as Received and populate the
            // Received* fields to match when they were created (before workflow existed).
            migrationBuilder.Sql(@"UPDATE ""PurchaseItems"" SET ""ReceivedQuantity"" = ""Quantity"" WHERE ""ReceivedQuantity"" IS NULL;");
            migrationBuilder.Sql(@"UPDATE ""PurchaseOrders"" SET ""Status"" = 'Received', ""ReceivedAt"" = ""CreatedAt"", ""ReceivedByUserId"" = ""CreatedByUserId"", ""ReceivedByName"" = ""CreatedByName"";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReceivedAt",
                table: "PurchaseOrders");

            migrationBuilder.DropColumn(
                name: "ReceivedByName",
                table: "PurchaseOrders");

            migrationBuilder.DropColumn(
                name: "ReceivedByUserId",
                table: "PurchaseOrders");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "PurchaseOrders");

            migrationBuilder.DropColumn(
                name: "ReceivedQuantity",
                table: "PurchaseItems");

            migrationBuilder.AlterColumn<decimal>(
                name: "UnitCost",
                table: "PurchaseItems",
                type: "numeric",
                nullable: false,
                defaultValue: 0m,
                oldClrType: typeof(decimal),
                oldType: "numeric",
                oldNullable: true);
        }
    }
}
