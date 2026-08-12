using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace CafePOS.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddTiffinPrepaidWallet : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Every existing subscriber (real, already-serving data) backfills to Postpaid — the
            // exact behavior they had before this column existed. EF's generator defaults an
            // added NOT NULL string column to "", which would fail to parse back into
            // TiffinPaymentMode on the very next read — deliberately overridden here.
            migrationBuilder.AddColumn<string>(
                name: "PaymentMode",
                table: "TiffinSubscribers",
                type: "text",
                nullable: false,
                defaultValue: "Postpaid");

            migrationBuilder.CreateTable(
                name: "TiffinWalletTransactions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    SubscriberId = table.Column<int>(type: "integer", nullable: false),
                    Type = table.Column<string>(type: "text", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric", nullable: false),
                    ForDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Method = table.Column<string>(type: "text", nullable: true),
                    Note = table.Column<string>(type: "text", nullable: true),
                    RecordedByUserId = table.Column<int>(type: "integer", nullable: true),
                    RecordedByName = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TiffinWalletTransactions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TiffinWalletTransactions_TiffinSubscribers_SubscriberId",
                        column: x => x.SubscriberId,
                        principalTable: "TiffinSubscribers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TiffinWalletTransactions_SubscriberId",
                table: "TiffinWalletTransactions",
                column: "SubscriberId");

            migrationBuilder.CreateIndex(
                name: "IX_TiffinWalletTransactions_TenantId",
                table: "TiffinWalletTransactions",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_TiffinWalletTransactions_TenantId_SubscriberId_ForDate",
                table: "TiffinWalletTransactions",
                columns: new[] { "TenantId", "SubscriberId", "ForDate" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TiffinWalletTransactions");

            migrationBuilder.DropColumn(
                name: "PaymentMode",
                table: "TiffinSubscribers");
        }
    }
}
