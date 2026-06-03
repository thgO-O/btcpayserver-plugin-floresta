using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.Floresta.Migrations
{
    /// <inheritdoc />
    public partial class AddTrackedAddressOwners : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tracked_address_owners",
                schema: "floresta",
                columns: table => new
                {
                    WalletId = table.Column<string>(type: "text", nullable: false),
                    Scripthash = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tracked_address_owners", x => new { x.WalletId, x.Scripthash });
                    table.ForeignKey(
                        name: "FK_tracked_address_owners_tracked_addresses_Scripthash",
                        column: x => x.Scripthash,
                        principalSchema: "floresta",
                        principalTable: "tracked_addresses",
                        principalColumn: "Scripthash",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_tracked_address_owners_tracked_wallets_WalletId",
                        column: x => x.WalletId,
                        principalSchema: "floresta",
                        principalTable: "tracked_wallets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_tracked_address_owners_Scripthash",
                schema: "floresta",
                table: "tracked_address_owners",
                column: "Scripthash");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tracked_address_owners",
                schema: "floresta");
        }
    }
}
