using InventoryApi.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InventoryApi.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20260907123300_RemoveProductMapped")]
    public partial class RemoveProductMapped : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("PRAGMA foreign_keys = OFF;", suppressTransaction: true);
            migrationBuilder.Sql("""
                CREATE TABLE "Products_New" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_Products" PRIMARY KEY AUTOINCREMENT,
                    "Name" TEXT NOT NULL,
                    "Sku" TEXT NULL,
                    "Description" TEXT NULL,
                    "UnitPrice" decimal(18,2) NOT NULL,
                    "QuantityInStock" INTEGER NOT NULL,
                    "LowStockThreshold" INTEGER NOT NULL,
                    "Unit" TEXT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    "UpdatedAt" TEXT NOT NULL,
                    "CategoryId" INTEGER NULL,
                    "SupplierId" INTEGER NULL,
                    "IsActive" INTEGER NOT NULL,
                    "AverageUnitCost" decimal(18,6) NOT NULL,
                    CONSTRAINT "FK_Products_Categories_CategoryId" FOREIGN KEY ("CategoryId") REFERENCES "Categories" ("Id") ON DELETE SET NULL,
                    CONSTRAINT "FK_Products_Suppliers_SupplierId" FOREIGN KEY ("SupplierId") REFERENCES "Suppliers" ("Id") ON DELETE SET NULL
                );
                """);
            migrationBuilder.Sql("""
                INSERT INTO "Products_New" ("Id", "Name", "Sku", "Description", "UnitPrice", "QuantityInStock", "LowStockThreshold", "Unit", "CreatedAt", "UpdatedAt", "CategoryId", "SupplierId", "IsActive", "AverageUnitCost")
                SELECT "Id", "Name", "Sku", "Description", "UnitPrice", "QuantityInStock", "LowStockThreshold", "Unit", "CreatedAt", "UpdatedAt", "CategoryId", "SupplierId", "IsActive", "AverageUnitCost"
                FROM "Products";
                """);
            migrationBuilder.Sql("DROP TABLE \"Products\";");
            migrationBuilder.Sql("ALTER TABLE \"Products_New\" RENAME TO \"Products\";");
            migrationBuilder.Sql("CREATE INDEX \"IX_Products_CategoryId\" ON \"Products\" (\"CategoryId\");");
            migrationBuilder.Sql("CREATE INDEX \"IX_Products_Sku\" ON \"Products\" (\"Sku\");");
            migrationBuilder.Sql("CREATE INDEX \"IX_Products_SupplierId\" ON \"Products\" (\"SupplierId\");");
            migrationBuilder.Sql("PRAGMA foreign_keys = ON;", suppressTransaction: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("PRAGMA foreign_keys = OFF;", suppressTransaction: true);
            migrationBuilder.Sql("""
                CREATE TABLE "Products_New" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_Products" PRIMARY KEY AUTOINCREMENT,
                    "Name" TEXT NOT NULL,
                    "Sku" TEXT NULL,
                    "Description" TEXT NULL,
                    "UnitPrice" decimal(18,2) NOT NULL,
                    "QuantityInStock" INTEGER NOT NULL,
                    "LowStockThreshold" INTEGER NOT NULL,
                    "Unit" TEXT NULL,
                    "Mapped" INTEGER NOT NULL DEFAULT 0,
                    "CreatedAt" TEXT NOT NULL,
                    "UpdatedAt" TEXT NOT NULL,
                    "CategoryId" INTEGER NULL,
                    "SupplierId" INTEGER NULL,
                    "IsActive" INTEGER NOT NULL,
                    "AverageUnitCost" decimal(18,6) NOT NULL,
                    CONSTRAINT "FK_Products_Categories_CategoryId" FOREIGN KEY ("CategoryId") REFERENCES "Categories" ("Id") ON DELETE SET NULL,
                    CONSTRAINT "FK_Products_Suppliers_SupplierId" FOREIGN KEY ("SupplierId") REFERENCES "Suppliers" ("Id") ON DELETE SET NULL
                );
                """);
            migrationBuilder.Sql("""
                INSERT INTO "Products_New" ("Id", "Name", "Sku", "Description", "UnitPrice", "QuantityInStock", "LowStockThreshold", "Unit", "CreatedAt", "UpdatedAt", "CategoryId", "SupplierId", "IsActive", "AverageUnitCost")
                SELECT "Id", "Name", "Sku", "Description", "UnitPrice", "QuantityInStock", "LowStockThreshold", "Unit", "CreatedAt", "UpdatedAt", "CategoryId", "SupplierId", "IsActive", "AverageUnitCost"
                FROM "Products";
                """);
            migrationBuilder.Sql("DROP TABLE \"Products\";");
            migrationBuilder.Sql("ALTER TABLE \"Products_New\" RENAME TO \"Products\";");
            migrationBuilder.Sql("CREATE INDEX \"IX_Products_CategoryId\" ON \"Products\" (\"CategoryId\");");
            migrationBuilder.Sql("CREATE INDEX \"IX_Products_Sku\" ON \"Products\" (\"Sku\");");
            migrationBuilder.Sql("CREATE INDEX \"IX_Products_SupplierId\" ON \"Products\" (\"SupplierId\");");
            migrationBuilder.Sql("PRAGMA foreign_keys = ON;", suppressTransaction: true);
        }
    }
}
