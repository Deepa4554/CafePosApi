using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace CafePOS.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddTiffin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TiffinSubscribers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    CustomerId = table.Column<int>(type: "integer", nullable: false),
                    Type = table.Column<string>(type: "text", nullable: false),
                    PlanName = table.Column<string>(type: "text", nullable: false),
                    MealType = table.Column<string>(type: "text", nullable: false),
                    Rate = table.Column<decimal>(type: "numeric", nullable: false),
                    DefaultQty = table.Column<int>(type: "integer", nullable: false),
                    DeliveryAddress = table.Column<string>(type: "text", nullable: true),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    Notes = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TiffinSubscribers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TiffinSubscribers_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TiffinInvoices",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    SubscriberId = table.Column<int>(type: "integer", nullable: false),
                    CustomerId = table.Column<int>(type: "integer", nullable: false),
                    PeriodStart = table.Column<DateOnly>(type: "date", nullable: false),
                    PeriodEnd = table.Column<DateOnly>(type: "date", nullable: false),
                    DeliveredDays = table.Column<int>(type: "integer", nullable: false),
                    TotalQty = table.Column<int>(type: "integer", nullable: false),
                    Rate = table.Column<decimal>(type: "numeric", nullable: false),
                    TotalAmount = table.Column<decimal>(type: "numeric", nullable: false),
                    AmountPaid = table.Column<decimal>(type: "numeric", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Notes = table.Column<string>(type: "text", nullable: true),
                    GeneratedByUserId = table.Column<int>(type: "integer", nullable: true),
                    GeneratedByName = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TiffinInvoices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TiffinInvoices_TiffinSubscribers_SubscriberId",
                        column: x => x.SubscriberId,
                        principalTable: "TiffinSubscribers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TiffinMarks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    SubscriberId = table.Column<int>(type: "integer", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Qty = table.Column<int>(type: "integer", nullable: false),
                    RecordedByUserId = table.Column<int>(type: "integer", nullable: true),
                    RecordedByName = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TiffinMarks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TiffinMarks_TiffinSubscribers_SubscriberId",
                        column: x => x.SubscriberId,
                        principalTable: "TiffinSubscribers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TiffinPayments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    InvoiceId = table.Column<int>(type: "integer", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric", nullable: false),
                    Method = table.Column<string>(type: "text", nullable: false),
                    Note = table.Column<string>(type: "text", nullable: true),
                    RecordedByUserId = table.Column<int>(type: "integer", nullable: true),
                    RecordedByName = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TiffinPayments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TiffinPayments_TiffinInvoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalTable: "TiffinInvoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TiffinInvoices_SubscriberId",
                table: "TiffinInvoices",
                column: "SubscriberId");

            migrationBuilder.CreateIndex(
                name: "IX_TiffinInvoices_TenantId",
                table: "TiffinInvoices",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_TiffinInvoices_TenantId_SubscriberId_PeriodStart",
                table: "TiffinInvoices",
                columns: new[] { "TenantId", "SubscriberId", "PeriodStart" });

            migrationBuilder.CreateIndex(
                name: "IX_TiffinMarks_SubscriberId",
                table: "TiffinMarks",
                column: "SubscriberId");

            migrationBuilder.CreateIndex(
                name: "IX_TiffinMarks_TenantId",
                table: "TiffinMarks",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_TiffinMarks_TenantId_SubscriberId_Date",
                table: "TiffinMarks",
                columns: new[] { "TenantId", "SubscriberId", "Date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TiffinPayments_InvoiceId",
                table: "TiffinPayments",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_TiffinPayments_TenantId",
                table: "TiffinPayments",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_TiffinPayments_TenantId_InvoiceId",
                table: "TiffinPayments",
                columns: new[] { "TenantId", "InvoiceId" });

            migrationBuilder.CreateIndex(
                name: "IX_TiffinSubscribers_CustomerId",
                table: "TiffinSubscribers",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_TiffinSubscribers_TenantId",
                table: "TiffinSubscribers",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_TiffinSubscribers_TenantId_CustomerId",
                table: "TiffinSubscribers",
                columns: new[] { "TenantId", "CustomerId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TiffinMarks");

            migrationBuilder.DropTable(
                name: "TiffinPayments");

            migrationBuilder.DropTable(
                name: "TiffinInvoices");

            migrationBuilder.DropTable(
                name: "TiffinSubscribers");
        }
    }
}
