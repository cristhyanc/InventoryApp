using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InventoryApi.Migrations
{
    /// <inheritdoc />
    public partial class AddTransactionStatusId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "TransactionStatusId",
                table: "NayaxSales",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.Sql(
                "UPDATE NayaxSales SET TransactionStatusId = 12 WHERE TransactionStatusId IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TransactionStatusId",
                table: "NayaxSales");
        }
    }
}
