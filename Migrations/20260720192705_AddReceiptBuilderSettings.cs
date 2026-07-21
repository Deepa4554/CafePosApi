using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CafePOS.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddReceiptBuilderSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "GstNumber",
                table: "Settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ReceiptShowAddress",
                table: "Settings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "ReceiptShowFooter",
                table: "Settings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "ReceiptShowGuestPhone",
                table: "Settings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "ReceiptShowItemNotes",
                table: "Settings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "ReceiptShowWaiterName",
                table: "Settings",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GstNumber",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "ReceiptShowAddress",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "ReceiptShowFooter",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "ReceiptShowGuestPhone",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "ReceiptShowItemNotes",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "ReceiptShowWaiterName",
                table: "Settings");
        }
    }
}
