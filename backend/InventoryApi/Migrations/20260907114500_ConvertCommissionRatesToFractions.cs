using InventoryApi.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InventoryApi.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260907114500_ConvertCommissionRatesToFractions")]
public partial class ConvertCommissionRatesToFractions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("UPDATE \"SiteCommissionAgreements\" SET \"CommissionRate\" = \"CommissionRate\" / 100.0 WHERE \"CommissionRate\" > 1;");

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("UPDATE \"SiteCommissionAgreements\" SET \"CommissionRate\" = \"CommissionRate\" * 100.0 WHERE \"CommissionRate\" <= 1;");
}
