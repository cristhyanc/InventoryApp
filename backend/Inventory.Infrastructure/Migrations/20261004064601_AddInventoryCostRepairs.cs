using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddInventoryCostRepairs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InventoryCostRepairs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BusinessId = table.Column<int>(type: "INTEGER", nullable: false),
                    ProductId = table.Column<long>(type: "INTEGER", nullable: false),
                    EffectiveAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Quantity = table.Column<int>(type: "INTEGER", nullable: false),
                    UnitCost = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    TotalValue = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedByDirectoryTenantId = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedByObjectId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryCostRepairs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InventoryCostRepairs_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryCostRepairs_BusinessId",
                table: "InventoryCostRepairs",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryCostRepairs_BusinessId_ProductId_EffectiveAt",
                table: "InventoryCostRepairs",
                columns: new[] { "BusinessId", "ProductId", "EffectiveAt" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryCostRepairs_ProductId",
                table: "InventoryCostRepairs",
                column: "ProductId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InventoryCostRepairs");
        }
    }
}
