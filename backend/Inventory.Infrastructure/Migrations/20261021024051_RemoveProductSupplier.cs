using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <summary>
    /// Drops the product's supplier association (issue #787, A1 of #786): the
    /// <c>Products.SupplierId</c> column with its foreign key to <c>Suppliers</c> and its index.
    ///
    /// <para><strong>This deletes stored business data, and the repository owner approved that on
    /// 21 October 2026.</strong> The column was a default for ordering that no ordering decision
    /// ever read; the supplier a business actually transacts with is recorded on the purchase and
    /// on the supplier order, and the per-product price comparison derives every supplier a product
    /// was really bought from out of that purchase history.</para>
    ///
    /// <para><strong><c>Down</c> cannot restore the values.</strong> It recreates the column, its
    /// index and its foreign key so the schema matches the previous release's code, and every row
    /// comes back <c>NULL</c>. Nothing is inferred in their place - a guessed supplier would be a
    /// false statement about what the business agreed.</para>
    ///
    /// <para>Nothing else moves: every other product column, <c>UnitPrice</c> included (its removal
    /// is issue #792), the suppliers themselves, supplier CRUD, and every purchase and
    /// supplier-order supplier, unit cost, quantity and total are untouched.</para>
    /// </summary>
    public partial class RemoveProductSupplier : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Products_Suppliers_SupplierId",
                table: "Products");

            migrationBuilder.DropIndex(
                name: "IX_Products_SupplierId",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "SupplierId",
                table: "Products");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SupplierId",
                table: "Products",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Products_SupplierId",
                table: "Products",
                column: "SupplierId");

            migrationBuilder.AddForeignKey(
                name: "FK_Products_Suppliers_SupplierId",
                table: "Products",
                column: "SupplierId",
                principalTable: "Suppliers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
