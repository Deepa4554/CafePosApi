using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace CafePOS.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddVariantModifierToOrderItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "VariantId",
                table: "OrderItems",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VariantName",
                table: "OrderItems",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "OrderItemModifiers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    OrderItemId = table.Column<int>(type: "integer", nullable: false),
                    ModifierOptionId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Price = table.Column<decimal>(type: "numeric", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderItemModifiers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OrderItemModifiers_OrderItems_OrderItemId",
                        column: x => x.OrderItemId,
                        principalTable: "OrderItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OrderItemModifiers_OrderItemId",
                table: "OrderItemModifiers",
                column: "OrderItemId");

            migrationBuilder.CreateIndex(
                name: "IX_OrderItemModifiers_TenantId",
                table: "OrderItemModifiers",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OrderItemModifiers");

            migrationBuilder.DropColumn(
                name: "VariantId",
                table: "OrderItems");

            migrationBuilder.DropColumn(
                name: "VariantName",
                table: "OrderItems");
        }
    }
}
