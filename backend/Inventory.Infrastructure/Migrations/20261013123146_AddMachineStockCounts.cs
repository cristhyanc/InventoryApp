using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMachineStockCounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MachineStockCounts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BusinessId = table.Column<int>(type: "INTEGER", nullable: false),
                    MachineId = table.Column<long>(type: "INTEGER", nullable: false),
                    ProductId = table.Column<long>(type: "INTEGER", nullable: false),
                    CountedQuantity = table.Column<int>(type: "INTEGER", nullable: false),
                    CountedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MachineStockCounts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MachineStockCounts_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MachineStockCounts_BusinessId",
                table: "MachineStockCounts",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_MachineStockCounts_BusinessId_MachineId_ProductId_CountedAtUtc",
                table: "MachineStockCounts",
                columns: new[] { "BusinessId", "MachineId", "ProductId", "CountedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MachineStockCounts_ProductId",
                table: "MachineStockCounts",
                column: "ProductId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MachineStockCounts");
        }
    }
}
