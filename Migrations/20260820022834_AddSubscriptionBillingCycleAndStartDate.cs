using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CafePOS.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddSubscriptionBillingCycleAndStartDate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // "Monthly", not EF's generated "": Cycle is a string-converted enum, so every
            // existing row would come back out of the database as an empty string and blow up
            // Enum.Parse on the very first read. Every plan sold before this migration was a
            // month, so Monthly is also the truthful value for them.
            migrationBuilder.AddColumn<string>(
                name: "Cycle",
                table: "Subscriptions",
                type: "text",
                nullable: false,
                defaultValue: "Monthly");

            migrationBuilder.AddColumn<DateTime>(
                name: "PlanStartedAt",
                table: "Subscriptions",
                type: "timestamp with time zone",
                nullable: true);

            // Backfill rather than leaving every existing cafe with a blank start date on its
            // Subscription screen. UpdatedAt is the honest answer: both change-plan paths stamp
            // it in the same breath as PlanExpiresAt, and a never-touched trial row still has
            // the UtcNow it was created with.
            migrationBuilder.Sql(@"UPDATE ""Subscriptions"" SET ""PlanStartedAt"" = ""UpdatedAt"" WHERE ""PlanStartedAt"" IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Cycle",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "PlanStartedAt",
                table: "Subscriptions");
        }
    }
}
