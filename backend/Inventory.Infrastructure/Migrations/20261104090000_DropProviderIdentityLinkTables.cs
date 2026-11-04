using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <summary>
    /// Drops the <c>ProductExternalIds</c> and <c>CategoryExternalIds</c> link tables of issue #732
    /// (issue #760, parent #329 child A1c).
    ///
    /// <para><strong>Why now, and not with #759.</strong> #759 copied every link onto its local row
    /// (<c>Product.ExternalProductId</c>, <c>Category.ExternalCategoryId</c>) and stopped reading and
    /// writing these tables, but left them in place: Production applies migrations at startup, so the
    /// code already running there was still reading them when that migration landed. Since that
    /// release they are unused and are expected to have gone stale - an external id written after it
    /// exists only in the column - so what is dropped here is two tables nothing has read for a
    /// release.</para>
    ///
    /// <para><strong>It confirms #759's copy before it drops anything.</strong> #759's migration must
    /// be in the migration history and the post-copy verification it records in
    /// <c>ProviderIdentityColumnVerifications</c> must be there for both tables. That record is the
    /// whole reason dropping these rows is safe, so without it this migration refuses rather than
    /// deleting the only other copy of a provider's identifiers.</para>
    ///
    /// <para><strong>Then it classifies every remaining link row, and the classification decides.</strong>
    /// Each row is exactly one of four things, in this order:</para>
    /// <list type="bullet">
    /// <item><description><c>Orphaned</c> - the local row it names no longer exists. The link tables'
    /// foreign keys cascade on delete, so this should be impossible and the expected count is zero; a
    /// database where foreign keys were not enforced could hold one. There is nothing left to compare
    /// it against and nothing that reads it, so it is dropped with the table and counted.</description></item>
    /// <item><description><c>Unexplained: external id on another row</c> - the local row exists, and
    /// the link's external id is now on a <em>different</em> row of the same business. Dropping that
    /// would quietly bless a catalogue where a provider's identifier moved between rows, so the
    /// migration refuses.</description></item>
    /// <item><description><c>Unexplained: column differs</c> - the local row exists and its column is
    /// empty or holds another value. One of the two is wrong, this migration cannot tell which, and
    /// the column is what every lookup now reads. It refuses.</description></item>
    /// <item><description><c>Matched</c> - the local row exists and its column equals the link's
    /// value. The link says nothing the row does not, and it is dropped with the table.</description></item>
    /// </list>
    ///
    /// <para>A column set with no link row at all is expected and is not checked: #759 wrote the
    /// columns, and every external id recorded since then was written only there. The classification
    /// reads from the link rows outwards for exactly that reason.</para>
    ///
    /// <para><strong>Every link row is classified, whichever provider it names.</strong> There is no
    /// <c>Provider = 1</c> filter, unlike #759's copy: a link naming another provider (nothing ever
    /// wrote one - <c>ExternalProvider</c> has one member) holds a value the column model cannot
    /// represent, so it is refused as unexplained rather than silently dropped.</para>
    ///
    /// <para><strong>The classification is recorded outside this migration's transaction.</strong> The
    /// counts and the offending rows' ids are written into
    /// <c>ProviderLinkTableDropClassifications</c> by statements with <c>suppressTransaction</c>, so
    /// the record survives the refusal that rolls the rest of the migration back - which is the point:
    /// a refusal that said only "something is unexplained" would leave an operator with nothing to
    /// look at. The record holds ids only, never a name, an external id or anything else about a
    /// business, because it reaches the startup log's neighbourhood and stays in the database. On a
    /// successful run it is the log of what was dropped, including the orphan count. Like #759's
    /// verification table it is deliberately not part of the EF model: it is evidence about a
    /// migration, and no application code reads or writes it.</para>
    ///
    /// <para><strong>What stays.</strong> The <c>(BusinessId, Id)</c> alternate keys on
    /// <c>Products</c> and <c>Categories</c>. The one on <c>Products</c> has been the principal of
    /// <c>NayaxSales.ProductId</c>'s foreign key since issue #758 - the database-level guarantee that
    /// a sale names a product of its own business - so only the link tables, their indexes and their
    /// own foreign keys go away, and neither local table is rebuilt.</para>
    ///
    /// <para><strong><c>Down</c> recreates both tables empty.</strong> It restores the schema the
    /// previous release's code compiles against; the rows are not recoverable and nothing is invented
    /// in their place. Rolling back to code that <em>reads</em> these tables has been unsupported
    /// since #759 in any case: they stopped being kept up to date then.</para>
    /// </summary>
    public partial class DropProviderIdentityLinkTables : Migration
    {
        /// <summary>#759's migration, whose recorded verification this one requires.</summary>
        private const string CopyMigrationId = "20261018034928_AddProviderIdentityColumns";

        /// <summary>The table #759 records its post-copy verification in, one row per link table.</summary>
        private const string VerificationTable = "ProviderIdentityColumnVerifications";

        /// <summary>Where this migration records what it classified and what it dropped.</summary>
        private const string ClassificationTable = "ProviderLinkTableDropClassifications";

        private const string Orphaned = "Orphaned";
        private const string ExternalIdOnAnotherRow = "Unexplained: external id on another row";
        private const string ColumnDiffers = "Unexplained: column differs";
        private const string Matched = "Matched";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var guard = new SqliteRefusalGuard("ProviderLinkDropGuard");

            // 1. The prerequisite, inside the migration's own transaction: #759 ran, and it recorded
            //    that it verified the copy for both tables. The table's existence is checked through
            //    sqlite_master so this refusal, rather than "no such table", is what an operator sees.
            guard.Create(migrationBuilder);
            guard.RefuseWhenNotZero(
                migrationBuilder,
                "The provider identity copy of issue #759 cannot be confirmed: its migration is not in "
                    + "the migration history, or the table it records the verified copy in does not "
                    + "exist. The provider link tables were not dropped.",
                $"""
                SELECT CASE
                    WHEN (SELECT COUNT(*) FROM "__EFMigrationsHistory"
                          WHERE "MigrationId" = '{CopyMigrationId}') = 1
                     AND (SELECT COUNT(*) FROM "sqlite_master"
                          WHERE "type" = 'table' AND "name" = '{VerificationTable}') = 1
                    THEN 0 ELSE 1 END
                """);
            guard.RefuseWhenNotZero(
                migrationBuilder,
                "The provider identity copy of issue #759 is not recorded as verified for both link "
                    + "tables, so the provider link tables were not dropped.",
                $"""
                SELECT 2 - (SELECT COUNT(DISTINCT "LinkTable") FROM "{VerificationTable}"
                            WHERE "LinkTable" IN ('ProductExternalIds', 'CategoryExternalIds'))
                """);
            guard.Drop(migrationBuilder);

            // 2. The classification, committed on its own so a refusal below cannot take the evidence
            //    with it. Nothing has been dropped at this point and nothing is written anywhere else,
            //    so the only thing these statements leave behind is the record.
            migrationBuilder.Sql(
                $"""
                CREATE TABLE IF NOT EXISTS "{ClassificationTable}" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_{ClassificationTable}" PRIMARY KEY AUTOINCREMENT,
                    "LinkTable" TEXT NOT NULL,
                    "Classification" TEXT NOT NULL,
                    "RowCount" INTEGER NOT NULL,
                    "RowIds" TEXT NULL,
                    "ClassifiedAtUtc" TEXT NOT NULL
                );
                """,
                suppressTransaction: true);

            // A refused attempt runs again at the next startup, so the previous attempt's rows are
            // cleared rather than appended to: the record describes the database as it is now.
            migrationBuilder.Sql($"""DELETE FROM "{ClassificationTable}";""", suppressTransaction: true);

            Classify(migrationBuilder, "ProductExternalIds", "Products", "ProductId", "ExternalProductId");
            Classify(migrationBuilder, "CategoryExternalIds", "Categories", "CategoryId", "ExternalCategoryId");

            // 3. The decision and the drop, in one transaction again: if the refusal fires, the drops
            //    after it never run and the whole statement list rolls back to here.
            guard.Create(migrationBuilder);
            guard.RefuseWhenNotZero(
                migrationBuilder,
                "Some provider link rows are unexplained: the local row still exists and its external "
                    + "id column is empty, holds a different value, or that value is now on another row "
                    + "of the same business. Nothing was dropped. Every classified row is listed by id "
                    + $"in {ClassificationTable}.",
                $"""
                SELECT COALESCE(SUM("RowCount"), 0) FROM "{ClassificationTable}"
                WHERE "Classification" IN ('{ExternalIdOnAnotherRow}', '{ColumnDiffers}')
                """);
            guard.Drop(migrationBuilder);

            // The matched rows say nothing their local row does not, and the orphaned rows name a row
            // that is gone; both are dropped with the tables they are in, which is also what removes
            // the indexes and the composite foreign keys.
            migrationBuilder.DropTable(name: "ProductExternalIds");
            migrationBuilder.DropTable(name: "CategoryExternalIds");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($"""DROP TABLE IF EXISTS "{ClassificationTable}";""");

            CreateLinkTable(migrationBuilder, "CategoryExternalIds", "CategoryId", "Categories");
            CreateLinkTable(migrationBuilder, "ProductExternalIds", "ProductId", "Products");
        }

        /// <summary>
        /// Records one row per classification for <paramref name="linkTable"/>: the count, and for
        /// everything that is not <see cref="Matched"/> the ids of the rows themselves - the link
        /// row's own key and the local row it names, and nothing else about either.
        ///
        /// <para>All four classifications are written even when they count zero, so the record says
        /// what was looked for rather than only what was found. The <c>CASE</c> is ordered: a link
        /// whose local row is gone is orphaned and is never also read as a mismatch, and the id that
        /// moved to another row is reported as that rather than as a plain difference.</para>
        /// </summary>
        private static void Classify(
            MigrationBuilder migrationBuilder,
            string linkTable,
            string localTable,
            string localIdColumn,
            string externalIdColumn)
        {
            migrationBuilder.Sql(
                $"""
                WITH "classes"("Classification") AS (
                    VALUES ('{Matched}'), ('{Orphaned}'), ('{ExternalIdOnAnotherRow}'), ('{ColumnDiffers}')
                ),
                "classified" AS (
                    SELECT link."Id" AS "LinkRowId",
                           link."{localIdColumn}" AS "LocalRowId",
                           CASE
                               WHEN local."Id" IS NULL THEN '{Orphaned}'
                               WHEN EXISTS (SELECT 1 FROM "{localTable}" other
                                            WHERE other."BusinessId" = link."BusinessId"
                                              AND other."{externalIdColumn}" = link."ExternalId"
                                              AND other."Id" <> link."{localIdColumn}")
                                   THEN '{ExternalIdOnAnotherRow}'
                               WHEN local."{externalIdColumn}" IS NULL
                                    OR local."{externalIdColumn}" <> link."ExternalId"
                                   THEN '{ColumnDiffers}'
                               ELSE '{Matched}'
                           END AS "Classification"
                    FROM "{linkTable}" link
                    LEFT JOIN "{localTable}" local
                        ON local."BusinessId" = link."BusinessId"
                       AND local."Id" = link."{localIdColumn}"
                )
                INSERT INTO "{ClassificationTable}"
                    ("LinkTable", "Classification", "RowCount", "RowIds", "ClassifiedAtUtc")
                SELECT '{linkTable}',
                       cl."Classification",
                       (SELECT COUNT(*) FROM "classified" c
                        WHERE c."Classification" = cl."Classification"),
                       (SELECT group_concat(c."LinkRowId" || '/' || c."LocalRowId", ' ')
                        FROM "classified" c
                        WHERE c."Classification" = cl."Classification"
                          AND cl."Classification" <> '{Matched}'),
                       strftime('%Y-%m-%d %H:%M:%S', 'now')
                FROM "classes" cl;
                """,
                suppressTransaction: true);
        }

        /// <summary>
        /// Recreates one link table and its four indexes exactly as <c>AddProviderIdentityLinks</c>
        /// created them, so <c>Down</c> restores that schema rather than an approximation of it. A
        /// deliberate copy and not a shared helper: an applied migration is a historical statement and
        /// must keep meaning what it meant, which it cannot if two migrations share the code.
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
