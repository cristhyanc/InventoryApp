using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <summary>
    /// Moves a provider's identifier from the link tables of issue #732 onto the local row, and
    /// records which provider a business sells through (issue #759, parent #329 decision P1 as
    /// revised on 18 October 2026).
    ///
    /// <para><strong>Additive, and releasable on its own.</strong> Three nullable columns, two
    /// filtered unique indexes, a copy out of the link tables and a recorded verification of that
    /// copy. No existing column, id or foreign key changes, and <em>the link tables are deliberately
    /// not dropped</em>: Production applies migrations at startup, so the code already running there
    /// is still reading them when this migration lands. Nothing reads or writes them after this
    /// release, and #760 drops them once it is in production.</para>
    ///
    /// <para><strong>Rolling back past this release is not supported.</strong> From here the external
    /// ids are written only to the columns, so the link tables stop being kept up to date; code that
    /// reads them would see a catalogue frozen at this instant. A recovery goes forward with a fix.
    /// <c>Down</c> exists to undo the schema, not to make that rollback safe.</para>
    ///
    /// <para><strong>What the copy claims.</strong> Only what a link row already says: this business
    /// mapped this external id to this local row. The copy is restricted to
    /// <c>Provider = 1</c> - the stored value of <c>Inventory.Domain.Providers.ExternalProvider.Nayax</c>,
    /// written as a literal rather than referenced because a migration is a historical statement
    /// about rows and must keep meaning the same thing if that enum is ever renumbered. Nothing is
    /// matched, inferred from a name, or derived from a local key.</para>
    ///
    /// <para><strong>It refuses rather than copying something ambiguous.</strong> The three rules the
    /// column model needs - a link names a row in its own business, a business holds an external id
    /// once, and a local row carries at most one external id - are all guaranteed by the link tables'
    /// own constraints, so each check should count zero. They are checked anyway because SQLite
    /// enforces foreign keys only when <c>PRAGMA foreign_keys</c> is on, so an out-of-band insert or
    /// a restored backup could leave a state the constraints would have refused, and copying from it
    /// would put one business's identifier on another's product. A non-zero count aborts the
    /// migration with the rule it broke, before anything is copied.</para>
    ///
    /// <para><strong>And it verifies the copy afterwards.</strong> Every Nayax link row must equal
    /// its local row's new column; any difference aborts. The counts and the instant are then
    /// recorded in <c>ProviderIdentityColumnVerifications</c>, which exists so #760 can confirm the
    /// copy was verified before it drops the tables. That table is deliberately not part of the EF
    /// model: it is evidence about a migration, read by a migration, and nothing in the application
    /// reads or writes it.</para>
    /// </summary>
    public partial class AddProviderIdentityColumns : Migration
    {
        /// <summary>
        /// The guard the refusals below are raised through: a one-column scratch table whose inserts
        /// a trigger aborts whenever the count being inserted is not zero. SQLite can only raise an
        /// error from a trigger, so this is how a migration says "fail loudly, with the reason".
        /// </summary>
        private const string GuardTable = "ProviderIdentityCopyGuard";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExternalProductId",
                table: "Products",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalCategoryId",
                table: "Categories",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SalesProvider",
                table: "Businesses",
                type: "INTEGER",
                nullable: true);

            // Filtered, because "this row has no provider id" is a real state - every row of a
            // business with no provider is in it - and a plain unique index would allow exactly one
            // such row per business.
            migrationBuilder.CreateIndex(
                name: "IX_Products_BusinessId_ExternalProductId",
                table: "Products",
                columns: new[] { "BusinessId", "ExternalProductId" },
                unique: true,
                filter: "\"ExternalProductId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Categories_BusinessId_ExternalCategoryId",
                table: "Categories",
                columns: new[] { "BusinessId", "ExternalCategoryId" },
                unique: true,
                filter: "\"ExternalCategoryId\" IS NOT NULL");

            // A business sells through Nayax if anything in this database says it ever did: stored
            // credentials, a recorded provider link, or a stored Nayax sale. Every other business
            // keeps NULL - no provider - because inventory works fully without one and a provider
            // nobody configured must not be invented here.
            migrationBuilder.Sql(
                """
                UPDATE "Businesses" SET "SalesProvider" = 1
                WHERE "Id" IN (SELECT "BusinessId" FROM "BusinessNayaxConnections")
                   OR "Id" IN (SELECT "BusinessId" FROM "ProductExternalIds")
                   OR "Id" IN (SELECT "BusinessId" FROM "CategoryExternalIds")
                   OR "Id" IN (SELECT "BusinessId" FROM "NayaxSales");
                """);

            CreateGuard(migrationBuilder);

            RefuseWhenNotZero(
                migrationBuilder,
                "A provider link names a local row in a different business, so the provider identity "
                    + "copy was refused and nothing was copied. Repair the link rows first.",
                """
                SELECT (SELECT COUNT(*) FROM "ProductExternalIds" link
                        WHERE NOT EXISTS (SELECT 1 FROM "Products" local
                                          WHERE local."Id" = link."ProductId"
                                            AND local."BusinessId" = link."BusinessId"))
                     + (SELECT COUNT(*) FROM "CategoryExternalIds" link
                        WHERE NOT EXISTS (SELECT 1 FROM "Categories" local
                                          WHERE local."Id" = link."CategoryId"
                                            AND local."BusinessId" = link."BusinessId"))
                """);

            RefuseWhenNotZero(
                migrationBuilder,
                "A business holds the same external id twice, so the provider identity copy was "
                    + "refused and nothing was copied. One external id names one local row.",
                """
                SELECT (SELECT COUNT(*) FROM (SELECT 1 FROM "ProductExternalIds"
                                              GROUP BY "BusinessId", "ExternalId" HAVING COUNT(*) > 1))
                     + (SELECT COUNT(*) FROM (SELECT 1 FROM "CategoryExternalIds"
                                              GROUP BY "BusinessId", "ExternalId" HAVING COUNT(*) > 1))
                """);

            RefuseWhenNotZero(
                migrationBuilder,
                "A local row carries more than one external id, so the provider identity copy was "
                    + "refused and nothing was copied. One row holds one provider's id, because a "
                    + "business has one sales provider.",
                """
                SELECT (SELECT COUNT(*) FROM (SELECT 1 FROM "ProductExternalIds"
                                              GROUP BY "BusinessId", "ProductId" HAVING COUNT(*) > 1))
                     + (SELECT COUNT(*) FROM (SELECT 1 FROM "CategoryExternalIds"
                                              GROUP BY "BusinessId", "CategoryId" HAVING COUNT(*) > 1))
                """);

            // The copy. Correlated on the business as well as the local id, so a link row could not
            // reach another business's row even if the checks above had let one through.
            migrationBuilder.Sql(
                """
                UPDATE "Products" SET "ExternalProductId" = (
                    SELECT link."ExternalId" FROM "ProductExternalIds" link
                    WHERE link."Provider" = 1
                      AND link."BusinessId" = "Products"."BusinessId"
                      AND link."ProductId" = "Products"."Id")
                WHERE EXISTS (
                    SELECT 1 FROM "ProductExternalIds" link
                    WHERE link."Provider" = 1
                      AND link."BusinessId" = "Products"."BusinessId"
                      AND link."ProductId" = "Products"."Id");
                """);

            migrationBuilder.Sql(
                """
                UPDATE "Categories" SET "ExternalCategoryId" = (
                    SELECT link."ExternalId" FROM "CategoryExternalIds" link
                    WHERE link."Provider" = 1
                      AND link."BusinessId" = "Categories"."BusinessId"
                      AND link."CategoryId" = "Categories"."Id")
                WHERE EXISTS (
                    SELECT 1 FROM "CategoryExternalIds" link
                    WHERE link."Provider" = 1
                      AND link."BusinessId" = "Categories"."BusinessId"
                      AND link."CategoryId" = "Categories"."Id");
                """);

            RefuseWhenNotZero(
                migrationBuilder,
                "A provider link does not equal the external id now stored on its local row, so the "
                    + "provider identity copy is refused and rolled back rather than left half "
                    + "applied.",
                """
                SELECT (SELECT COUNT(*) FROM "ProductExternalIds" link
                        LEFT JOIN "Products" local
                          ON local."BusinessId" = link."BusinessId" AND local."Id" = link."ProductId"
                        WHERE link."Provider" = 1
                          AND (local."ExternalProductId" IS NULL
                               OR local."ExternalProductId" <> link."ExternalId"))
                     + (SELECT COUNT(*) FROM "CategoryExternalIds" link
                        LEFT JOIN "Categories" local
                          ON local."BusinessId" = link."BusinessId" AND local."Id" = link."CategoryId"
                        WHERE link."Provider" = 1
                          AND (local."ExternalCategoryId" IS NULL
                               OR local."ExternalCategoryId" <> link."ExternalId"))
                """);

            DropGuard(migrationBuilder);

            // The evidence #760 reads before it drops the link tables: one row per table, carrying
            // how many Nayax link rows were verified against the columns and when. Written only
            // after every check above passed, because a record of a verification that did not happen
            // would be worse than none.
            migrationBuilder.Sql(
                """
                CREATE TABLE IF NOT EXISTS "ProviderIdentityColumnVerifications" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_ProviderIdentityColumnVerifications" PRIMARY KEY AUTOINCREMENT,
                    "LinkTable" TEXT NOT NULL,
                    "VerifiedRowCount" INTEGER NOT NULL,
                    "VerifiedAtUtc" TEXT NOT NULL
                );
                """);

            migrationBuilder.Sql(
                """
                INSERT INTO "ProviderIdentityColumnVerifications" ("LinkTable", "VerifiedRowCount", "VerifiedAtUtc")
                SELECT 'ProductExternalIds',
                       (SELECT COUNT(*) FROM "ProductExternalIds" WHERE "Provider" = 1),
                       strftime('%Y-%m-%d %H:%M:%S', 'now');
                """);

            migrationBuilder.Sql(
                """
                INSERT INTO "ProviderIdentityColumnVerifications" ("LinkTable", "VerifiedRowCount", "VerifiedAtUtc")
                SELECT 'CategoryExternalIds',
                       (SELECT COUNT(*) FROM "CategoryExternalIds" WHERE "Provider" = 1),
                       strftime('%Y-%m-%d %H:%M:%S', 'now');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // It undoes the schema, and that is all it claims: the link tables were never emptied,
            // but they stopped being written while this release was live, so a database taken back
            // through here holds whatever they held when it was deployed. See the type remarks.
            migrationBuilder.Sql("""DROP TABLE IF EXISTS "ProviderIdentityColumnVerifications";""");

            migrationBuilder.DropIndex(
                name: "IX_Products_BusinessId_ExternalProductId",
                table: "Products");

            migrationBuilder.DropIndex(
                name: "IX_Categories_BusinessId_ExternalCategoryId",
                table: "Categories");

            migrationBuilder.DropColumn(
                name: "ExternalProductId",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "ExternalCategoryId",
                table: "Categories");

            migrationBuilder.DropColumn(
                name: "SalesProvider",
                table: "Businesses");
        }

        /// <summary>
        /// Creates the scratch table every check below inserts its count into. It is dropped again
        /// once the checks have passed, so this migration leaves nothing behind but the verification
        /// record.
        /// </summary>
        private static void CreateGuard(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql(
                $"""
                CREATE TABLE "{GuardTable}" ("Offending" INTEGER NOT NULL);
                """);

        private static void DropGuard(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql($"""DROP TABLE "{GuardTable}";""");

        /// <summary>
        /// Aborts the migration with <paramref name="message"/> when <paramref name="countingSql"/>
        /// counts anything at all.
        ///
        /// <para>A trigger is the only way SQLite can raise an error with a message of its own, so
        /// one is created, the count is inserted through it, and it is dropped again. The message is
        /// a literal because <c>RAISE</c> takes no expression; it names the rule that was broken and
        /// no row data, because it reaches the startup log.</para>
        /// </summary>
        private static void RefuseWhenNotZero(
            MigrationBuilder migrationBuilder, string message, string countingSql)
        {
            var trigger = $"TR_{GuardTable}_Refuse";

            migrationBuilder.Sql(
                $"""
                CREATE TRIGGER "{trigger}" BEFORE INSERT ON "{GuardTable}"
                WHEN NEW."Offending" <> 0
                BEGIN
                    SELECT RAISE(ABORT, '{message.Replace("'", "''")}');
                END;
                """);

            migrationBuilder.Sql(
                $"""
                INSERT INTO "{GuardTable}" ("Offending") {countingSql};
                """);

            migrationBuilder.Sql($"""DROP TRIGGER "{trigger}";""");
        }
    }
}
