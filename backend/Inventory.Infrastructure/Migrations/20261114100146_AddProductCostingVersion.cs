using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <summary>
    /// Records the version of a product's costing position (issue #1245, finding INV-08 of audit
    /// #1222): one additive, non-nullable <c>CostingVersion</c> column on <c>Products</c>, defaulting
    /// to <c>0</c>, and nothing else.
    ///
    /// <para><strong>Why the column exists.</strong> The sales cost-rebuild drain replays a product's
    /// pending rebuild outside SQLite's write lock and then acknowledges its claim. That claim was the
    /// pending row's <c>Revision</c> alone, which only moves when <em>another sale</em> adds work - so
    /// a purchase, a correction or a stock count committed during the replay changed the history the
    /// staged figures were computed from while leaving the claim intact, and the drain wrote a costing
    /// position that left the newer write out until the next rebuild. Every cost rebuild now moves this
    /// column in the same transaction as the position it writes, and the drain compares it.</para>
    ///
    /// <para><strong>No backfill, and nothing to backfill from.</strong> The column is a counter of
    /// rebuilds rather than a fact about one, so every existing product starting at <c>0</c> is exactly
    /// right: the first rebuild after the upgrade moves it to <c>1</c>, and what a drain compares is
    /// whether the value changed, never what it is. It records no quantity, cost or value, so no
    /// stored costing position, movement, purchase or sale is read or written here.</para>
    ///
    /// <para><strong>Why <c>ALTER TABLE ... DROP COLUMN</c> on the way back.</strong> Dropping the
    /// column through the provider's own <c>DropColumn</c> would rebuild <c>Products</c> - the table
    /// every stock movement, purchase line, sale association and costing row hangs off - to remove one
    /// unindexed, unconstrained column, and it would need a point-in-time model to rebuild from. SQLite
    /// has dropped such a column in place since 3.35, which is what the bundled library this
    /// application runs on provides, so the rollback is the one statement it actually needs, exactly as
    /// <c>AddPurchaseInvoiceNumber</c> and <c>AddBusinessLifecycleStatusAndProfile</c> already do.
    /// Rolling back loses the recorded versions, which costs nothing: a drain on the older schema
    /// compares the revision alone again, as it did before this migration.</para>
    /// </summary>
    public partial class AddProductCostingVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "CostingVersion",
                table: "Products",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE \"Products\" DROP COLUMN \"CostingVersion\";");
        }
    }
}
