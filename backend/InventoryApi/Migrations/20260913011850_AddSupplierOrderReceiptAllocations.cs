using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InventoryApi.Migrations
{
    /// <inheritdoc />
    public partial class AddSupplierOrderReceiptAllocations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SupplierOrderReceiptAllocations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SupplierOrderLineId = table.Column<int>(type: "INTEGER", nullable: false),
                    ReceiptItemId = table.Column<int>(type: "INTEGER", nullable: false),
                    QuantityApplied = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierOrderReceiptAllocations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SupplierOrderReceiptAllocations_ReceiptItems_ReceiptItemId",
                        column: x => x.ReceiptItemId,
                        principalTable: "ReceiptItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SupplierOrderReceiptAllocations_SupplierOrderLines_SupplierOrderLineId",
                        column: x => x.SupplierOrderLineId,
                        principalTable: "SupplierOrderLines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierOrderReceiptAllocations_ReceiptItemId",
                table: "SupplierOrderReceiptAllocations",
                column: "ReceiptItemId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierOrderReceiptAllocations_SupplierOrderLineId",
                table: "SupplierOrderReceiptAllocations",
                column: "SupplierOrderLineId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SupplierOrderReceiptAllocations");
        }
    }
}
