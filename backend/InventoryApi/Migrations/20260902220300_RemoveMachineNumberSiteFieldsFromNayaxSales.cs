using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InventoryApi.Migrations
{
    public partial class RemoveMachineNumberSiteFieldsFromNayaxSales : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MachineNumber",
                table: "NayaxSales");

            migrationBuilder.DropColumn(
                name: "SiteID",
                table: "NayaxSales");

            migrationBuilder.DropColumn(
                name: "SiteName",
                table: "NayaxSales");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MachineNumber",
                table: "NayaxSales",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SiteID",
                table: "NayaxSales",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "SiteName",
                table: "NayaxSales",
                type: "TEXT",
                nullable: true);
        }
    }
}
