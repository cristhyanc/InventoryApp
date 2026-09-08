using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InventoryApi.Migrations
{
    /// <inheritdoc />
    public partial class AddHistoricalPerpetualInventoryCosting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "AverageUnitCostAfter",
                table: "StockAdjustments",
                type: "decimal(18,6)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CostingQuantityAfter",
                table: "StockAdjustments",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "InventoryValueAfter",
                table: "StockAdjustments",
                type: "decimal(18,6)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CostingQuantity",
                table: "Products",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "InventoryValue",
                table: "Products",
                type: "decimal(18,6)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AverageUnitCostAfter",
                table: "StockAdjustments");

            migrationBuilder.DropColumn(
                name: "CostingQuantityAfter",
                table: "StockAdjustments");

            migrationBuilder.DropColumn(
                name: "InventoryValueAfter",
                table: "StockAdjustments");

            migrationBuilder.DropColumn(
                name: "CostingQuantity",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "InventoryValue",
                table: "Products");
        }
    }
}
