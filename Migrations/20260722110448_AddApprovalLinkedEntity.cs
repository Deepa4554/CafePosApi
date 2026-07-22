using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CafePOS.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddApprovalLinkedEntity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LinkedEntityId",
                table: "Approvals",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PayloadJson",
                table: "Approvals",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LinkedEntityId",
                table: "Approvals");

            migrationBuilder.DropColumn(
                name: "PayloadJson",
                table: "Approvals");
        }
    }
}
