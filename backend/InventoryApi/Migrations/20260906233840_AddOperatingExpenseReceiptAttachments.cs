using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InventoryApi.Migrations
{
    /// <inheritdoc />
    public partial class AddOperatingExpenseReceiptAttachments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ReceiptContentType",
                table: "OperatingExpenses",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReceiptFileName",
                table: "OperatingExpenses",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReceiptStoredFileName",
                table: "OperatingExpenses",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReceiptContentType",
                table: "OperatingExpenses");

            migrationBuilder.DropColumn(
                name: "ReceiptFileName",
                table: "OperatingExpenses");

            migrationBuilder.DropColumn(
                name: "ReceiptStoredFileName",
                table: "OperatingExpenses");
        }
    }
}
