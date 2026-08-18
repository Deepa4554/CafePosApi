using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace CafePOS.Api.Migrations
{
    /// <summary>
    /// Gives every cafe its own bill numbering. Until now the number printed on a receipt was
    /// derived from Orders."Id" — one identity sequence shared by every tenant in the database —
    /// so a cafe that onboarded once the platform already held 454 orders saw its very first
    /// bill printed as "#1455", and the number leaked the platform's running order count to
    /// anyone holding a receipt.
    ///
    /// Adds Orders."BillNumber" plus a per-tenant "BillCounters" row that
    /// OrderBuildingService.NextBillNumberAsync bumps atomically, then backfills both so old
    /// bills renumber consistently rather than sitting at 0 beside new ones starting at 1.
    /// </summary>
    public partial class AddPerTenantBillNumber : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BillNumber",
                table: "Orders",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "BillCounters",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    LastNumber = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BillCounters", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Orders_TenantId_BillNumber",
                table: "Orders",
                columns: new[] { "TenantId", "BillNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_BillCounters_TenantId",
                table: "BillCounters",
                column: "TenantId",
                unique: true);

            // Backfill: renumber every existing order 1..N within its own tenant, in the order
            // the orders were actually created. "Id" is the ordering key rather than "CreatedAt"
            // because it is strictly monotonic per insert — two orders rung up in the same second
            // share a CreatedAt and would number arbitrarily.
            //
            // Old bills already handed to guests still say "#1455"; that string stays findable
            // because SearchController falls back to matching id = typed - 1000, and
            // OrderNumberFormat renders the same fallback for any row this backfill missed.
            migrationBuilder.Sql("""
                WITH numbered AS (
                    SELECT "Id", row_number() OVER (PARTITION BY "TenantId" ORDER BY "Id") AS rn
                    FROM "Orders"
                )
                UPDATE "Orders" o SET "BillNumber" = numbered.rn
                FROM numbered WHERE o."Id" = numbered."Id";
                """);

            // Seed each tenant's counter at its highest backfilled number, so the next order
            // continues the sequence instead of restarting at 1 and colliding with history.
            // Tenants with no orders get no row at all — NextBillNumberAsync's UPSERT inserts
            // one starting at 1 the first time they ring something up.
            migrationBuilder.Sql("""
                INSERT INTO "BillCounters" ("TenantId", "LastNumber")
                SELECT "TenantId", MAX("BillNumber") FROM "Orders" GROUP BY "TenantId"
                ON CONFLICT ("TenantId") DO NOTHING;
                """);
        }

        /// <summary>Reverts to id-derived numbering. The per-cafe numbers themselves are lost —
        /// they were only ever stored in the dropped column — but re-running Up regenerates the
        /// identical sequence, since the backfill is a pure function of (TenantId, Id).</summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BillCounters");

            migrationBuilder.DropIndex(
                name: "IX_Orders_TenantId_BillNumber",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "BillNumber",
                table: "Orders");
        }
    }
}
