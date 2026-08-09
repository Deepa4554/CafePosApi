using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CafePOS.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddCourierDeliveryBooking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BorzoAuthToken",
                table: "Settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "BorzoEnabled",
                table: "Settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // defaultValue hand-set to true on both, to match the property initializers on
            // CafeSettings. EF derives a column default from the CLR type (false), not from the
            // initializer, so scaffolding gave every EXISTING cafe the opposite of the intended
            // default while new ones got the right one. On BorzoUseTestEnvironment that
            // difference is the whole safety story: false means live Borzo, so the first token
            // pasted into an already-existing cafe would have booked a real rider and spent real
            // money on what everyone assumed was a sandbox trial.
            migrationBuilder.AddColumn<bool>(
                name: "BorzoPassFeeToCustomer",
                table: "Settings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "BorzoUseTestEnvironment",
                table: "Settings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "PickupAddress",
                table: "Settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PickupLatitude",
                table: "Settings",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PickupLongitude",
                table: "Settings",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CourierBookedAt",
                table: "Orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "CourierFeeAmount",
                table: "Orders",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CourierOrderId",
                table: "Orders",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CourierProvider",
                table: "Orders",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CourierRiderName",
                table: "Orders",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CourierRiderPhone",
                table: "Orders",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CourierStatus",
                table: "Orders",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CourierTrackingUrl",
                table: "Orders",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeliveryAddress",
                table: "Orders",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "DeliveryLatitude",
                table: "Orders",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "DeliveryLongitude",
                table: "Orders",
                type: "numeric",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BorzoAuthToken",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "BorzoEnabled",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "BorzoPassFeeToCustomer",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "BorzoUseTestEnvironment",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "PickupAddress",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "PickupLatitude",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "PickupLongitude",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "CourierBookedAt",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "CourierFeeAmount",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "CourierOrderId",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "CourierProvider",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "CourierRiderName",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "CourierRiderPhone",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "CourierStatus",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "CourierTrackingUrl",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "DeliveryAddress",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "DeliveryLatitude",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "DeliveryLongitude",
                table: "Orders");
        }
    }
}
