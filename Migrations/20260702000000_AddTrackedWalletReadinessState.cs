using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.Floresta.Migrations
{
    /// <inheritdoc />
    public partial class AddTrackedWalletReadinessState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DescriptorStatus",
                schema: "floresta",
                table: "tracked_wallets",
                type: "text",
                nullable: false,
                defaultValue: "Unknown");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DescriptorStatusUpdatedAt",
                schema: "floresta",
                table: "tracked_wallets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RescanStatus",
                schema: "floresta",
                table: "tracked_wallets",
                type: "text",
                nullable: false,
                defaultValue: "Unknown");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RescanStatusUpdatedAt",
                schema: "floresta",
                table: "tracked_wallets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RescanRequestedAt",
                schema: "floresta",
                table: "tracked_wallets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RescanStartHeight",
                schema: "floresta",
                table: "tracked_wallets",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RescanError",
                schema: "floresta",
                table: "tracked_wallets",
                type: "text",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE floresta.tracked_wallets
                SET "DescriptorStatus" = CASE
                    WHEN "DescriptorRegistrationError" IS NOT NULL AND "DescriptorRegistrationError" <> '' THEN 'Failed'
                    WHEN "DescriptorRegisteredAt" IS NOT NULL THEN 'Registered'
                    ELSE 'Unknown'
                END
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DescriptorStatus",
                schema: "floresta",
                table: "tracked_wallets");

            migrationBuilder.DropColumn(
                name: "DescriptorStatusUpdatedAt",
                schema: "floresta",
                table: "tracked_wallets");

            migrationBuilder.DropColumn(
                name: "RescanStatus",
                schema: "floresta",
                table: "tracked_wallets");

            migrationBuilder.DropColumn(
                name: "RescanStatusUpdatedAt",
                schema: "floresta",
                table: "tracked_wallets");

            migrationBuilder.DropColumn(
                name: "RescanRequestedAt",
                schema: "floresta",
                table: "tracked_wallets");

            migrationBuilder.DropColumn(
                name: "RescanStartHeight",
                schema: "floresta",
                table: "tracked_wallets");

            migrationBuilder.DropColumn(
                name: "RescanError",
                schema: "floresta",
                table: "tracked_wallets");
        }
    }
}
