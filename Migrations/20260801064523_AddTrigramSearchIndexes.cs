using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CafePOS.Api.Migrations
{
    /// <summary>
    /// SearchController matches with <c>.ToLower().Contains(term)</c>, i.e. SQL
    /// <c>LIKE '%term%'</c>. A leading wildcard defeats every ordinary B-tree index, so each
    /// of the four tables it searches was scanned in full — on every keystroke, by every
    /// staff member with the search box open, growing with the catalog.
    ///
    /// A pg_trgm GIN index is the one index type Postgres CAN use for that predicate, which
    /// is why this is a raw-SQL migration rather than a model change: it indexes an
    /// expression (<c>lower(col)</c>) with a non-default operator class, neither of which
    /// EF's index model can express.
    ///
    /// Both steps are written to degrade rather than fail. If the deployment's Postgres role
    /// isn't allowed to install extensions, the CREATE EXTENSION is swallowed and the index
    /// block is skipped — search keeps working exactly as it does today, just without the
    /// speed-up, and the deploy still comes up.
    /// </summary>
    public partial class AddTrigramSearchIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    CREATE EXTENSION IF NOT EXISTS pg_trgm;
                EXCEPTION WHEN OTHERS THEN
                    RAISE NOTICE 'pg_trgm not available on this instance — search stays sequential.';
                END $$;
                """);

            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'pg_trgm') THEN
                        CREATE INDEX IF NOT EXISTS "IX_MenuItems_Name_trgm"
                            ON "MenuItems" USING gin (lower("Name") gin_trgm_ops);
                        CREATE INDEX IF NOT EXISTS "IX_MenuItems_ShortCode_trgm"
                            ON "MenuItems" USING gin (lower("ShortCode") gin_trgm_ops);
                        CREATE INDEX IF NOT EXISTS "IX_Tables_Code_trgm"
                            ON "Tables" USING gin (lower("Code") gin_trgm_ops);
                        CREATE INDEX IF NOT EXISTS "IX_Tables_Zone_trgm"
                            ON "Tables" USING gin (lower("Zone") gin_trgm_ops);
                        CREATE INDEX IF NOT EXISTS "IX_Customers_Name_trgm"
                            ON "Customers" USING gin (lower("Name") gin_trgm_ops);
                        CREATE INDEX IF NOT EXISTS "IX_InventoryItems_Name_trgm"
                            ON "InventoryItems" USING gin (lower("Name") gin_trgm_ops);
                    END IF;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The extension itself is deliberately left installed — other things may have
            // started using it, and dropping it would cascade.
            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS "IX_MenuItems_Name_trgm";
                DROP INDEX IF EXISTS "IX_MenuItems_ShortCode_trgm";
                DROP INDEX IF EXISTS "IX_Tables_Code_trgm";
                DROP INDEX IF EXISTS "IX_Tables_Zone_trgm";
                DROP INDEX IF EXISTS "IX_Customers_Name_trgm";
                DROP INDEX IF EXISTS "IX_InventoryItems_Name_trgm";
                """);
        }
    }
}
