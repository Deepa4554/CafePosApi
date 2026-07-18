using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace CafePOS.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddModifiersAndMenuItemFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MenuItemChannelVisibilities");

            migrationBuilder.DropTable(
                name: "MenuSchedules");

            migrationBuilder.DropTable(
                name: "PriceChangeLogs");

            migrationBuilder.DropIndex(
                name: "IX_Variants_MenuItemId",
                table: "Variants");

            migrationBuilder.DropColumn(
                name: "DefaultVariantId",
                table: "MenuItems");

            migrationBuilder.DropColumn(
                name: "PreparationTime",
                table: "MenuItems");

            migrationBuilder.DropColumn(
                name: "PriceInclusiveTax",
                table: "MenuItems");

            migrationBuilder.DropColumn(
                name: "TaxGroupId",
                table: "MenuItems");

            migrationBuilder.AlterColumn<string>(
                name: "KitchenStation",
                table: "MenuItems",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ItemType",
                table: "MenuItems",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "VegNonVegType",
                table: "MenuItems",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Modifiers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    MenuItemId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Type = table.Column<string>(type: "text", nullable: false),
                    IsRequired = table.Column<bool>(type: "boolean", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Modifiers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Modifiers_MenuItems_MenuItemId",
                        column: x => x.MenuItemId,
                        principalTable: "MenuItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ModifierOptions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    ModifierId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Price = table.Column<decimal>(type: "numeric", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModifierOptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ModifierOptions_Modifiers_ModifierId",
                        column: x => x.ModifierId,
                        principalTable: "Modifiers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Variants_MenuItemId",
                table: "Variants",
                column: "MenuItemId");

            migrationBuilder.CreateIndex(
                name: "IX_ModifierOptions_ModifierId",
                table: "ModifierOptions",
                column: "ModifierId");

            migrationBuilder.CreateIndex(
                name: "IX_ModifierOptions_TenantId",
                table: "ModifierOptions",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Modifiers_MenuItemId",
                table: "Modifiers",
                column: "MenuItemId");

            migrationBuilder.CreateIndex(
                name: "IX_Modifiers_TenantId",
                table: "Modifiers",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ModifierOptions");

            migrationBuilder.DropTable(
                name: "Modifiers");

            migrationBuilder.DropIndex(
                name: "IX_Variants_MenuItemId",
                table: "Variants");

            migrationBuilder.DropColumn(
                name: "ItemType",
                table: "MenuItems");

            migrationBuilder.DropColumn(
                name: "VegNonVegType",
                table: "MenuItems");

            migrationBuilder.AlterColumn<string>(
                name: "KitchenStation",
                table: "MenuItems",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<int>(
                name: "DefaultVariantId",
                table: "MenuItems",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PreparationTime",
                table: "MenuItems",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "PriceInclusiveTax",
                table: "MenuItems",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "TaxGroupId",
                table: "MenuItems",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MenuItemChannelVisibilities",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Channel = table.Column<string>(type: "text", nullable: false),
                    IsVisible = table.Column<bool>(type: "boolean", nullable: false),
                    MenuItemId = table.Column<int>(type: "integer", nullable: false),
                    TenantId = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MenuItemChannelVisibilities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MenuItemChannelVisibilities_MenuItems_MenuItemId",
                        column: x => x.MenuItemId,
                        principalTable: "MenuItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MenuSchedules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CategoryName = table.Column<string>(type: "text", nullable: true),
                    DaysOfWeek = table.Column<string>(type: "text", nullable: false),
                    EndTime = table.Column<TimeSpan>(type: "interval", nullable: false),
                    MenuItemId = table.Column<int>(type: "integer", nullable: true),
                    StartTime = table.Column<TimeSpan>(type: "interval", nullable: false),
                    TenantId = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MenuSchedules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PriceChangeLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ChangedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ChangedByName = table.Column<string>(type: "text", nullable: true),
                    ChangedByUserId = table.Column<int>(type: "integer", nullable: false),
                    EffectiveFrom = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    MenuItemId = table.Column<int>(type: "integer", nullable: false),
                    NewPrice = table.Column<decimal>(type: "numeric", nullable: false),
                    OldPrice = table.Column<decimal>(type: "numeric", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: true),
                    TenantId = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    VariantId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PriceChangeLogs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Variants_MenuItemId",
                table: "Variants",
                column: "MenuItemId",
                unique: true,
                filter: "\"IsDefault\" = true");

            migrationBuilder.CreateIndex(
                name: "IX_MenuItemChannelVisibilities_MenuItemId",
                table: "MenuItemChannelVisibilities",
                column: "MenuItemId");

            migrationBuilder.CreateIndex(
                name: "IX_MenuItemChannelVisibilities_TenantId",
                table: "MenuItemChannelVisibilities",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_MenuSchedules_TenantId",
                table: "MenuSchedules",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_PriceChangeLogs_TenantId",
                table: "PriceChangeLogs",
                column: "TenantId");
        }
    }
}
