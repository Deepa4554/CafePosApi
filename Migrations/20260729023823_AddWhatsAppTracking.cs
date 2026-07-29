using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace CafePOS.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddWhatsAppTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "WhatsAppOrderUpdatesEnabled",
                table: "Settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "WhatsAppMessageLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    OrderId = table.Column<int>(type: "integer", nullable: true),
                    TrackingId = table.Column<int>(type: "integer", nullable: true),
                    Direction = table.Column<string>(type: "text", nullable: false),
                    Type = table.Column<string>(type: "text", nullable: false),
                    ToOrFromPhoneE164 = table.Column<string>(type: "text", nullable: true),
                    Body = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<string>(type: "text", nullable: false),
                    FailureReason = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WhatsAppMessageLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WhatsAppSessions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    Status = table.Column<string>(type: "text", nullable: false),
                    PhoneNumberE164 = table.Column<string>(type: "text", nullable: true),
                    ConnectedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastQrGeneratedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastDisconnectReason = table.Column<string>(type: "text", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WhatsAppSessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WhatsAppTracking",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    OrderId = table.Column<int>(type: "integer", nullable: false),
                    TrackingId = table.Column<string>(type: "text", nullable: false),
                    WhatsAppNumberE164 = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LinkedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WhatsAppTracking", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WhatsAppMessageLogs_TenantId",
                table: "WhatsAppMessageLogs",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_WhatsAppSessions_TenantId",
                table: "WhatsAppSessions",
                column: "TenantId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WhatsAppTracking_OrderId",
                table: "WhatsAppTracking",
                column: "OrderId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WhatsAppTracking_TenantId",
                table: "WhatsAppTracking",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_WhatsAppTracking_TrackingId",
                table: "WhatsAppTracking",
                column: "TrackingId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WhatsAppMessageLogs");

            migrationBuilder.DropTable(
                name: "WhatsAppSessions");

            migrationBuilder.DropTable(
                name: "WhatsAppTracking");

            migrationBuilder.DropColumn(
                name: "WhatsAppOrderUpdatesEnabled",
                table: "Settings");
        }
    }
}
