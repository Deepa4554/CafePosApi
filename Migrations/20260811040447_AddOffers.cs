using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace CafePOS.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddOffers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AppliedOfferTitle",
                table: "Orders",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "OfferDiscountAmount",
                table: "Orders",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "OfferDiscountAmount",
                table: "OrderItems",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "Offers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    Title = table.Column<string>(type: "text", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    Scope = table.Column<int>(type: "integer", nullable: false),
                    CategoryName = table.Column<string>(type: "text", nullable: true),
                    Value = table.Column<decimal>(type: "numeric", nullable: false),
                    MaxDiscountAmount = table.Column<decimal>(type: "numeric", nullable: false),
                    BuyQty = table.Column<int>(type: "integer", nullable: false),
                    GetQty = table.Column<int>(type: "integer", nullable: false),
                    MinOrderValue = table.Column<decimal>(type: "numeric", nullable: false),
                    MaxApplicationsPerBill = table.Column<int>(type: "integer", nullable: false),
                    Stackable = table.Column<bool>(type: "boolean", nullable: false),
                    StartsAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EndsAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DaysOfWeek = table.Column<string>(type: "text", nullable: true),
                    StartTime = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    EndTime = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    AutoApply = table.Column<bool>(type: "boolean", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Offers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OfferMenuItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    OfferId = table.Column<int>(type: "integer", nullable: false),
                    MenuItemId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OfferMenuItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OfferMenuItems_Offers_OfferId",
                        column: x => x.OfferId,
                        principalTable: "Offers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OfferMenuItems_OfferId",
                table: "OfferMenuItems",
                column: "OfferId");

            migrationBuilder.CreateIndex(
                name: "IX_OfferMenuItems_TenantId",
                table: "OfferMenuItems",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Offers_TenantId",
                table: "Offers",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OfferMenuItems");

            migrationBuilder.DropTable(
                name: "Offers");

            migrationBuilder.DropColumn(
                name: "AppliedOfferTitle",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "OfferDiscountAmount",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "OfferDiscountAmount",
                table: "OrderItems");
        }
    }
}
