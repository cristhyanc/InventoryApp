using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <summary>
    /// Creates the storage for observed supplier prices (issue #925, parent #817): two platform
    /// tables, the three columns a supplier listing keeps about its own price checks, and the
    /// per-supplier freshness limit on the supplier master record.
    ///
    /// <para><strong>Additive, and it writes one value.</strong> Two new tables, four new columns and
    /// two check constraints; no existing column is dropped or rewritten, no business, product,
    /// purchase, cost, inventory or report row is read or changed, and nothing is backfilled. The one
    /// data statement is the twice-weekly freshness limit on the shipped BIG W record - the owner's
    /// decision of 27 October 2026, which has to live in the data because the alternative is code
    /// that names a supplier (see <c>SupplierPreset.PriceCheckFreshnessHours</c>). It is written as an
    /// <c>UPDATE</c> over one normalised name, matching at most one row and possibly none, exactly as
    /// <c>AddSupplierPresetMasterFields</c> recorded that record's collection route: a row somebody
    /// renamed or removed is not a row this migration knows, and it keeps the column default rather
    /// than having a cadence guessed for it.</para>
    ///
    /// <para><strong>The default fails safe rather than closed.</strong> Every other supplier master
    /// record takes 30 hours, which is daily collection plus the six-hour margin - what the collector
    /// does unless somebody records otherwise. There is no "no limit" value: zero would read as
    /// "never stale" or "always stale" depending on who resolved it, and both are silently wrong, so
    /// the column is required and its range is checked.</para>
    ///
    /// <para><strong>SQLite rebuilds <c>SupplierListings</c> and <c>SupplierPresets</c> here</strong>,
    /// because adding a check constraint or a foreign key to an existing table is a table rebuild in
    /// SQLite. Both are safe to rebuild: the listings table has had no writer at all since #923
    /// created it, and the presets table is the platform's own small list, copied column for column by
    /// the rebuild. The upgrade test asserts the shipped preset rows survive it unchanged.</para>
    ///
    /// <para><strong>The constraints are the facts the comparison depends on.</strong> A pack price of
    /// zero divides into a unit price of zero and compares as cheaper than every real offer; a GST
    /// rate that is not a proportion - 10 where 0.10 belongs - converts a price to eleven times
    /// itself; a promotion end date on a price that is not a promotion silently withdraws a real
    /// offer; a currency that is not AUD would be compared as though it were dollars; and a listing
    /// that records a check without an observation, or a price change with no observation at all,
    /// cannot say how old its price is. Each is also a Domain rule
    /// (<c>SupplierPriceObservationPolicy</c>, <c>SupplierListingPriceStatePolicy</c>), and each is in
    /// the database because a stored row is not something an application-level check can be trusted to
    /// have prevented. Both references in the new tables <strong>restrict</strong>, and so does the
    /// listing's pointer at its latest observation: an observation whose listing or whose collector
    /// run had been deleted from under it would be a price nobody could place, and the observation a
    /// listing points at must not be removable while it points there.</para>
    ///
    /// <para><strong>Reversing it drops the two tables and the four columns.</strong> The BIG W
    /// freshness limit goes with the column that held it, which is what reversing an additive
    /// migration means; everything that record carried before - its name, website, enabled state,
    /// market, price basis, connector and both instants - is untouched, because this migration never
    /// wrote them.</para>
    /// </summary>
    /// <inheritdoc />
    public partial class AddSupplierPriceObservations : Migration
    {
        /// <summary>
        /// The one supplier master record whose prices are not collected daily, by the normalised
        /// name <c>SupplierPresetPolicy.NormalisedName</c> produces. BIG W is checked on Thursday and
        /// Sunday (the owner, 27 October 2026), so its longest legitimate gap is 96 hours.
        ///
        /// <para>It is named here, in a migration recording one decided value, and nowhere in
        /// application code: what the freshness rule reads is the column, never a supplier's
        /// identity.</para>
        /// </summary>
        private const string TwiceWeeklyPresetNormalisedName = "BIG W";

        /// <summary>
        /// BIG W's freshness limit in hours: its 96-hour gap plus the six-hour margin, which is
        /// <c>SupplierPriceFreshnessPolicy.TwiceWeeklyCheckLimitHours</c>. Written as a literal rather
        /// than read from that policy, because a migration records the value it wrote and must not
        /// change meaning when application code does - the rule
        /// <c>AddSupplierPresetMasterFields</c> follows for the connector numbers, and
        /// <c>AddSupplierPriceObservationsMigrationTests</c> asserts the two agree.
        /// </summary>
        private const int TwiceWeeklyFreshnessHours = 102;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PriceCheckFreshnessHours",
                table: "SupplierPresets",
                type: "INTEGER",
                nullable: false,
                defaultValue: 30);

            // The one row whose collection cadence the owner stated. At most one row matches, and a
            // database whose BIG W record was renamed or removed matches none and keeps the daily
            // default - a visible, editable value rather than a guess about a record this migration
            // no longer recognises.
            migrationBuilder.Sql(
                $"""
                UPDATE SupplierPresets
                SET PriceCheckFreshnessHours = {TwiceWeeklyFreshnessHours}
                WHERE NormalisedName = '{TwiceWeeklyPresetNormalisedName}';
                """);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastCheckedAtUtc",
                table: "SupplierListings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastPriceChangeAtUtc",
                table: "SupplierListings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "LatestObservationId",
                table: "SupplierListings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PriceCollectorRuns",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    StartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    ListingsCheckedCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ObservationsRecordedCount = table.Column<int>(type: "INTEGER", nullable: false),
                    FailureCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PriceCollectorRuns", x => x.Id);
                    table.CheckConstraint("CK_PriceCollectorRuns_CountsNotNegative", "\"ListingsCheckedCount\" >= 0 AND \"ObservationsRecordedCount\" >= 0 AND \"FailureCount\" >= 0");
                });

            migrationBuilder.CreateTable(
                name: "SupplierPriceObservations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SupplierListingId = table.Column<long>(type: "INTEGER", nullable: false),
                    PriceCollectorRunId = table.Column<long>(type: "INTEGER", nullable: false),
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
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierPriceObservations", x => x.Id);
                    table.CheckConstraint("CK_SupplierPriceObservations_CurrencyIsAud", "\"Currency\" = 'AUD'");
                    table.CheckConstraint("CK_SupplierPriceObservations_GstRateIsAProportion", "\"SupplierGstRate\" IS NULL OR (\"SupplierGstRate\" >= 0 AND \"SupplierGstRate\" <= 1)");
                    table.CheckConstraint("CK_SupplierPriceObservations_PackPricePositive", "\"PackPrice\" > 0");
                    table.CheckConstraint("CK_SupplierPriceObservations_PromotionEndNeedsPromotion", "\"PromotionEndsAtUtc\" IS NULL OR \"IsPromotion\" = 1");
                    table.CheckConstraint("CK_SupplierPriceObservations_RegularPackPricePositive", "\"RegularPackPrice\" IS NULL OR \"RegularPackPrice\" > 0");
                    table.ForeignKey(
                        name: "FK_SupplierPriceObservations_PriceCollectorRuns_PriceCollectorRunId",
                        column: x => x.PriceCollectorRunId,
                        principalTable: "PriceCollectorRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierPriceObservations_SupplierListings_SupplierListingId",
                        column: x => x.SupplierListingId,
                        principalTable: "SupplierListings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_SupplierPresets_PriceCheckFreshnessHoursInRange",
                table: "SupplierPresets",
                sql: "\"PriceCheckFreshnessHours\" > 0 AND \"PriceCheckFreshnessHours\" <= 720");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierListings_LatestObservationId",
                table: "SupplierListings",
                column: "LatestObservationId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SupplierListings_PriceStateConsistent",
                table: "SupplierListings",
                sql: "((\"LatestObservationId\" IS NULL AND \"LastCheckedAtUtc\" IS NULL AND \"LastPriceChangeAtUtc\" IS NULL) OR (\"LatestObservationId\" IS NOT NULL AND \"LastCheckedAtUtc\" IS NOT NULL))");

            migrationBuilder.CreateIndex(
                name: "IX_PriceCollectorRuns_StartedAtUtc",
                table: "PriceCollectorRuns",
                column: "StartedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierPriceObservations_PriceCollectorRunId",
                table: "SupplierPriceObservations",
                column: "PriceCollectorRunId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierPriceObservations_SupplierListingId_ObservedAtUtc",
                table: "SupplierPriceObservations",
                columns: new[] { "SupplierListingId", "ObservedAtUtc" });

            migrationBuilder.AddForeignKey(
                name: "FK_SupplierListings_SupplierPriceObservations_LatestObservationId",
                table: "SupplierListings",
                column: "LatestObservationId",
                principalTable: "SupplierPriceObservations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The listing's pointer at an observation goes first, so the observations table can be
            // dropped; the BIG W freshness limit goes with the column that held it, and nothing else
            // on that record is touched.
            migrationBuilder.DropForeignKey(
                name: "FK_SupplierListings_SupplierPriceObservations_LatestObservationId",
                table: "SupplierListings");

            migrationBuilder.DropTable(
                name: "SupplierPriceObservations");

            migrationBuilder.DropTable(
                name: "PriceCollectorRuns");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SupplierPresets_PriceCheckFreshnessHoursInRange",
                table: "SupplierPresets");

            migrationBuilder.DropIndex(
                name: "IX_SupplierListings_LatestObservationId",
                table: "SupplierListings");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SupplierListings_PriceStateConsistent",
                table: "SupplierListings");

            migrationBuilder.DropColumn(
                name: "PriceCheckFreshnessHours",
                table: "SupplierPresets");

            migrationBuilder.DropColumn(
                name: "LastCheckedAtUtc",
                table: "SupplierListings");

            migrationBuilder.DropColumn(
                name: "LastPriceChangeAtUtc",
                table: "SupplierListings");

            migrationBuilder.DropColumn(
                name: "LatestObservationId",
                table: "SupplierListings");
        }
    }
}
