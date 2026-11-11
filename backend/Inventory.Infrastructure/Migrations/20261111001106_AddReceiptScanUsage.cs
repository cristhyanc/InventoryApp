using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddReceiptScanUsage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ReceiptScanUsages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BusinessId = table.Column<int>(type: "INTEGER", nullable: false),
                    MonthKey = table.Column<int>(type: "INTEGER", nullable: false),
                    ScanCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReceiptScanUsages", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReceiptScanUsages_BusinessId",
                table: "ReceiptScanUsages",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_ReceiptScanUsages_BusinessId_MonthKey",
                table: "ReceiptScanUsages",
                columns: new[] { "BusinessId", "MonthKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReceiptScanUsages");
        }
    }
}
