using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CafePOS.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantRazorpayKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "OnlinePaymentEnabled",
                table: "Settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "RazorpayKeyId",
                table: "Settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RazorpayKeySecretEnc",
                table: "Settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RazorpayWebhookSecretEnc",
                table: "Settings",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OnlinePaymentEnabled",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "RazorpayKeyId",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "RazorpayKeySecretEnc",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "RazorpayWebhookSecretEnc",
                table: "Settings");
        }
    }
}
