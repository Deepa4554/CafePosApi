using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace CafePOS.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddDynoPlatformBridge : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Backfilled to match the entity default (true), not the CLR default: existing rows
            // would otherwise land on auto-accept OFF, so the first cafe to switch the bridge on
            // would have its Zomato orders sit unaccepted until the platform cancelled them.
            migrationBuilder.AddColumn<bool>(
                name: "DynoAutoAccept",
                table: "Settings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "DynoBridgeToken",
                table: "Settings",
                type: "text",
                nullable: true);

            // Likewise 30, the entity default — a backfilled 0 would promise every aggregator
            // customer an instant order the moment a cafe enabled the bridge.
            migrationBuilder.AddColumn<int>(
                name: "DynoDefaultPrepTimeMins",
                table: "Settings",
                type: "integer",
                nullable: false,
                defaultValue: 30);

            migrationBuilder.AddColumn<bool>(
                name: "DynoEnabled",
                table: "Settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "DynoLastContactAt",
                table: "Settings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SwiggyRestaurantId",
                table: "Settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ZomatoRestaurantId",
                table: "Settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PlatformGrossAmount",
                table: "Orders",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PlatformOrderId",
                table: "Orders",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PlatformProvider",
                table: "Orders",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PlatformStatus",
                table: "Orders",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PlatformCatalogEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    Provider = table.Column<string>(type: "text", nullable: false),
                    ResId = table.Column<string>(type: "text", nullable: false),
                    PlatformEntityId = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    IsCategory = table.Column<bool>(type: "boolean", nullable: false),
                    PlatformPrice = table.Column<decimal>(type: "numeric", nullable: true),
                    RefreshedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformCatalogEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PlatformMenuMappings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    Provider = table.Column<string>(type: "text", nullable: false),
                    ResId = table.Column<string>(type: "text", nullable: false),
                    PlatformEntityId = table.Column<string>(type: "text", nullable: false),
                    IsCategory = table.Column<bool>(type: "boolean", nullable: false),
                    MenuItemId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformMenuMappings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PlatformOrderPayloads",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    OrderId = table.Column<int>(type: "integer", nullable: false),
                    Provider = table.Column<string>(type: "text", nullable: false),
                    PlatformOrderId = table.Column<string>(type: "text", nullable: false),
                    RawJson = table.Column<string>(type: "text", nullable: false),
                    LinesParsed = table.Column<bool>(type: "boolean", nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformOrderPayloads", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PlatformStockChanges",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    Provider = table.Column<string>(type: "text", nullable: false),
                    ResId = table.Column<string>(type: "text", nullable: false),
                    PlatformEntityId = table.Column<string>(type: "text", nullable: false),
                    IsCategory = table.Column<bool>(type: "boolean", nullable: false),
                    DesiredInStock = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ProcessedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FailureReason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformStockChanges", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Orders_TenantId_PlatformProvider_PlatformOrderId",
                table: "Orders",
                columns: new[] { "TenantId", "PlatformProvider", "PlatformOrderId" },
                unique: true,
                filter: "\"PlatformOrderId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PlatformCatalogEntries_TenantId",
                table: "PlatformCatalogEntries",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_PlatformCatalogEntries_TenantId_Provider_ResId_PlatformEnti~",
                table: "PlatformCatalogEntries",
                columns: new[] { "TenantId", "Provider", "ResId", "PlatformEntityId", "IsCategory" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlatformMenuMappings_TenantId",
                table: "PlatformMenuMappings",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_PlatformMenuMappings_TenantId_Provider_ResId_PlatformEntity~",
                table: "PlatformMenuMappings",
                columns: new[] { "TenantId", "Provider", "ResId", "PlatformEntityId", "IsCategory" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlatformOrderPayloads_TenantId",
                table: "PlatformOrderPayloads",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_PlatformStockChanges_TenantId",
                table: "PlatformStockChanges",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_PlatformStockChanges_TenantId_ResId_ProcessedAt",
                table: "PlatformStockChanges",
                columns: new[] { "TenantId", "ResId", "ProcessedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlatformCatalogEntries");

            migrationBuilder.DropTable(
                name: "PlatformMenuMappings");

            migrationBuilder.DropTable(
                name: "PlatformOrderPayloads");

            migrationBuilder.DropTable(
                name: "PlatformStockChanges");

            migrationBuilder.DropIndex(
                name: "IX_Orders_TenantId_PlatformProvider_PlatformOrderId",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "DynoAutoAccept",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "DynoBridgeToken",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "DynoDefaultPrepTimeMins",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "DynoEnabled",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "DynoLastContactAt",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "SwiggyRestaurantId",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "ZomatoRestaurantId",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "PlatformGrossAmount",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "PlatformOrderId",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "PlatformProvider",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "PlatformStatus",
                table: "Orders");
        }
    }
}
