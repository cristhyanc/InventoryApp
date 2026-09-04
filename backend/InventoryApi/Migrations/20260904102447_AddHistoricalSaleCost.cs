using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InventoryApi.Migrations
{
    /// <inheritdoc />
    public partial class AddHistoricalSaleCost : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "EffectiveAt",
                table: "StockAdjustments",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<decimal>(
                name: "CostOfGoodsSold",
                table: "NayaxSales",
                type: "decimal(18,6)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CostingStatus",
                table: "NayaxSales",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "UnitCostAtSale",
                table: "NayaxSales",
                type: "decimal(18,6)",
                nullable: true);

            migrationBuilder.Sql(
                "UPDATE StockAdjustments SET EffectiveAt = CreatedAt WHERE EffectiveAt = '0001-01-01 00:00:00'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EffectiveAt",
                table: "StockAdjustments");

            migrationBuilder.DropColumn(
                name: "CostOfGoodsSold",
                table: "NayaxSales");

            migrationBuilder.DropColumn(
                name: "CostingStatus",
                table: "NayaxSales");

            migrationBuilder.DropColumn(
                name: "UnitCostAtSale",
                table: "NayaxSales");
        }
    }
}
