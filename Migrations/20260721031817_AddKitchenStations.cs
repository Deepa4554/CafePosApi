using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace CafePOS.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddKitchenStations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // KOT/order lines snapshot the station name at creation time (same convention as
            // OrderItem.Name/VariantName) — historical rows get the same "Kitchen" fallback
            // MenuItem.KitchenStation itself defaulted to.
            migrationBuilder.AddColumn<string>(
                name: "StationName",
                table: "OrderItems",
                type: "text",
                nullable: false,
                defaultValue: "Kitchen");

            migrationBuilder.CreateTable(
                name: "Stations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Icon = table.Column<string>(type: "text", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    Active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Stations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Stations_TenantId",
                table: "Stations",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Stations_TenantId_Name",
                table: "Stations",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            // Backfill: one Station row per distinct (TenantId, trimmed KitchenStation) value
            // that's actually in use — case-insensitively deduped so "Bakery"/"BAKERY" typos
            // collapse into a single managed station instead of becoming two. A menu item
            // with a blank/whitespace-only KitchenStation falls back to "Kitchen".
            migrationBuilder.Sql("""
                INSERT INTO "Stations" ("TenantId", "Name", "Icon", "SortOrder", "Active")
                SELECT "TenantId", "Name", 'chef-hat', 0, true
                FROM (
                    SELECT "TenantId",
                           MIN(TRIM(BOTH FROM COALESCE(NULLIF(TRIM(BOTH FROM "KitchenStation"), ''), 'Kitchen'))) AS "Name",
                           LOWER(TRIM(BOTH FROM COALESCE(NULLIF(TRIM(BOTH FROM "KitchenStation"), ''), 'Kitchen'))) AS "NameKey"
                    FROM "MenuItems"
                    GROUP BY "TenantId", LOWER(TRIM(BOTH FROM COALESCE(NULLIF(TRIM(BOTH FROM "KitchenStation"), ''), 'Kitchen')))
                ) distinct_stations;
                """);

            // Nullable + no default while we backfill row-by-row from the just-created
            // Stations, then locked down to NOT NULL with the real FK below.
            migrationBuilder.AddColumn<int>(
                name: "StationId",
                table: "MenuItems",
                type: "integer",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE "MenuItems" m
                SET "StationId" = s."Id"
                FROM "Stations" s
                WHERE s."TenantId" = m."TenantId"
                  AND LOWER(s."Name") = LOWER(TRIM(BOTH FROM COALESCE(NULLIF(TRIM(BOTH FROM m."KitchenStation"), ''), 'Kitchen')));
                """);

            migrationBuilder.DropColumn(
                name: "KitchenStation",
                table: "MenuItems");

            migrationBuilder.AlterColumn<int>(
                name: "StationId",
                table: "MenuItems",
                type: "integer",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MenuItems_StationId",
                table: "MenuItems",
                column: "StationId");

            // Restrict, not Cascade — a station with menu items still pointing at it can't be
            // deleted at the DB level either way (StationsController already blocks it in the
            // API), but Restrict is the correct failure mode if that check is ever bypassed.
            migrationBuilder.AddForeignKey(
                name: "FK_MenuItems_Stations_StationId",
                table: "MenuItems",
                column: "StationId",
                principalTable: "Stations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "KitchenStation",
                table: "MenuItems",
                type: "text",
                nullable: false,
                defaultValue: "KITCHEN");

            migrationBuilder.Sql("""
                UPDATE "MenuItems" m
                SET "KitchenStation" = UPPER(s."Name")
                FROM "Stations" s
                WHERE s."Id" = m."StationId";
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_MenuItems_Stations_StationId",
                table: "MenuItems");

            migrationBuilder.DropIndex(
                name: "IX_MenuItems_StationId",
                table: "MenuItems");

            migrationBuilder.DropColumn(
                name: "StationId",
                table: "MenuItems");

            migrationBuilder.DropTable(
                name: "Stations");

            migrationBuilder.DropColumn(
                name: "StationName",
                table: "OrderItems");
        }
    }
}
