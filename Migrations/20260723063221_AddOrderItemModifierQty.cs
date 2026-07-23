using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CafePOS.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderItemModifierQty : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // defaultValue 1, not the scaffolded 0: every already-placed order's add-on rows
            // predate this column and each represents exactly one unit, so backfilling them
            // with 0 would retroactively zero out their contribution to the line price.
            migrationBuilder.AddColumn<int>(
                name: "Qty",
                table: "OrderItemModifiers",
                type: "integer",
                nullable: false,
                defaultValue: 1);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Qty",
                table: "OrderItemModifiers");
        }
    }
}
