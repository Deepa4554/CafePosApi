using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace CafePOS.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddMultiTenancy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Tables_Code",
                table: "Tables");

            migrationBuilder.DropIndex(
                name: "IX_GiftCards_Code",
                table: "GiftCards");

            migrationBuilder.DropIndex(
                name: "IX_Coupons_Code",
                table: "Coupons");

            migrationBuilder.AddColumn<int>(
                name: "TenantId",
                table: "Users",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TenantId",
                table: "Tasks",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "TenantId",
                table: "Tables",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "TenantId",
                table: "Subscriptions",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "TenantId",
                table: "Staff",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "TenantId",
                table: "Shifts",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "TenantId",
                table: "Settings",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "TenantId",
                table: "PerformanceReviews",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "TenantId",
                table: "Orders",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "TenantId",
                table: "OrderItems",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "TenantId",
                table: "Notifications",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "TenantId",
                table: "MenuItems",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "TenantId",
                table: "InventoryItems",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "TenantId",
                table: "Integrations",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "TenantId",
                table: "GiftCards",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "TenantId",
                table: "FavoriteItems",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "TenantId",
                table: "Customers",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "TenantId",
                table: "Coupons",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "TenantId",
                table: "Branches",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "TenantId",
                table: "AuditLog",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "TenantId",
                table: "Approvals",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateTable(
                name: "Tenants",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Slug = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tenants", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_TenantId",
                table: "Tasks",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Tables_TenantId",
                table: "Tables",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Tables_TenantId_Code",
                table: "Tables",
                columns: new[] { "TenantId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Subscriptions_TenantId",
                table: "Subscriptions",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Staff_TenantId",
                table: "Staff",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Shifts_TenantId",
                table: "Shifts",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Settings_TenantId",
                table: "Settings",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_PerformanceReviews_TenantId",
                table: "PerformanceReviews",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_TenantId",
                table: "Orders",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_OrderItems_TenantId",
                table: "OrderItems",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_TenantId",
                table: "Notifications",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_MenuItems_TenantId",
                table: "MenuItems",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryItems_TenantId",
                table: "InventoryItems",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Integrations_TenantId",
                table: "Integrations",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_GiftCards_TenantId",
                table: "GiftCards",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_GiftCards_TenantId_Code",
                table: "GiftCards",
                columns: new[] { "TenantId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FavoriteItems_TenantId",
                table: "FavoriteItems",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_TenantId",
                table: "Customers",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Coupons_TenantId",
                table: "Coupons",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Coupons_TenantId_Code",
                table: "Coupons",
                columns: new[] { "TenantId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Branches_TenantId",
                table: "Branches",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLog_TenantId",
                table: "AuditLog",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Approvals_TenantId",
                table: "Approvals",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Tenants_Slug",
                table: "Tenants",
                column: "Slug",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Tenants");

            migrationBuilder.DropIndex(
                name: "IX_Tasks_TenantId",
                table: "Tasks");

            migrationBuilder.DropIndex(
                name: "IX_Tables_TenantId",
                table: "Tables");

            migrationBuilder.DropIndex(
                name: "IX_Tables_TenantId_Code",
                table: "Tables");

            migrationBuilder.DropIndex(
                name: "IX_Subscriptions_TenantId",
                table: "Subscriptions");

            migrationBuilder.DropIndex(
                name: "IX_Staff_TenantId",
                table: "Staff");

            migrationBuilder.DropIndex(
                name: "IX_Shifts_TenantId",
                table: "Shifts");

            migrationBuilder.DropIndex(
                name: "IX_Settings_TenantId",
                table: "Settings");

            migrationBuilder.DropIndex(
                name: "IX_PerformanceReviews_TenantId",
                table: "PerformanceReviews");

            migrationBuilder.DropIndex(
                name: "IX_Orders_TenantId",
                table: "Orders");

            migrationBuilder.DropIndex(
                name: "IX_OrderItems_TenantId",
                table: "OrderItems");

            migrationBuilder.DropIndex(
                name: "IX_Notifications_TenantId",
                table: "Notifications");

            migrationBuilder.DropIndex(
                name: "IX_MenuItems_TenantId",
                table: "MenuItems");

            migrationBuilder.DropIndex(
                name: "IX_InventoryItems_TenantId",
                table: "InventoryItems");

            migrationBuilder.DropIndex(
                name: "IX_Integrations_TenantId",
                table: "Integrations");

            migrationBuilder.DropIndex(
                name: "IX_GiftCards_TenantId",
                table: "GiftCards");

            migrationBuilder.DropIndex(
                name: "IX_GiftCards_TenantId_Code",
                table: "GiftCards");

            migrationBuilder.DropIndex(
                name: "IX_FavoriteItems_TenantId",
                table: "FavoriteItems");

            migrationBuilder.DropIndex(
                name: "IX_Customers_TenantId",
                table: "Customers");

            migrationBuilder.DropIndex(
                name: "IX_Coupons_TenantId",
                table: "Coupons");

            migrationBuilder.DropIndex(
                name: "IX_Coupons_TenantId_Code",
                table: "Coupons");

            migrationBuilder.DropIndex(
                name: "IX_Branches_TenantId",
                table: "Branches");

            migrationBuilder.DropIndex(
                name: "IX_AuditLog_TenantId",
                table: "AuditLog");

            migrationBuilder.DropIndex(
                name: "IX_Approvals_TenantId",
                table: "Approvals");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "Tables");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "Staff");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "Shifts");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "PerformanceReviews");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "OrderItems");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "Notifications");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "MenuItems");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "InventoryItems");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "Integrations");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "GiftCards");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "FavoriteItems");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "Coupons");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "Branches");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "AuditLog");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "Approvals");

            migrationBuilder.CreateIndex(
                name: "IX_Tables_Code",
                table: "Tables",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GiftCards_Code",
                table: "GiftCards",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Coupons_Code",
                table: "Coupons",
                column: "Code",
                unique: true);
        }
    }
}
