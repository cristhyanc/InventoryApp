using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddNayaxMachineStockEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Source",
                table: "StockAdjustments",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "NayaxMachineStockEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BusinessId = table.Column<int>(type: "INTEGER", nullable: false),
                    NayaxEventId = table.Column<long>(type: "INTEGER", nullable: false),
                    MachineId = table.Column<long>(type: "INTEGER", nullable: false),
                    EventCode = table.Column<int>(type: "INTEGER", nullable: false),
                    EventTimestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RawEventData = table.Column<string>(type: "TEXT", nullable: false),
                    RawSourceMetadata = table.Column<string>(type: "TEXT", nullable: true),
                    ParsedMdb = table.Column<int>(type: "INTEGER", nullable: true),
                    ParsedProductName = table.Column<string>(type: "TEXT", nullable: true),
                    ParsedQuantity = table.Column<int>(type: "INTEGER", nullable: true),
                    MatchedProductId = table.Column<long>(type: "INTEGER", nullable: true),
                    MatchStatus = table.Column<int>(type: "INTEGER", nullable: false),
                    NeedsReviewReason = table.Column<string>(type: "TEXT", nullable: true),
                    ProcessingStatus = table.Column<int>(type: "INTEGER", nullable: false),
                    ProcessedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    StockAdjustmentId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NayaxMachineStockEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NayaxMachineStockEvents_Products_MatchedProductId",
                        column: x => x.MatchedProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NayaxMachineStockEvents_BusinessId",
                table: "NayaxMachineStockEvents",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_NayaxMachineStockEvents_BusinessId_MachineId_ProcessingStatus",
                table: "NayaxMachineStockEvents",
                columns: new[] { "BusinessId", "MachineId", "ProcessingStatus" });

            migrationBuilder.CreateIndex(
                name: "IX_NayaxMachineStockEvents_BusinessId_NayaxEventId",
                table: "NayaxMachineStockEvents",
                columns: new[] { "BusinessId", "NayaxEventId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NayaxMachineStockEvents_MatchedProductId",
                table: "NayaxMachineStockEvents",
                column: "MatchedProductId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NayaxMachineStockEvents");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "StockAdjustments");
        }
    }
}
