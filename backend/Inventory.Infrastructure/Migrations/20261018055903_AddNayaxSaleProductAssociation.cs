using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <summary>
    /// Gives every stored sale its own column for the local product it belongs to, and resolves that
    /// column for the sales that already exist from their recorded identifiers alone (issue #758,
    /// parent #329 decision A2c). Step 1 of two in this release;
    /// <c>ResolveNayaxSaleProductsByName</c> is step 2 and holds the failure gate.
    ///
    /// <para><strong>Additive, and it preserves every existing value.</strong> Three columns on
    /// <c>NayaxSales</c>, two append-only tables, an index and a foreign key. No existing column,
    /// amount, cost or identifier is changed: <c>NayaxProductId</c> is left exactly as it is and
    /// marked provenance-unknown, because before this release that column held the raw provider id
    /// on an export-written row and the <em>local</em> product id on a sync-written one, and nothing
    /// may read a value of unknown provenance as a key. Adding the foreign key rebuilds the table,
    /// which SQLite requires; the rebuild copies every row and every column.</para>
    ///
    /// <para><strong>Step 1 resolves only what the recorded ids already say.</strong> A sale's
    /// <c>ProductId</c> is set when its stored number names a product of the same business directly
    /// - which is what <c>ProductMatcher</c> does today - <em>and</em>, when some product of that
    /// business carries that same number as its <c>ExternalProductId</c> (issue #759), it is that
    /// same product. Those two agree for every row today, because #732's backfill recorded each
    /// product's Nayax id as the one it was keyed by and #759 copied it onto the row. When they
    /// disagree the sale stays unresolved and a <c>NayaxSaleProductConflicts</c> row records both
    /// candidates: the stored number is of mixed provenance, so there is no honest way to choose,
    /// and <strong>a conflicting sale is never resolved automatically - not by name, not by
    /// anything</strong>. Only an audited human decision settles one, through the
    /// <c>resolve-nayax-sale-products</c> command.</para>
    ///
    /// <para><strong>Nothing is guessed, and nothing is inferred from a name here.</strong> A number
    /// that names no product is left for step 2's name rule, which is the same precedence the
    /// readers apply today. The provider of every existing row is recorded as <c>Nayax</c>, written
    /// as the literal <c>1</c> rather than referencing
    /// <c>Inventory.Domain.Providers.ExternalProvider.Nayax</c>, because a migration is a historical
    /// statement about rows and must keep meaning the same thing if that enum is ever renumbered.</para>
    ///
    /// <para><strong>It works with the code already in production</strong>, which applies migrations
    /// at startup (AGENTS.md § Database and migrations): every column it adds is new, every table it
    /// adds is new, and the running code neither reads nor writes any of them.</para>
    /// </summary>
    public partial class AddNayaxSaleProductAssociation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ProductId",
                table: "NayaxSales",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Provider",
                table: "NayaxSales",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "ProviderProductIdIsRaw",
                table: "NayaxSales",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "NayaxSaleProductConflicts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BusinessId = table.Column<int>(type: "INTEGER", nullable: false),
                    NayaxSaleId = table.Column<long>(type: "INTEGER", nullable: false),
                    TransactionId = table.Column<long>(type: "INTEGER", nullable: false),
                    DirectCandidateProductId = table.Column<long>(type: "INTEGER", nullable: false),
                    ExternalCandidateProductId = table.Column<long>(type: "INTEGER", nullable: false),
                    DetectedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NayaxSaleProductConflicts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NayaxSaleProductResolutions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BusinessId = table.Column<int>(type: "INTEGER", nullable: false),
                    NayaxSaleId = table.Column<long>(type: "INTEGER", nullable: false),
                    TransactionId = table.Column<long>(type: "INTEGER", nullable: false),
                    PreviousProductId = table.Column<long>(type: "INTEGER", nullable: true),
                    NewProductId = table.Column<long>(type: "INTEGER", nullable: false),
                    Rule = table.Column<int>(type: "INTEGER", nullable: false),
                    CandidateProductIds = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    DecidedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    ResolvedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NayaxSaleProductResolutions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NayaxSales_BusinessId_ProductId",
                table: "NayaxSales",
                columns: new[] { "BusinessId", "ProductId" });

            migrationBuilder.CreateIndex(
                name: "IX_NayaxSaleProductConflicts_BusinessId",
                table: "NayaxSaleProductConflicts",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_NayaxSaleProductConflicts_BusinessId_NayaxSaleId",
                table: "NayaxSaleProductConflicts",
                columns: new[] { "BusinessId", "NayaxSaleId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NayaxSaleProductResolutions_BusinessId",
                table: "NayaxSaleProductResolutions",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_NayaxSaleProductResolutions_BusinessId_NayaxSaleId",
                table: "NayaxSaleProductResolutions",
                columns: new[] { "BusinessId", "NayaxSaleId" });

            migrationBuilder.AddForeignKey(
                name: "FK_NayaxSales_Products_BusinessId_ProductId",
                table: "NayaxSales",
                columns: new[] { "BusinessId", "ProductId" },
                principalTable: "Products",
                principalColumns: new[] { "BusinessId", "Id" },
                onDelete: ReferentialAction.Restrict);

            // Every row that exists came from Nayax: it is the only provider this application has
            // ever integrated, and the table is named for it. ProviderProductIdIsRaw stays 0 on all
            // of them - provenance-unknown - because the column it describes was written by two
            // writers that meant two different things by it.
            migrationBuilder.Sql("""UPDATE "NayaxSales" SET "Provider" = 1;""");

            // Step 1, ids. The stored number names a product of this business directly, and no
            // *other* product of that business carries it as its provider id. Correlated on the
            // business as well as the id, so a sale could not reach another business's product even
            // if the foreign key above were not already refusing it.
            migrationBuilder.Sql(
                """
                UPDATE "NayaxSales" SET "ProductId" = "NayaxProductId"
                WHERE "ProductId" IS NULL
                  AND "NayaxProductId" IS NOT NULL
                  AND EXISTS (
                      SELECT 1 FROM "Products" direct
                      WHERE direct."BusinessId" = "NayaxSales"."BusinessId"
                        AND direct."Id" = "NayaxSales"."NayaxProductId")
                  AND NOT EXISTS (
                      SELECT 1 FROM "Products" external
                      WHERE external."BusinessId" = "NayaxSales"."BusinessId"
                        AND external."ExternalProductId" = CAST("NayaxSales"."NayaxProductId" AS TEXT)
                        AND external."Id" <> "NayaxSales"."NayaxProductId");
                """);

            // The sales the two candidates disagreed about, with both candidates recorded as the
            // reason. At most one external candidate exists per business and number, because
            // (BusinessId, ExternalProductId) is uniquely indexed where it is not null.
            migrationBuilder.Sql(
                """
                INSERT INTO "NayaxSaleProductConflicts"
                    ("BusinessId", "NayaxSaleId", "TransactionId",
                     "DirectCandidateProductId", "ExternalCandidateProductId", "DetectedAtUtc")
                SELECT sale."BusinessId", sale."Id", sale."TransactionID",
                       sale."NayaxProductId", external."Id",
                       strftime('%Y-%m-%d %H:%M:%S', 'now')
                FROM "NayaxSales" sale
                JOIN "Products" external
                  ON external."BusinessId" = sale."BusinessId"
                 AND external."ExternalProductId" = CAST(sale."NayaxProductId" AS TEXT)
                 AND external."Id" <> sale."NayaxProductId"
                WHERE sale."ProductId" IS NULL
                  AND sale."NayaxProductId" IS NOT NULL
                  AND EXISTS (
                      SELECT 1 FROM "Products" direct
                      WHERE direct."BusinessId" = sale."BusinessId"
                        AND direct."Id" = sale."NayaxProductId");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_NayaxSales_Products_BusinessId_ProductId",
                table: "NayaxSales");

            migrationBuilder.DropTable(
                name: "NayaxSaleProductConflicts");

            migrationBuilder.DropTable(
                name: "NayaxSaleProductResolutions");

            migrationBuilder.DropIndex(
                name: "IX_NayaxSales_BusinessId_ProductId",
                table: "NayaxSales");

            migrationBuilder.DropColumn(
                name: "ProductId",
                table: "NayaxSales");

            migrationBuilder.DropColumn(
                name: "Provider",
                table: "NayaxSales");

            migrationBuilder.DropColumn(
                name: "ProviderProductIdIsRaw",
                table: "NayaxSales");
        }
    }
}
