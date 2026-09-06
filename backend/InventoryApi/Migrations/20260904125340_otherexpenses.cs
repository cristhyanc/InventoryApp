using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InventoryApi.Migrations
{
    /// <inheritdoc />
    public partial class otherexpenses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OperatingExpenses",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ExpenseDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Category = table.Column<int>(type: "INTEGER", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    AmountExGst = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    GstAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    TotalAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    SupplierId = table.Column<int>(type: "INTEGER", nullable: true),
                    SiteId = table.Column<long>(type: "INTEGER", nullable: true),
                    MachineId = table.Column<long>(type: "INTEGER", nullable: true),
                    ReceiptId = table.Column<int>(type: "INTEGER", nullable: true),
                    ServicePeriodStart = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ServicePeriodEnd = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Notes = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperatingExpenses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OperatingExpenses_Receipts_ReceiptId",
                        column: x => x.ReceiptId,
                        principalTable: "Receipts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_OperatingExpenses_Suppliers_SupplierId",
                        column: x => x.SupplierId,
                        principalTable: "Suppliers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OperatingExpenses_Category",
                table: "OperatingExpenses",
                column: "Category");

            migrationBuilder.CreateIndex(
                name: "IX_OperatingExpenses_ExpenseDate",
                table: "OperatingExpenses",
                column: "ExpenseDate");

            migrationBuilder.CreateIndex(
                name: "IX_OperatingExpenses_MachineId",
                table: "OperatingExpenses",
                column: "MachineId");

            migrationBuilder.CreateIndex(
                name: "IX_OperatingExpenses_ReceiptId",
                table: "OperatingExpenses",
                column: "ReceiptId");

            migrationBuilder.CreateIndex(
                name: "IX_OperatingExpenses_SupplierId",
                table: "OperatingExpenses",
                column: "SupplierId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OperatingExpenses");
        }
    }
}
