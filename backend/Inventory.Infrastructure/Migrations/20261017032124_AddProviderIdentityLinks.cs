using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <summary>
    /// Adds the provider identity link tables and backfills the Nayax link every existing product
    /// and category already implies (issue #732, parent #329 decision P1).
    ///
    /// <para><strong>Additive, and releasable on its own.</strong> No existing table, column, id or
    /// foreign key changes here, and nothing reads a link yet: code already running in production
    /// ignores these two tables entirely, which is what lets the backfill run at Production startup
    /// before the slices that resolve through a link (#733, #745, #747, #734) exist.</para>
    ///
    /// <para><strong>Why the alternate keys are a separate, earlier migration.</strong> Each link's
    /// foreign key is the pair <c>(BusinessId, local id)</c>, so its parent columns must carry a
    /// uniqueness constraint - <c>AddProductAndCategoryBusinessKeys</c>, which SQLite can only add
    /// by rebuilding <c>Products</c> and <c>Categories</c>. The SQLite generator defers a pending
    /// rebuild to the end of the migration it occurs in, which would have run the inserts below
    /// <em>before</em> the parent key existed and failed them with "foreign key mismatch". Splitting
    /// the two is what makes the order right, and it is what EF's own warning 30200 asks for.</para>
    ///
    /// <para><strong>What the backfill claims.</strong> Only that <c>Product.Id</c> and
    /// <c>Category.Id</c> are today the Nayax <c>NayaxProductID</c> and <c>ProductGroupID</c>, which
    /// is how <c>EfNayaxProductCatalogImportStore</c> writes them. Every link therefore belongs to
    /// the business of the row it was read from, one per row, and the unique indexes above would
    /// refuse the insert if that were not so.</para>
    /// </summary>
    public partial class AddProviderIdentityLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            CreateLinkTable(migrationBuilder, "CategoryExternalIds", "CategoryId", "Categories");
            CreateLinkTable(migrationBuilder, "ProductExternalIds", "ProductId", "Products");

            // The backfill. Every product and category that exists arrived through the Nayax
            // catalogue import, which writes the remote identifier as the local primary key, so the
            // link recorded here is a fact already in the row rather than a guess: nothing is
            // inferred from a name, nothing is matched, no existing id or foreign key is read for
            // anything else or changed, and a database with no catalogue inserts no rows at all.
            //
            // Provider is written as the literal 1 - the stored value of
            // Inventory.Domain.Providers.ExternalProvider.Nayax - rather than referenced, because a
            // migration is a historical statement about rows and must keep meaning the same thing
            // if that enum is ever renumbered. The same convention the status and role migrations
            // follow.
            //
            // CAST(Id AS TEXT) is SQLite's integer-to-text conversion, which produces exactly the
            // invariant decimal form Inventory.Domain.Providers.NayaxExternalId.From writes, so a
            // backfilled ExternalId reads back through NayaxExternalId.TryParse as the id it came
            // from (AddProviderIdentityLinksMigrationTests asserts that round trip).
            migrationBuilder.Sql(
                """
                INSERT INTO "ProductExternalIds" ("BusinessId", "Provider", "ExternalId", "ProductId")
                SELECT "BusinessId", 1, CAST("Id" AS TEXT), "Id" FROM "Products";
                """);

            migrationBuilder.Sql(
                """
                INSERT INTO "CategoryExternalIds" ("BusinessId", "Provider", "ExternalId", "CategoryId")
                SELECT "BusinessId", 1, CAST("Id" AS TEXT), "Id" FROM "Categories";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CategoryExternalIds");

            migrationBuilder.DropTable(
                name: "ProductExternalIds");
        }

        /// <summary>
        /// Creates one provider link table and its four indexes. The product and category tables
        /// are the same shape and differ only in the local id column and the table it points at, so
        /// both are written by this one method rather than by two copies of the same operations.
        /// </summary>
        private static void CreateLinkTable(
            MigrationBuilder migrationBuilder, string linkTable, string localIdColumn, string localTable)
        {
            migrationBuilder.CreateTable(
                name: linkTable,
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BusinessId = table.Column<int>(type: "INTEGER", nullable: false),
                    Provider = table.Column<int>(type: "INTEGER", nullable: false),
                    ExternalId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    LocalId = table.Column<long>(name: localIdColumn, type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey($"PK_{linkTable}", x => x.Id);
                    table.ForeignKey(
                        name: $"FK_{linkTable}_{localTable}_BusinessId_{localIdColumn}",
                        columns: x => new { x.BusinessId, x.LocalId },
                        principalTable: localTable,
                        principalColumns: new[] { "BusinessId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: $"IX_{linkTable}_BusinessId",
                table: linkTable,
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: $"IX_{linkTable}_BusinessId_{localIdColumn}",
                table: linkTable,
                columns: new[] { "BusinessId", localIdColumn });

            migrationBuilder.CreateIndex(
                name: $"IX_{linkTable}_BusinessId_Provider_{localIdColumn}",
                table: linkTable,
                columns: new[] { "BusinessId", "Provider", localIdColumn },
                unique: true);

            migrationBuilder.CreateIndex(
                name: $"IX_{linkTable}_BusinessId_Provider_ExternalId",
                table: linkTable,
                columns: new[] { "BusinessId", "Provider", "ExternalId" },
                unique: true);
        }
    }
}
