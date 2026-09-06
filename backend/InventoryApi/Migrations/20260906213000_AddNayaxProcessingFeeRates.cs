using System;
using InventoryApi.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InventoryApi.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260906213000_AddNayaxProcessingFeeRates")]
public partial class AddNayaxProcessingFeeRates : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "NayaxProcessingFeeRates",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                EffectiveFrom = table.Column<DateTime>(type: "TEXT", nullable: false),
                FeeExGst = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_NayaxProcessingFeeRates", x => x.Id));

        migrationBuilder.CreateIndex(
            name: "IX_NayaxProcessingFeeRates_EffectiveFrom",
            table: "NayaxProcessingFeeRates",
            column: "EffectiveFrom",
            unique: true);

        migrationBuilder.Sql("""
            INSERT INTO "NayaxProcessingFeeRates" ("EffectiveFrom", "FeeExGst", "CreatedAt")
            VALUES ('1900-01-01 00:00:00', 0.17, '2026-09-06 00:00:00');
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "NayaxProcessingFeeRates");
}
