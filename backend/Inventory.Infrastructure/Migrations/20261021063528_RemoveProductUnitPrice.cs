using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <summary>
    /// Drops the product's catalogue selling price (issue #792, A2 of #786): the
    /// <c>Products.UnitPrice</c> column and every value stored in it.
    ///
    /// <para><strong>This deletes stored business data, and the repository owner approved that on
    /// 21 October 2026.</strong> It was the catalogue default/list price - a display value no
    /// calculation in the Domain or the Application layer ever read - and it stood beside the
    /// machine's own live price as a second, silently stale answer to what a product sells for.</para>
    ///
    /// <para><strong><c>Down</c> cannot restore the values.</strong> It recreates the column so the
    /// schema matches the previous release's code, and every row comes back at the column's
    /// <c>0</c> default. Nothing is inferred in its place - a guessed price would be a false
    /// statement about what the business charged.</para>
    ///
    /// <para><strong>Every cost is untouched.</strong> A product's <c>AverageUnitCost</c>,
    /// <c>CostingQuantity</c> and <c>InventoryValue</c>, a purchase item's <c>UnitCost</c>, a
    /// supplier-order line's agreed <c>SupplierOrderLines.UnitPrice</c> and every recorded sale's
    /// amount and historical cost stay exactly as they are, and nothing here recosts or backfills
    /// anything. The catalogue review's <c>ProviderCatalogueReviewItems.ProviderUnitPrice</c> is
    /// deliberately kept too, with its stored values: it records what the provider said when a
    /// review was read, and the 21 October scope clarification on issue #792 is explicit that it is
    /// neither dropped nor backfilled.</para>
    ///
    /// <para><strong>Running the previous release against this schema does not work</strong>, which
    /// is why the release sequence is human-controlled: Production applies pending migrations at
    /// startup (AGENTS.md § Database and migrations), so the API that starts after this one is
    /// applied is the one that no longer reads the column. Deploy the API before the frontend, as
    /// the Deploy Production workflow already does.</para>
    /// </summary>
    public partial class RemoveProductUnitPrice : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UnitPrice",
                table: "Products");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "UnitPrice",
                table: "Products",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 0m);
        }
    }
}
