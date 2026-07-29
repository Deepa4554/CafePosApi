using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CafePOS.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddWhatsAppQueueColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "NextAttemptAt",
                table: "WhatsAppMessageLogs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RetryCount",
                table: "WhatsAppMessageLogs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                table: "WhatsAppMessageLogs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_WhatsAppMessageLogs_TenantId_Direction_Status_NextAttemptAt",
                table: "WhatsAppMessageLogs",
                columns: new[] { "TenantId", "Direction", "Status", "NextAttemptAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WhatsAppMessageLogs_TenantId_Direction_Status_NextAttemptAt",
                table: "WhatsAppMessageLogs");

            migrationBuilder.DropColumn(
                name: "NextAttemptAt",
                table: "WhatsAppMessageLogs");

            migrationBuilder.DropColumn(
                name: "RetryCount",
                table: "WhatsAppMessageLogs");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "WhatsAppMessageLogs");
        }
    }
}
