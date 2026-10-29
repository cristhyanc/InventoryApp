using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <summary>
    /// Creates the storage the collector results endpoint needs (issue #937, parent #817): the two
    /// platform tables that hold a submitted row's outcome and a reading that was not accepted, the
    /// identity columns a listing is checked against, the instant a complete search found no listing,
    /// the per-outcome counts a closed run records, and the two columns a search's own answer carries.
    ///
    /// <para><strong>Additive, and it writes nothing.</strong> Two new tables and eleven new columns;
    /// no existing column is dropped or rewritten, no business, product, purchase, cost, inventory or
    /// report row is read or changed, and nothing is backfilled. Every listing that exists keeps a
    /// null variant, size and not-found instant - this migration invents no identity for a listing
    /// recorded before one was read, exactly as <c>AddPriceCollectorWorkItems</c> invented no expiry
    /// for a run issued before lifetimes existed - and every run keeps five zero counts, which is what
    /// a run that never reported says.</para>
    ///
    /// <para><strong>Two more platform tables, on the terms the eight before them state.</strong> A
    /// submitted reading of a supplier's shelf and the decision made about it are facts about that
    /// shelf and about no business, so neither table carries a <c>BusinessId</c>, an index on one or a
    /// query filter. Nothing here is tenant-owned, so nothing here is stamped or filtered.</para>
    ///
    /// <para><strong>The idempotency key is a unique index, not a <c>WHERE</c> clause.</strong>
    /// <c>(PriceCollectorRunId, PriceCollectorWorkItemId, SupplierProductId)</c> on
    /// <c>PriceCollectorResultRows</c> is what makes re-submitting a row answer with the first
    /// attempt's outcome instead of storing a second reading: two concurrent submissions of one key
    /// would both pass a clause. The supplier product id is in the key deliberately, because one
    /// search legitimately returns a 24-pack and a 30-pack and accepting one must not suppress the
    /// other. <c>SupplierListingPendingObservations</c> is unique on its listing for the matching
    /// reason: "the latest pending observation, replacing the previous one" is one row per listing,
    /// and an index is what holds that rather than a delete-then-insert nothing checks.</para>
    ///
    /// <para><strong>SQLite rebuilds <c>SupplierListings</c> and <c>PriceCollectorRuns</c> here</strong>,
    /// because adding a check constraint to an existing table is a table rebuild in SQLite. Both are
    /// safe to rebuild: the listings table has had no writer since #923 created it and the runs table
    /// only the work list of #924, and the rebuild copies every row column for column. The upgrade
    /// test asserts a pre-existing listing and a pre-existing run survive it unchanged.</para>
    ///
    /// <para><strong>The constraints are the facts a review depends on.</strong> A held reading may
    /// later be approved into a price, so it is held to an accepted price's own rules - a positive
    /// pack price, AUD, a GST rate that is a proportion, a promotion end date only on a promotion -
    /// because storing one this application would refuse as a price only moves the refusal to the
    /// moment somebody approves it. A listing's individual size travels with its unit and is positive,
    /// on the terms its barcode travels with its scope and a master product's size is positive: a
    /// number with no unit compares with nothing, and a size of zero is a denominator. Every reference
    /// <strong>restricts</strong>, except a pending reading's own listing, which cascades: a reading
    /// waiting on a listing has no meaning without it.</para>
    ///
    /// <para><c>Down</c> drops the two tables, the two constraints on the listings, the one on the
    /// runs, and the eleven columns. That is lossless for everything that existed before this
    /// migration: no listing loses its price state, its match status or its owner, no run loses a
    /// count or a status, and the only thing that cannot survive a downgrade is what this release
    /// collected - which a release that predates the results endpoint has nothing to do with.</para>
    /// </summary>
    /// <inheritdoc />
    public partial class AddPriceCollectorResults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastNotFoundAtUtc",
                table: "SupplierListings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SizeUnit",
                table: "SupplierListings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "SizeValue",
                table: "SupplierListings",
                type: "decimal(18,3)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Variant",
                table: "SupplierListings",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CompleteSearchCount",
                table: "PriceCollectorRuns",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "FailedSearchCount",
                table: "PriceCollectorRuns",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "NoMatchesSearchCount",
                table: "PriceCollectorRuns",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "NotSearchedCount",
                table: "PriceCollectorRuns",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TruncatedSearchCount",
                table: "PriceCollectorRuns",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "FailureReason",
                table: "PriceCollectorWorkItems",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SubmittedRowCount",
                table: "PriceCollectorWorkItems",
                type: "INTEGER",
                nullable: true);

            // SQLite cannot add a check constraint to an existing table, so both tables that gain
            // one are rebuilt here - create, copy every column, drop, rename, recreate the indexes.
            // It is written out rather than left to migrationBuilder.AddCheckConstraint because this
            // migration was authored by hand and carries no generated target model for the SQLite
            // provider's own rewriter to read; the shape is the one that rewriter produces, and the
            // shape 20260907123300_RemoveProductMapped already uses here. The copy is total: every
            // listing and every run keeps every value it had, which the upgrade test asserts column
            // for column.
            migrationBuilder.Sql("PRAGMA foreign_keys = OFF;", suppressTransaction: true);

            migrationBuilder.Sql("""
                CREATE TABLE "ef_temp_SupplierListings" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_SupplierListings" PRIMARY KEY AUTOINCREMENT,
                    "Barcode" TEXT NULL,
                    "BarcodeScope" INTEGER NULL,
                    "CreatedAtUtc" TEXT NOT NULL,
                    "LastCheckedAtUtc" TEXT NULL,
                    "LastNotFoundAtUtc" TEXT NULL,
                    "LastPriceChangeAtUtc" TEXT NULL,
                    "LatestObservationId" INTEGER NULL,
                    "MasterProductId" INTEGER NULL,
                    "MatchStatus" INTEGER NOT NULL,
                    "OriginWaitingKey" TEXT NULL,
                    "SizeUnit" INTEGER NULL,
                    "SizeValue" decimal(18,3) NULL,
                    "SupplierPresetId" INTEGER NOT NULL,
                    "SupplierProductId" TEXT NOT NULL,
                    "Title" TEXT NULL,
                    "UnitsPerPack" INTEGER NULL,
                    "UpdatedAtUtc" TEXT NOT NULL,
                    "Url" TEXT NULL,
                    "Variant" TEXT NULL,
                    "WaitingKey" TEXT NULL,
                    CONSTRAINT "CK_SupplierListings_BarcodeAndScopeTogether" CHECK (("Barcode" IS NULL AND "BarcodeScope" IS NULL) OR ("Barcode" IS NOT NULL AND "BarcodeScope" IS NOT NULL)),
                    CONSTRAINT "CK_SupplierListings_OneOwner" CHECK (("MasterProductId" IS NOT NULL AND "WaitingKey" IS NULL) OR ("MasterProductId" IS NULL AND "WaitingKey" IS NOT NULL)),
                    CONSTRAINT "CK_SupplierListings_PriceStateConsistent" CHECK ((("LatestObservationId" IS NULL AND "LastCheckedAtUtc" IS NULL AND "LastPriceChangeAtUtc" IS NULL) OR ("LatestObservationId" IS NOT NULL AND "LastCheckedAtUtc" IS NOT NULL))),
                    CONSTRAINT "CK_SupplierListings_SizeAndUnitTogether" CHECK (("SizeValue" IS NULL AND "SizeUnit" IS NULL) OR ("SizeValue" IS NOT NULL AND "SizeUnit" IS NOT NULL)),
                    CONSTRAINT "CK_SupplierListings_SizeValuePositive" CHECK ("SizeValue" IS NULL OR "SizeValue" > 0),
                    CONSTRAINT "CK_SupplierListings_UnitsPerPackPositive" CHECK ("UnitsPerPack" IS NULL OR "UnitsPerPack" > 0),
                    CONSTRAINT "FK_SupplierListings_MasterProducts_MasterProductId" FOREIGN KEY ("MasterProductId") REFERENCES "MasterProducts" ("Id") ON DELETE RESTRICT,
                    CONSTRAINT "FK_SupplierListings_SupplierPresets_SupplierPresetId" FOREIGN KEY ("SupplierPresetId") REFERENCES "SupplierPresets" ("Id") ON DELETE RESTRICT,
                    CONSTRAINT "FK_SupplierListings_SupplierPriceObservations_LatestObservationId" FOREIGN KEY ("LatestObservationId") REFERENCES "SupplierPriceObservations" ("Id") ON DELETE RESTRICT
                );
                """);

            migrationBuilder.Sql("""
                INSERT INTO "ef_temp_SupplierListings" ("Id", "Barcode", "BarcodeScope", "CreatedAtUtc", "LastCheckedAtUtc", "LastNotFoundAtUtc", "LastPriceChangeAtUtc", "LatestObservationId", "MasterProductId", "MatchStatus", "OriginWaitingKey", "SizeUnit", "SizeValue", "SupplierPresetId", "SupplierProductId", "Title", "UnitsPerPack", "UpdatedAtUtc", "Url", "Variant", "WaitingKey")
                SELECT "Id", "Barcode", "BarcodeScope", "CreatedAtUtc", "LastCheckedAtUtc", "LastNotFoundAtUtc", "LastPriceChangeAtUtc", "LatestObservationId", "MasterProductId", "MatchStatus", "OriginWaitingKey", "SizeUnit", "SizeValue", "SupplierPresetId", "SupplierProductId", "Title", "UnitsPerPack", "UpdatedAtUtc", "Url", "Variant", "WaitingKey"
                FROM "SupplierListings";
                """);

            migrationBuilder.Sql("""DROP TABLE "SupplierListings";""");
            migrationBuilder.Sql("""ALTER TABLE "ef_temp_SupplierListings" RENAME TO "SupplierListings";""");

            migrationBuilder.Sql("""
                CREATE TABLE "ef_temp_PriceCollectorRuns" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_PriceCollectorRuns" PRIMARY KEY AUTOINCREMENT,
                    "CompleteSearchCount" INTEGER NOT NULL,
                    "CompletedAtUtc" TEXT NULL,
                    "ExpiresAtUtc" TEXT NULL,
                    "FailedSearchCount" INTEGER NOT NULL,
                    "FailureCount" INTEGER NOT NULL,
                    "ListingsCheckedCount" INTEGER NOT NULL,
                    "NoMatchesSearchCount" INTEGER NOT NULL,
                    "NotSearchedCount" INTEGER NOT NULL,
                    "ObservationsRecordedCount" INTEGER NOT NULL,
                    "StartedAtUtc" TEXT NOT NULL,
                    "Status" INTEGER NOT NULL,
                    "TruncatedSearchCount" INTEGER NOT NULL,
                    CONSTRAINT "CK_PriceCollectorRuns_CountsNotNegative" CHECK ("ListingsCheckedCount" >= 0 AND "ObservationsRecordedCount" >= 0 AND "FailureCount" >= 0),
                    CONSTRAINT "CK_PriceCollectorRuns_SearchCountsNotNegative" CHECK ("CompleteSearchCount" >= 0 AND "TruncatedSearchCount" >= 0 AND "NoMatchesSearchCount" >= 0 AND "FailedSearchCount" >= 0 AND "NotSearchedCount" >= 0)
                );
                """);

            migrationBuilder.Sql("""
                INSERT INTO "ef_temp_PriceCollectorRuns" ("Id", "CompleteSearchCount", "CompletedAtUtc", "ExpiresAtUtc", "FailedSearchCount", "FailureCount", "ListingsCheckedCount", "NoMatchesSearchCount", "NotSearchedCount", "ObservationsRecordedCount", "StartedAtUtc", "Status", "TruncatedSearchCount")
                SELECT "Id", "CompleteSearchCount", "CompletedAtUtc", "ExpiresAtUtc", "FailedSearchCount", "FailureCount", "ListingsCheckedCount", "NoMatchesSearchCount", "NotSearchedCount", "ObservationsRecordedCount", "StartedAtUtc", "Status", "TruncatedSearchCount"
                FROM "PriceCollectorRuns";
                """);

            migrationBuilder.Sql("""DROP TABLE "PriceCollectorRuns";""");
            migrationBuilder.Sql("""ALTER TABLE "ef_temp_PriceCollectorRuns" RENAME TO "PriceCollectorRuns";""");

            migrationBuilder.Sql("PRAGMA foreign_keys = ON;", suppressTransaction: true);

            migrationBuilder.CreateIndex(
                name: "IX_SupplierListings_LatestObservationId",
                table: "SupplierListings",
                column: "LatestObservationId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierListings_MasterProductId",
                table: "SupplierListings",
                column: "MasterProductId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierListings_WaitingKey",
                table: "SupplierListings",
                column: "WaitingKey");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierListings_SupplierPresetId_SupplierProductId",
                table: "SupplierListings",
                columns: new[] { "SupplierPresetId", "SupplierProductId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PriceCollectorRuns_StartedAtUtc",
                table: "PriceCollectorRuns",
                column: "StartedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_PriceCollectorRuns_Status",
                table: "PriceCollectorRuns",
                column: "Status");

            migrationBuilder.CreateTable(
                name: "PriceCollectorResultRows",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PriceCollectorRunId = table.Column<long>(type: "INTEGER", nullable: false),
                    PriceCollectorWorkItemId = table.Column<long>(type: "INTEGER", nullable: false),
                    SupplierProductId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Outcome = table.Column<int>(type: "INTEGER", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    SupplierListingId = table.Column<long>(type: "INTEGER", nullable: true),
                    SupplierPriceObservationId = table.Column<long>(type: "INTEGER", nullable: true),
                    SubmittedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PriceCollectorResultRows", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PriceCollectorResultRows_PriceCollectorRuns_PriceCollectorRunId",
                        column: x => x.PriceCollectorRunId,
                        principalTable: "PriceCollectorRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PriceCollectorResultRows_PriceCollectorWorkItems_PriceCollectorWorkItemId",
                        column: x => x.PriceCollectorWorkItemId,
                        principalTable: "PriceCollectorWorkItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PriceCollectorResultRows_SupplierListings_SupplierListingId",
                        column: x => x.SupplierListingId,
                        principalTable: "SupplierListings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PriceCollectorResultRows_SupplierPriceObservations_SupplierPriceObservationId",
                        column: x => x.SupplierPriceObservationId,
                        principalTable: "SupplierPriceObservations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SupplierListingPendingObservations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SupplierListingId = table.Column<long>(type: "INTEGER", nullable: false),
                    PriceCollectorRunId = table.Column<long>(type: "INTEGER", nullable: false),
                    PriceCollectorWorkItemId = table.Column<long>(type: "INTEGER", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    PackPrice = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    RegularPackPrice = table.Column<decimal>(type: "decimal(18,4)", nullable: true),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 3, nullable: false),
                    PriceBasis = table.Column<int>(type: "INTEGER", nullable: false),
                    SupplierGstRate = table.Column<decimal>(type: "decimal(5,4)", nullable: true),
                    IsPromotion = table.Column<bool>(type: "INTEGER", nullable: false),
                    PromotionEndsAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    StockStatus = table.Column<int>(type: "INTEGER", nullable: false),
                    ObservedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SourceReference = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    SourceUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    Variant = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    SizeValue = table.Column<decimal>(type: "decimal(18,3)", nullable: true),
                    SizeUnit = table.Column<int>(type: "INTEGER", nullable: true),
                    Barcode = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    BarcodeScope = table.Column<int>(type: "INTEGER", nullable: true),
                    UnitsPerPack = table.Column<int>(type: "INTEGER", nullable: true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    RecordedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierListingPendingObservations", x => x.Id);
                    table.CheckConstraint("CK_SupplierListingPendingObservations_BarcodeAndScopeTogether", "(\"Barcode\" IS NULL AND \"BarcodeScope\" IS NULL) OR (\"Barcode\" IS NOT NULL AND \"BarcodeScope\" IS NOT NULL)");
                    table.CheckConstraint("CK_SupplierListingPendingObservations_CurrencyIsAud", "\"Currency\" = 'AUD'");
                    table.CheckConstraint("CK_SupplierListingPendingObservations_GstRateIsAProportion", "\"SupplierGstRate\" IS NULL OR (\"SupplierGstRate\" >= 0 AND \"SupplierGstRate\" <= 1)");
                    table.CheckConstraint("CK_SupplierListingPendingObservations_PackPricePositive", "\"PackPrice\" > 0");
                    table.CheckConstraint("CK_SupplierListingPendingObservations_PromotionEndNeedsPromotion", "\"PromotionEndsAtUtc\" IS NULL OR \"IsPromotion\" = 1");
                    table.CheckConstraint("CK_SupplierListingPendingObservations_RegularPackPricePositive", "\"RegularPackPrice\" IS NULL OR \"RegularPackPrice\" > 0");
                    table.CheckConstraint("CK_SupplierListingPendingObservations_SizeAndUnitTogether", "(\"SizeValue\" IS NULL AND \"SizeUnit\" IS NULL) OR (\"SizeValue\" IS NOT NULL AND \"SizeUnit\" IS NOT NULL)");
                    table.CheckConstraint("CK_SupplierListingPendingObservations_UnitsPerPackPositive", "\"UnitsPerPack\" IS NULL OR \"UnitsPerPack\" > 0");
                    table.ForeignKey(
                        name: "FK_SupplierListingPendingObservations_PriceCollectorRuns_PriceCollectorRunId",
                        column: x => x.PriceCollectorRunId,
                        principalTable: "PriceCollectorRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierListingPendingObservations_PriceCollectorWorkItems_PriceCollectorWorkItemId",
                        column: x => x.PriceCollectorWorkItemId,
                        principalTable: "PriceCollectorWorkItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierListingPendingObservations_SupplierListings_SupplierListingId",
                        column: x => x.SupplierListingId,
                        principalTable: "SupplierListings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PriceCollectorResultRows_PriceCollectorRunId_PriceCollectorWorkItemId_SupplierProductId",
                table: "PriceCollectorResultRows",
                columns: new[] { "PriceCollectorRunId", "PriceCollectorWorkItemId", "SupplierProductId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PriceCollectorResultRows_PriceCollectorWorkItemId",
                table: "PriceCollectorResultRows",
                column: "PriceCollectorWorkItemId");

            migrationBuilder.CreateIndex(
                name: "IX_PriceCollectorResultRows_SupplierListingId",
                table: "PriceCollectorResultRows",
                column: "SupplierListingId");

            migrationBuilder.CreateIndex(
                name: "IX_PriceCollectorResultRows_SupplierPriceObservationId",
                table: "PriceCollectorResultRows",
                column: "SupplierPriceObservationId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierListingPendingObservations_PriceCollectorRunId",
                table: "SupplierListingPendingObservations",
                column: "PriceCollectorRunId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierListingPendingObservations_PriceCollectorWorkItemId",
                table: "SupplierListingPendingObservations",
                column: "PriceCollectorWorkItemId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierListingPendingObservations_SupplierListingId",
                table: "SupplierListingPendingObservations",
                column: "SupplierListingId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PriceCollectorResultRows");

            migrationBuilder.DropTable(
                name: "SupplierListingPendingObservations");

            // The same rebuild in reverse, and for the same reason: SQLite will not drop a column a
            // check constraint names. Both tables return to exactly the shape the migration before
            // this one left them in, and every row that predates this migration keeps every value
            // it had - what a downgrade loses is only what this release collected.
            migrationBuilder.Sql("PRAGMA foreign_keys = OFF;", suppressTransaction: true);

            migrationBuilder.Sql("""
                CREATE TABLE "ef_temp_SupplierListings" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_SupplierListings" PRIMARY KEY AUTOINCREMENT,
                    "Barcode" TEXT NULL,
                    "BarcodeScope" INTEGER NULL,
                    "CreatedAtUtc" TEXT NOT NULL,
                    "LastCheckedAtUtc" TEXT NULL,
                    "LastPriceChangeAtUtc" TEXT NULL,
                    "LatestObservationId" INTEGER NULL,
                    "MasterProductId" INTEGER NULL,
                    "MatchStatus" INTEGER NOT NULL,
                    "OriginWaitingKey" TEXT NULL,
                    "SupplierPresetId" INTEGER NOT NULL,
                    "SupplierProductId" TEXT NOT NULL,
                    "Title" TEXT NULL,
                    "UnitsPerPack" INTEGER NULL,
                    "UpdatedAtUtc" TEXT NOT NULL,
                    "Url" TEXT NULL,
                    "WaitingKey" TEXT NULL,
                    CONSTRAINT "CK_SupplierListings_BarcodeAndScopeTogether" CHECK (("Barcode" IS NULL AND "BarcodeScope" IS NULL) OR ("Barcode" IS NOT NULL AND "BarcodeScope" IS NOT NULL)),
                    CONSTRAINT "CK_SupplierListings_OneOwner" CHECK (("MasterProductId" IS NOT NULL AND "WaitingKey" IS NULL) OR ("MasterProductId" IS NULL AND "WaitingKey" IS NOT NULL)),
                    CONSTRAINT "CK_SupplierListings_PriceStateConsistent" CHECK ((("LatestObservationId" IS NULL AND "LastCheckedAtUtc" IS NULL AND "LastPriceChangeAtUtc" IS NULL) OR ("LatestObservationId" IS NOT NULL AND "LastCheckedAtUtc" IS NOT NULL))),
                    CONSTRAINT "CK_SupplierListings_UnitsPerPackPositive" CHECK ("UnitsPerPack" IS NULL OR "UnitsPerPack" > 0),
                    CONSTRAINT "FK_SupplierListings_MasterProducts_MasterProductId" FOREIGN KEY ("MasterProductId") REFERENCES "MasterProducts" ("Id") ON DELETE RESTRICT,
                    CONSTRAINT "FK_SupplierListings_SupplierPresets_SupplierPresetId" FOREIGN KEY ("SupplierPresetId") REFERENCES "SupplierPresets" ("Id") ON DELETE RESTRICT,
                    CONSTRAINT "FK_SupplierListings_SupplierPriceObservations_LatestObservationId" FOREIGN KEY ("LatestObservationId") REFERENCES "SupplierPriceObservations" ("Id") ON DELETE RESTRICT
                );
                """);

            migrationBuilder.Sql("""
                INSERT INTO "ef_temp_SupplierListings" ("Id", "Barcode", "BarcodeScope", "CreatedAtUtc", "LastCheckedAtUtc", "LastPriceChangeAtUtc", "LatestObservationId", "MasterProductId", "MatchStatus", "OriginWaitingKey", "SupplierPresetId", "SupplierProductId", "Title", "UnitsPerPack", "UpdatedAtUtc", "Url", "WaitingKey")
                SELECT "Id", "Barcode", "BarcodeScope", "CreatedAtUtc", "LastCheckedAtUtc", "LastPriceChangeAtUtc", "LatestObservationId", "MasterProductId", "MatchStatus", "OriginWaitingKey", "SupplierPresetId", "SupplierProductId", "Title", "UnitsPerPack", "UpdatedAtUtc", "Url", "WaitingKey"
                FROM "SupplierListings";
                """);

            migrationBuilder.Sql("""DROP TABLE "SupplierListings";""");
            migrationBuilder.Sql("""ALTER TABLE "ef_temp_SupplierListings" RENAME TO "SupplierListings";""");

            migrationBuilder.Sql("""
                CREATE TABLE "ef_temp_PriceCollectorRuns" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_PriceCollectorRuns" PRIMARY KEY AUTOINCREMENT,
                    "StartedAtUtc" TEXT NOT NULL,
                    "CompletedAtUtc" TEXT NULL,
                    "Status" INTEGER NOT NULL,
                    "ListingsCheckedCount" INTEGER NOT NULL,
                    "ObservationsRecordedCount" INTEGER NOT NULL,
                    "FailureCount" INTEGER NOT NULL,
                    "ExpiresAtUtc" TEXT NULL,
                    CONSTRAINT "CK_PriceCollectorRuns_CountsNotNegative" CHECK ("ListingsCheckedCount" >= 0 AND "ObservationsRecordedCount" >= 0 AND "FailureCount" >= 0)
                );
                """);

            migrationBuilder.Sql("""
                INSERT INTO "ef_temp_PriceCollectorRuns" ("Id", "StartedAtUtc", "CompletedAtUtc", "Status", "ListingsCheckedCount", "ObservationsRecordedCount", "FailureCount", "ExpiresAtUtc")
                SELECT "Id", "StartedAtUtc", "CompletedAtUtc", "Status", "ListingsCheckedCount", "ObservationsRecordedCount", "FailureCount", "ExpiresAtUtc"
                FROM "PriceCollectorRuns";
                """);

            migrationBuilder.Sql("""DROP TABLE "PriceCollectorRuns";""");
            migrationBuilder.Sql("""ALTER TABLE "ef_temp_PriceCollectorRuns" RENAME TO "PriceCollectorRuns";""");

            migrationBuilder.Sql("PRAGMA foreign_keys = ON;", suppressTransaction: true);

            migrationBuilder.CreateIndex(
                name: "IX_SupplierListings_LatestObservationId",
                table: "SupplierListings",
                column: "LatestObservationId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierListings_MasterProductId",
                table: "SupplierListings",
                column: "MasterProductId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierListings_WaitingKey",
                table: "SupplierListings",
                column: "WaitingKey");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierListings_SupplierPresetId_SupplierProductId",
                table: "SupplierListings",
                columns: new[] { "SupplierPresetId", "SupplierProductId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PriceCollectorRuns_StartedAtUtc",
                table: "PriceCollectorRuns",
                column: "StartedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_PriceCollectorRuns_Status",
                table: "PriceCollectorRuns",
                column: "Status");

            migrationBuilder.DropColumn(
                name: "FailureReason",
                table: "PriceCollectorWorkItems");

            migrationBuilder.DropColumn(
                name: "SubmittedRowCount",
                table: "PriceCollectorWorkItems");
        }
    }
}
