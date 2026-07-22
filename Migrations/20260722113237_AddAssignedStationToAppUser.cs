using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CafePOS.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddAssignedStationToAppUser : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AssignedStationId",
                table: "Users",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AssignedStationId",
                table: "Users");
        }
    }
}
