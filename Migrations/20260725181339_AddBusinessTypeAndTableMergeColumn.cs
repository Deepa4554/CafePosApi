using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CafePOS.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddBusinessTypeAndTableMergeColumn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // MergedIntoTableId already landed via the earlier AddTableMerge migration
            // (20260724180051) — that one shipped on a different branch than this
            // migration was generated from, so this migration's auto-generated diff
            // picked it up again; dropped here to avoid re-adding it.
            // IF NOT EXISTS (rather than plain AddColumn) because this same column also
            // already landed directly on the shared dev database from an earlier run of
            // this migration that crashed before EF could record it in the history table
            // — idempotent here so a fresh database and this already-patched one both end
            // up correct.
            migrationBuilder.Sql(@"ALTER TABLE ""Settings"" ADD COLUMN IF NOT EXISTS ""BusinessType"" text;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BusinessType",
                table: "Settings");
        }
    }
}
