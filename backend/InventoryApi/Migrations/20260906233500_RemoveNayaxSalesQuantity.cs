using InventoryApi.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InventoryApi.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260906233500_RemoveNayaxSalesQuantity")]
public partial class RemoveNayaxSalesQuantity : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE "NayaxSales_New" (
                "TransactionID" INTEGER NOT NULL CONSTRAINT "PK_NayaxSales" PRIMARY KEY,
                "MachineAuthorizationTime" TEXT NOT NULL,
                "MachineID" INTEGER NOT NULL,
                "MachineName" TEXT NULL,
                "NayaxProductId" INTEGER NULL,
                "PaymentMethod" TEXT NULL,
                "ProductName" TEXT NULL,
                "SettlementValue" decimal(18,2) NOT NULL,
                "TransactionStatusId" INTEGER NULL,
                "CostOfGoodsSold" decimal(18,6) NULL,
                "CostingStatus" INTEGER NOT NULL DEFAULT 0,
                "UnitCostAtSale" decimal(18,6) NULL
            );
            INSERT INTO "NayaxSales_New" (
                "TransactionID", "MachineAuthorizationTime", "MachineID", "MachineName", "NayaxProductId",
                "PaymentMethod", "ProductName", "SettlementValue", "TransactionStatusId", "CostOfGoodsSold",
                "CostingStatus", "UnitCostAtSale")
            SELECT
                "TransactionID", "MachineAuthorizationTime", "MachineID", "MachineName", "NayaxProductId",
                "PaymentMethod", "ProductName", "SettlementValue", "TransactionStatusId", "CostOfGoodsSold",
                "CostingStatus", "UnitCostAtSale"
            FROM "NayaxSales";
            DROP TABLE "NayaxSales";
            ALTER TABLE "NayaxSales_New" RENAME TO "NayaxSales";
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<decimal>(
            name: "Quantity",
            table: "NayaxSales",
            type: "decimal(18,2)",
            nullable: false,
            defaultValue: 1m);
}
