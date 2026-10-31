using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <summary>
    /// Makes a purchase's supporting document optional (issue #976, child of #965): the four
    /// document columns on <c>Receipts</c> become nullable, and a <c>CHECK</c> constraint keeps them
    /// all present or all absent.
    ///
    /// <para><strong>No data changes.</strong> Every purchase that already exists keeps its document
    /// metadata exactly as it stands - the copy below moves all eighteen columns across verbatim -
    /// and nothing is nulled, blanked or backfilled. What changes is only what the schema
    /// <em>permits</em>: a new purchase may now record no document at all.</para>
    ///
    /// <para><strong>Why the constraint is in the database.</strong> A half-recorded document is not
    /// a state this application has, and it is not recoverable either: a row holding a stored name
    /// with no content type names bytes nothing can serve, and a row holding a content type with no
    /// stored name claims a document nothing can find. "This purchase has no document" is the answer
    /// the file endpoint's 404, the lists' "No document" and the attach-only-if-empty write all rest
    /// on, so it has to be unambiguous no matter what wrote the row.
    /// <c>Inventory.Domain.Purchases.PurchaseDocumentPolicy.IsMetadataConsistent</c> states the same
    /// rule for the application.</para>
    ///
    /// <para><strong>Why it is hand-written SQL.</strong> SQLite cannot drop a column's
    /// <c>NOT NULL</c> or add a table constraint in place: both need the table rebuilt, and the
    /// provider's model-driven rebuild needs a point-in-time model - which, for a <c>Down</c>, is the
    /// <em>preceding</em> migration's, and several migrations in this history are hand-written and
    /// deliberately record none (<c>AddSupplierOrderPartialCompletion</c> explains that at length).
    /// The statements below are exactly the sequence the SQLite provider generates for this change,
    /// written out so neither direction depends on any recorded model and whichever migration ends
    /// up in front of this one after a merge cannot decide whether rolling back works. The rebuilt
    /// table deliberately keeps the <c>DEFAULT 0</c> clauses the earlier additive migrations left on
    /// <c>BusinessId</c> and the four GST columns: removing a database default is its own reviewed
    /// change (<c>RemoveBusinessStatusActiveDatabaseDefault</c> is the precedent), not something this
    /// migration should do on the way past.</para>
    ///
    /// <para><strong>Rolling back needs every purchase to have a document.</strong> There is no value
    /// a restored <c>NOT NULL</c> column could hold for a purchase that has none - a blank stored
    /// name would be a row claiming a document that does not exist - so <c>Down</c> refuses, by name,
    /// rather than inventing one or dropping the purchase. Deleting the affected purchases first is a
    /// human decision about real business records, and it is deliberately not automated here.</para>
    /// </summary>
    /// <inheritdoc />
    public partial class AllowPurchaseWithoutDocument : Migration
    {
        /// <summary>
        /// The message <c>Down</c> aborts with when a purchase recorded without a document exists.
        /// It names the rule and says nothing about the business's data beyond that. Public so the
        /// migration test asserts the refusal by its own wording rather than by a copy of it; this
        /// assembly exposes no internals to the test projects.
        /// </summary>
        public const string DownBlockedMessage =
            "Cannot roll back AllowPurchaseWithoutDocument: at least one purchase has no document, " +
            "and the earlier schema requires one. Remove or document those purchases first.";

        /// <summary>
        /// Every column of <c>Receipts</c> as this migration found the table, named explicitly
        /// rather than copied with <c>SELECT *</c> so the copy does not depend on column order. The
        /// rebuilt table is defined from this same list, and
        /// <c>AllowPurchaseWithoutDocumentMigrationTests</c> asserts the column set across the
        /// upgrade, so a column some other change adds to <c>Receipts</c> before this merges cannot
        /// be quietly left behind by the rebuild.
        /// </summary>
        private const string AllColumns =
            @"""Id"", ""Title"", ""Notes"", ""TotalAmount"", ""PurchaseDate"", ""SupplierId"", " +
            @"""FileName"", ""StoredFileName"", ""ContentType"", ""FileSizeBytes"", ""CreatedAt"", " +
            @"""DeliveryCost"", ""PackageCost"", ""BusinessId"", ""DeliveryGstClassification"", " +
            @"""DeliveryGstClassificationSource"", ""PackageGstClassification"", " +
            @"""PackageGstClassificationSource""";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TABLE "ef_temp_Receipts" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_Receipts" PRIMARY KEY AUTOINCREMENT,
                    "Title" TEXT NOT NULL,
                    "Notes" TEXT NULL,
                    "TotalAmount" decimal(18,2) NULL,
                    "PurchaseDate" TEXT NOT NULL,
                    "SupplierId" INTEGER NULL,
                    "FileName" TEXT NULL,
                    "StoredFileName" TEXT NULL,
                    "ContentType" TEXT NULL,
                    "FileSizeBytes" INTEGER NULL,
                    "CreatedAt" TEXT NOT NULL,
                    "DeliveryCost" decimal(18,2) NULL,
                    "PackageCost" decimal(18,2) NULL,
                    "BusinessId" INTEGER NOT NULL DEFAULT 0,
                    "DeliveryGstClassification" INTEGER NOT NULL DEFAULT 0,
                    "DeliveryGstClassificationSource" INTEGER NOT NULL DEFAULT 0,
                    "PackageGstClassification" INTEGER NOT NULL DEFAULT 0,
                    "PackageGstClassificationSource" INTEGER NOT NULL DEFAULT 0,
                    CONSTRAINT "CK_Receipts_DocumentMetadataAllOrNothing" CHECK (("StoredFileName" IS NULL AND "ContentType" IS NULL AND "FileName" IS NULL AND "FileSizeBytes" IS NULL) OR ("StoredFileName" IS NOT NULL AND "ContentType" IS NOT NULL AND "FileName" IS NOT NULL AND "FileSizeBytes" IS NOT NULL)),
                    CONSTRAINT "FK_Receipts_Suppliers_SupplierId" FOREIGN KEY ("SupplierId") REFERENCES "Suppliers" ("Id") ON DELETE SET NULL
                );
                """);

            Rebuild(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Refused before anything is created or copied, so a rollback that cannot keep every
            // recorded purchase leaves the database exactly as it was.
            var guard = new SqliteRefusalGuard("AllowPurchaseWithoutDocumentDownGuard");
            guard.Create(migrationBuilder);
            guard.RefuseWhenNotZero(
                migrationBuilder,
                DownBlockedMessage,
                @"SELECT COUNT(*) FROM ""Receipts"" WHERE ""StoredFileName"" IS NULL");
            guard.Drop(migrationBuilder);

            migrationBuilder.Sql("""
                CREATE TABLE "ef_temp_Receipts" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_Receipts" PRIMARY KEY AUTOINCREMENT,
                    "Title" TEXT NOT NULL,
                    "Notes" TEXT NULL,
                    "TotalAmount" decimal(18,2) NULL,
                    "PurchaseDate" TEXT NOT NULL,
                    "SupplierId" INTEGER NULL,
                    "FileName" TEXT NOT NULL,
                    "StoredFileName" TEXT NOT NULL,
                    "ContentType" TEXT NOT NULL,
                    "FileSizeBytes" INTEGER NOT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    "DeliveryCost" decimal(18,2) NULL,
                    "PackageCost" decimal(18,2) NULL,
                    "BusinessId" INTEGER NOT NULL DEFAULT 0,
                    "DeliveryGstClassification" INTEGER NOT NULL DEFAULT 0,
                    "DeliveryGstClassificationSource" INTEGER NOT NULL DEFAULT 0,
                    "PackageGstClassification" INTEGER NOT NULL DEFAULT 0,
                    "PackageGstClassificationSource" INTEGER NOT NULL DEFAULT 0,
                    CONSTRAINT "FK_Receipts_Suppliers_SupplierId" FOREIGN KEY ("SupplierId") REFERENCES "Suppliers" ("Id") ON DELETE SET NULL
                );
                """);

            Rebuild(migrationBuilder);
        }

        /// <summary>
        /// Copies every purchase into the table the caller just created, swaps it in, and recreates
        /// the two indexes the dropped table carried - the second half of SQLite's own table-rebuild
        /// procedure, and the same statement order the provider emits.
        ///
        /// <para>The foreign keys are disabled around the swap, outside the migration's transaction
        /// because SQLite ignores the pragma inside one: <c>ReceiptItems</c> points at
        /// <c>Receipts</c>, so dropping the original would otherwise cascade the purchase lines of
        /// every purchase in the database away. The rename puts a table of the same name back in its
        /// place, so the lines still point at the purchases they were recorded on.</para>
        /// </summary>
        private static void Rebuild(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($"""
                INSERT INTO "ef_temp_Receipts" ({AllColumns})
                SELECT {AllColumns}
                FROM "Receipts";
                """);
            migrationBuilder.Sql("PRAGMA foreign_keys = 0;", suppressTransaction: true);
            migrationBuilder.Sql(@"DROP TABLE ""Receipts"";");
            migrationBuilder.Sql(@"ALTER TABLE ""ef_temp_Receipts"" RENAME TO ""Receipts"";");
            migrationBuilder.Sql("PRAGMA foreign_keys = 1;", suppressTransaction: true);
            migrationBuilder.Sql(@"CREATE INDEX ""IX_Receipts_BusinessId"" ON ""Receipts"" (""BusinessId"");");
            migrationBuilder.Sql(@"CREATE INDEX ""IX_Receipts_SupplierId"" ON ""Receipts"" (""SupplierId"");");
        }
    }
}
