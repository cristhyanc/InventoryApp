using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InventoryApi.Migrations
{
    /// <inheritdoc />
    public partial class AddInventoryCostTransitionBaseline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InventoryCostTransitionBaselines",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProductId = table.Column<long>(type: "INTEGER", nullable: false),
                    CutoffAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    HomeStockQuantity = table.Column<int>(type: "INTEGER", nullable: false),
                    MachineStockQuantity = table.Column<int>(type: "INTEGER", nullable: false),
                    OpeningCostingQuantity = table.Column<int>(type: "INTEGER", nullable: false),
                    AverageUnitCost = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    InventoryValue = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    CostSource = table.Column<int>(type: "INTEGER", nullable: false),
                    LegacyReplayedPhysicalQuantity = table.Column<int>(type: "INTEGER", nullable: false),
                    LegacyPhysicalDiscrepancy = table.Column<int>(type: "INTEGER", nullable: false),
                    DataQualityNote = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryCostTransitionBaselines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InventoryCostTransitionBaselines_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "InventoryCostTransitionPreviewDrafts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProductId = table.Column<long>(type: "INTEGER", nullable: false),
                    SnapshotJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AppliedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryCostTransitionPreviewDrafts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InventoryCostTransitionMachineStocks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    InventoryCostTransitionBaselineId = table.Column<int>(type: "INTEGER", nullable: false),
                    MachineId = table.Column<long>(type: "INTEGER", nullable: false),
                    MachineName = table.Column<string>(type: "TEXT", nullable: true),
                    StockQuantity = table.Column<int>(type: "INTEGER", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryCostTransitionMachineStocks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InventoryCostTransitionMachineStocks_InventoryCostTransitionBaselines_InventoryCostTransitionBaselineId",
                        column: x => x.InventoryCostTransitionBaselineId,
                        principalTable: "InventoryCostTransitionBaselines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryCostTransitionBaselines_ProductId",
                table: "InventoryCostTransitionBaselines",
                column: "ProductId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InventoryCostTransitionMachineStocks_InventoryCostTransitionBaselineId",
                table: "InventoryCostTransitionMachineStocks",
                column: "InventoryCostTransitionBaselineId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InventoryCostTransitionMachineStocks");

            migrationBuilder.DropTable(
                name: "InventoryCostTransitionPreviewDrafts");

            migrationBuilder.DropTable(
                name: "InventoryCostTransitionBaselines");
        }
    }
}
