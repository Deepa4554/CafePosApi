using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CafePOS.Api.Migrations
{
    /// <summary>
    /// Backstop for the duplicate-name checks in MenuController/InventoryController: those run
    /// as read-then-write, so two requests racing (a double-tapped Save, an offline queue
    /// flushing twice) can both pass the check and both insert. A unique index makes the
    /// database the final arbiter.
    ///
    /// Both indexes are on lower(Name) because the controller checks compare case-insensitively —
    /// a plain index on Name would let "Milk" and "milk" through. Inventory's is additionally
    /// partial (active rows only) and keys on COALESCE(BranchId, 0), matching Create/Update:
    /// the same ingredient legitimately exists per branch, deactivated rows deliberately free
    /// up their name, and NULL BranchId (cafes with no branches set up) has to collapse to a
    /// real value or Postgres would treat every NULL row as distinct.
    ///
    /// Expression + partial indexes can't be expressed in the EF model, so this is raw SQL and
    /// the model snapshot stays unchanged.
    /// </summary>
    public partial class AddDuplicateNameUniqueIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Any duplicates already in the data would make the CREATE INDEX below fail and
            // crash-loop the deploy, so clear them first. Suffixing with the row's own Id
            // keeps the rename deterministic and collision-free; the oldest row (lowest Id)
            // keeps the clean name. Both statements match nothing on a healthy database.
            migrationBuilder.Sql("""
                WITH ranked AS (
                    SELECT "Id", ROW_NUMBER() OVER (
                        PARTITION BY "TenantId", lower("Name") ORDER BY "Id") AS rn
                    FROM "MenuItems"
                )
                UPDATE "MenuItems" m
                SET "Name" = m."Name" || ' (' || m."Id" || ')'
                FROM ranked
                WHERE m."Id" = ranked."Id" AND ranked.rn > 1;
                """);
            migrationBuilder.Sql("""
                WITH ranked AS (
                    SELECT "Id", ROW_NUMBER() OVER (
                        PARTITION BY "TenantId", COALESCE("BranchId", 0), lower("Name") ORDER BY "Id") AS rn
                    FROM "InventoryItems"
                    WHERE "IsActive"
                )
                UPDATE "InventoryItems" i
                SET "Name" = i."Name" || ' (' || i."Id" || ')'
                FROM ranked
                WHERE i."Id" = ranked."Id" AND ranked.rn > 1;
                """);

            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_MenuItems_TenantId_NameLower"
                ON "MenuItems" ("TenantId", lower("Name"));
                """);
            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_InventoryItems_TenantId_Branch_NameLower_Active"
                ON "InventoryItems" ("TenantId", COALESCE("BranchId", 0), lower("Name"))
                WHERE "IsActive";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Only the indexes come back off — the de-duplicating renames above have no
            // sensible inverse (the original names were the broken state).
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_InventoryItems_TenantId_Branch_NameLower_Active";""");
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_MenuItems_TenantId_NameLower";""");
        }
    }
}
