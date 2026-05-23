using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.Floresta.Migrations
{
    /// <inheritdoc />
    public partial class AddTrackedWalletDescriptorMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DescriptorHash",
                schema: "floresta",
                table: "tracked_wallets",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReceiveDescriptor",
                schema: "floresta",
                table: "tracked_wallets",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ChangeDescriptor",
                schema: "floresta",
                table: "tracked_wallets",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DescriptorRegisteredAt",
                schema: "floresta",
                table: "tracked_wallets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DescriptorRegistrationError",
                schema: "floresta",
                table: "tracked_wallets",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DescriptorHash",
                schema: "floresta",
                table: "tracked_wallets");

            migrationBuilder.DropColumn(
                name: "ReceiveDescriptor",
                schema: "floresta",
                table: "tracked_wallets");

            migrationBuilder.DropColumn(
                name: "ChangeDescriptor",
                schema: "floresta",
                table: "tracked_wallets");

            migrationBuilder.DropColumn(
                name: "DescriptorRegisteredAt",
                schema: "floresta",
                table: "tracked_wallets");

            migrationBuilder.DropColumn(
                name: "DescriptorRegistrationError",
                schema: "floresta",
                table: "tracked_wallets");
        }
    }
}
