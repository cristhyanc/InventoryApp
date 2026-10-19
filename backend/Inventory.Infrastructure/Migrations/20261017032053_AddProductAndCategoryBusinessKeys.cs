using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <summary>
    /// Makes <c>(BusinessId, Id)</c> a unique key of <c>Products</c> and of <c>Categories</c>
    /// (issue #732), so the provider identity link tables the next migration adds can point at a
    /// local row <em>and</em> its business with one foreign key.
    ///
    /// <para><strong>It constrains nothing new about the data.</strong> <c>Id</c> is already the
    /// primary key of both tables, so a pair containing it is unique whatever the other column
    /// holds: no existing row can violate this, and the upgrade can neither fail on data nor change
    /// any value. What the key adds is a parent SQLite will accept a composite foreign key against,
    /// which is what makes "a link belongs to the same business as its product" a database
    /// guarantee instead of an application rule (AGENTS.md § Tenant ownership and data
    /// isolation).</para>
    ///
    /// <para><strong>It rebuilds both tables, because SQLite has no way to add a constraint to an
    /// existing one.</strong> EF's rebuild creates <c>ef_temp_Products</c>/<c>ef_temp_Categories</c>
    /// with the same columns, copies every row by name, drops the original and renames the copy,
    /// then recreates the indexes. <c>Id</c> is copied explicitly and is each table's
    /// <c>INTEGER PRIMARY KEY</c>, so every key value - and therefore every foreign key in
    /// <c>ReceiptItems</c>, <c>StockAdjustments</c>, <c>SupplierOrderLines</c>,
    /// <c>MachineStockCounts</c>, <c>InventoryCostRepairs</c>, the costing baselines and the
    /// products' own category reference - still resolves to the same row afterwards. The drop and
    /// rename run with <c>PRAGMA foreign_keys = 0</c>, which is also what stops SQLite rewriting
    /// other tables' <c>REFERENCES</c> clauses to the temporary name.
    /// <c>AddProviderIdentityLinksMigrationTests</c> upgrades a seeded database from the previous
    /// migration and asserts that every id, relationship and costing value survives.</para>
    ///
    /// <para>Separate from the migration that uses it so the link tables' backfill runs after the
    /// rebuild rather than before it; see that migration for why the order matters.</para>
    /// </summary>
    public partial class AddProductAndCategoryBusinessKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_Products_BusinessId_Id",
                table: "Products",
                columns: new[] { "BusinessId", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Categories_BusinessId_Id",
                table: "Categories",
                columns: new[] { "BusinessId", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropUniqueConstraint(
                name: "AK_Products_BusinessId_Id",
                table: "Products");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Categories_BusinessId_Id",
                table: "Categories");
        }
    }
}
