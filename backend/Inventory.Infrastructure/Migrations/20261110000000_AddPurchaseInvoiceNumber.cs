using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <summary>
    /// Records the supplier's own receipt or invoice number on a purchase (issue #1162, sub-issue A
    /// of #975): one additive, nullable <c>InvoiceNumber</c> column of at most 100 characters on
    /// <c>Receipts</c>, and nothing else.
    ///
    /// <para><strong>There is no backfill, and there is nothing to backfill from.</strong> A purchase
    /// recorded before this migration has no supplier document number anywhere in this database, so
    /// every existing row keeps <c>NULL</c> - which is exactly what "this purchase records no invoice
    /// number" means. Deriving one from the title, the notes or an uploaded document's file name
    /// would state a supplier's invoice number that nobody entered.</para>
    ///
    /// <para><strong>Why <c>ALTER TABLE ... DROP COLUMN</c> on the way back.</strong> Dropping the
    /// column through the provider's own <c>DropColumn</c> would rebuild <c>Receipts</c> - the table
    /// every purchase line, receipt allocation and purchase-linked stock movement hangs off - to
    /// remove one unindexed, unconstrained nullable column, and it would need a point-in-time model
    /// to rebuild from. SQLite has dropped such a column in place since 3.35, which is what the
    /// bundled library this application runs on provides, so the rollback is the one statement it
    /// actually needs, exactly as
    /// <c>AddBusinessLifecycleStatusAndProfile</c> and
    /// <c>AddSupplierOrderPartialCompletion</c> already do. Rolling back loses the recorded invoice
    /// numbers, which is inherent: the older schema has nowhere to keep them.</para>
    /// </summary>
    public partial class AddPurchaseInvoiceNumber : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "InvoiceNumber",
                table: "Receipts",
                type: "TEXT",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE \"Receipts\" DROP COLUMN \"InvoiceNumber\";");
        }
    }
}
