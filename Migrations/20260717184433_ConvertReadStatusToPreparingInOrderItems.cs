using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CafePOS.Api.Migrations
{
    /// <inheritdoc />
    public partial class ConvertReadStatusToPreparingInOrderItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Convert all 'Read' status orders to 'Preparing' since READ stage was removed from enum
            migrationBuilder.Sql("UPDATE \"Orders\" SET \"Status\" = 'Preparing' WHERE \"Status\" = 'Read';");

            // Also convert OrderItems with 'Read' status to 'Preparing'
            migrationBuilder.Sql("UPDATE \"OrderItems\" SET \"Status\" = 'Preparing' WHERE \"Status\" = 'Read';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Revert: Convert 'Preparing' back to 'Read' (for rollback)
            migrationBuilder.Sql("UPDATE \"Orders\" SET \"Status\" = 'Read' WHERE \"Status\" = 'Preparing';");
            migrationBuilder.Sql("UPDATE \"OrderItems\" SET \"Status\" = 'Read' WHERE \"Status\" = 'Preparing';");
        }
    }
}
