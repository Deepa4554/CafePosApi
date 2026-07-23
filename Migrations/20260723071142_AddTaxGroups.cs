using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace CafePOS.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddTaxGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "TaxAmount",
                table: "OrderItems",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "TaxRatePct",
                table: "OrderItems",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TaxableAmount",
                table: "OrderItems",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "TaxGroupId",
                table: "MenuItems",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TaxGroups",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    Name = table.Column<string>(type: "text", nullable: false),
                    RatePct = table.Column<decimal>(type: "numeric", nullable: false),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaxGroups", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TaxGroups_TenantId",
                table: "TaxGroups",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TaxGroups");

            migrationBuilder.DropColumn(
                name: "TaxAmount",
                table: "OrderItems");

            migrationBuilder.DropColumn(
                name: "TaxRatePct",
                table: "OrderItems");

            migrationBuilder.DropColumn(
                name: "TaxableAmount",
                table: "OrderItems");

            migrationBuilder.DropColumn(
                name: "TaxGroupId",
                table: "MenuItems");
        }
    }
}
