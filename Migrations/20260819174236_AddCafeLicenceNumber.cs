using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CafePOS.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddCafeLicenceNumber : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LicenceNumber",
                table: "Settings",
                type: "text",
                nullable: true);

            // NOTE: EF also generated an AddColumn for OrderItems.Subtitle here, and it has
            // been deleted deliberately. That column was already created by
            // 20260817111215_AddOrderItemSubtitle and exists in every deployed database; the
            // model snapshot had simply lost it, so EF believed it was still missing. Leaving
            // the generated line in would have made this migration fail on deploy with
            // "column already exists". The regenerated snapshot shipped alongside this
            // migration is the real fix — it now records both columns, so the next migration
            // starts from a truthful picture instead of re-proposing this same column.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LicenceNumber",
                table: "Settings");

            // The matching DropColumn for OrderItems.Subtitle is deliberately absent: this
            // migration never created that column, so rolling it back must not destroy it and
            // the order-item subtitles stored in it.
        }
    }
}
