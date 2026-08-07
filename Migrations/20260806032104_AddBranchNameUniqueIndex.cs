using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CafePOS.Api.Migrations
{
    /// <summary>
    /// Same reasoning as AddDuplicateNameUniqueIndexes (MenuItems/InventoryItems): the
    /// BranchesController check is read-then-write, so a double-tapped Save or a retried
    /// offline-queue request can race past it and insert twice. This makes the database the
    /// final arbiter for Branches too.
    ///
    /// Case-insensitive (matches the controller's ToLower() comparison) and partial —
    /// active branches only, keyed on lower(Name) per tenant — so a deactivated branch's
    /// name is free to reuse (deactivate-then-recreate under the same name is legitimate,
    /// e.g. a location that closes and reopens later).
    ///
    /// Expression + partial indexes can't be expressed in the EF model, so this is raw SQL
    /// and the model snapshot stays unchanged (see AddDuplicateNameUniqueIndexes).
    /// </summary>
    public partial class AddBranchNameUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Any duplicates already in the data would make the CREATE INDEX below fail and
            // crash-loop the deploy, so clear them first. Suffixing with the row's own Id
            // keeps the rename deterministic and collision-free; the oldest row (lowest Id)
            // keeps the clean name. Matches nothing on a healthy database.
            migrationBuilder.Sql("""
                WITH ranked AS (
                    SELECT "Id", ROW_NUMBER() OVER (
                        PARTITION BY "TenantId", lower("Name") ORDER BY "Id") AS rn
                    FROM "Branches"
                    WHERE "IsActive"
                )
                UPDATE "Branches" b
                SET "Name" = b."Name" || ' (' || b."Id" || ')'
                FROM ranked
                WHERE b."Id" = ranked."Id" AND ranked.rn > 1;
                """);

            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_Branches_TenantId_NameLower_Active"
                ON "Branches" ("TenantId", lower("Name"))
                WHERE "IsActive";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Only the index comes back off — the de-duplicating rename above has no
            // sensible inverse (the original names were the broken state).
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_Branches_TenantId_NameLower_Active";""");
        }
    }
}
