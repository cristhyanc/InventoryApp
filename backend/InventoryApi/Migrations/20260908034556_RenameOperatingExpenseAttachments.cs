using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InventoryApi.Migrations
{
    /// <inheritdoc />
    public partial class RenameOperatingExpenseAttachments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_OperatingExpenses_Receipts_ReceiptId",
                table: "OperatingExpenses");

            migrationBuilder.DropIndex(
                name: "IX_OperatingExpenses_ReceiptId",
                table: "OperatingExpenses");

            migrationBuilder.RenameColumn(
                name: "ReceiptStoredFileName",
                table: "OperatingExpenses",
                newName: "AttachmentStoredFileName");

            migrationBuilder.RenameColumn(
                name: "ReceiptFileName",
                table: "OperatingExpenses",
                newName: "AttachmentFileName");

            migrationBuilder.RenameColumn(
                name: "ReceiptContentType",
                table: "OperatingExpenses",
                newName: "AttachmentContentType");

            migrationBuilder.DropColumn(
                name: "ReceiptId",
                table: "OperatingExpenses");

            migrationBuilder.AddColumn<long>(
                name: "AttachmentFileSizeBytes",
                table: "OperatingExpenses",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "AttachmentStoredFileName",
                table: "OperatingExpenses",
                newName: "ReceiptStoredFileName");

            migrationBuilder.RenameColumn(
                name: "AttachmentFileName",
                table: "OperatingExpenses",
                newName: "ReceiptFileName");

            migrationBuilder.RenameColumn(
                name: "AttachmentContentType",
                table: "OperatingExpenses",
                newName: "ReceiptContentType");

            migrationBuilder.DropColumn(
                name: "AttachmentFileSizeBytes",
                table: "OperatingExpenses");

            migrationBuilder.AddColumn<int>(
                name: "ReceiptId",
                table: "OperatingExpenses",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_OperatingExpenses_ReceiptId",
                table: "OperatingExpenses",
                column: "ReceiptId");

            migrationBuilder.AddForeignKey(
                name: "FK_OperatingExpenses_Receipts_ReceiptId",
                table: "OperatingExpenses",
                column: "ReceiptId",
                principalTable: "Receipts",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
