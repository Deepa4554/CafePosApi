using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CafePOS.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddStaffScreenAccess : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AccessMode",
                table: "Users",
                type: "text",
                nullable: false,
                defaultValue: "Automatic");

            migrationBuilder.AddColumn<string>(
                name: "AllowedScreens",
                table: "Users",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AccessMode",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "AllowedScreens",
                table: "Users");
        }
    }
}
