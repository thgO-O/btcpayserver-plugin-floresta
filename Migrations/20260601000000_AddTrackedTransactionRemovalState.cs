using System;
using BTCPayServer.Plugins.Floresta.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.Floresta.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(FlorestaDbContext))]
    [Migration("20260601000000_AddTrackedTransactionRemovalState")]
    public partial class AddTrackedTransactionRemovalState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RemovedAt",
                schema: "floresta",
                table: "transactions",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RemovedAt",
                schema: "floresta",
                table: "transactions");
        }
    }
}
