using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InventoryApi.Migrations
{
    public partial class AddNayaxSales : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NayaxSales",
                columns: table => new
                {
                    TransactionID = table.Column<long>(type: "INTEGER", nullable: false),
                    MachineID = table.Column<long>(type: "INTEGER", nullable: false),
                    MachineName = table.Column<string>(type: "TEXT", nullable: true),
                    MachineNumber = table.Column<string>(type: "TEXT", nullable: true),
                    SettlementValue = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    PaymentMethod = table.Column<string>(type: "TEXT", nullable: true),
                    ProductName = table.Column<string>(type: "TEXT", nullable: true),
                    Quantity = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    MachineAuthorizationTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SiteID = table.Column<int>(type: "INTEGER", nullable: false),
                    SiteName = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NayaxSales", x => x.TransactionID);
                });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NayaxSales");
        }
    }
}
