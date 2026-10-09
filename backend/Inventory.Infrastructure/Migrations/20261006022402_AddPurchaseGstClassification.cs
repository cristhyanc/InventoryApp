using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPurchaseGstClassification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DeliveryGstClassification",
                table: "Receipts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "DeliveryGstClassificationSource",
                table: "Receipts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "PackageGstClassification",
                table: "Receipts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "PackageGstClassificationSource",
                table: "Receipts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "GstClassification",
                table: "ReceiptItems",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "GstClassificationSource",
                table: "ReceiptItems",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeliveryGstClassification",
                table: "Receipts");

            migrationBuilder.DropColumn(
                name: "DeliveryGstClassificationSource",
                table: "Receipts");

            migrationBuilder.DropColumn(
                name: "PackageGstClassification",
                table: "Receipts");

            migrationBuilder.DropColumn(
                name: "PackageGstClassificationSource",
                table: "Receipts");

            migrationBuilder.DropColumn(
                name: "GstClassification",
                table: "ReceiptItems");

            migrationBuilder.DropColumn(
                name: "GstClassificationSource",
                table: "ReceiptItems");
        }
    }
}
