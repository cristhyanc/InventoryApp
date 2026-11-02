using Inventory.Domain.PriceCatalogue;
using Inventory.Domain.Suppliers;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// How observed supplier prices are stored and read back (issue #925, parent #817): the observation
/// itself, the latest one per listing, and the rule that a failed or missing check never removes the
/// last good price.
///
/// <para>Relational SQLite over the real migration history, because everything under test here is the
/// database's: the check constraints, the restricted references, the stored precision and the
/// ordering a "latest per listing" read depends on. AGENTS.md § Database and migrations requires a
/// relational test exactly where SQL, constraints and ordering matter, and an InMemory provider has
/// none of them.</para>
///
/// <para>No business is seeded and no scope is taken: every table here is a platform table, so an
/// unrestricted context is doing setup rather than standing in for a request.</para>
/// </summary>
public class SupplierPriceObservationStorageTests
{
    private static readonly DateTime RecordedAt = new(2026, 10, 28, 5, 0, 0, DateTimeKind.Utc);

    #region An observation is stored as it was observed

    /// <summary>
    /// Every fact the issue asks for, written and read back: both prices, the currency, the basis as
    /// observed, the supplier's own GST rate, the promotion and its end, the stock answer, the
    /// observed instant, the run and reference it came from, and the source link.
    /// </summary>
    [Fact]
    public async Task An_observation_is_stored_with_every_fact_it_was_observed_with()
    {
        await using var fixture = await ObservationFixture.CreateAsync();

        var runId = await fixture.AddRunAsync(PriceCollectorRunStatus.Succeeded);
        await fixture.AddObservationAsync(
            fixture.ListingId,
            runId,
            packPrice: 26.4000m,
            regularPackPrice: 32.0000m,
            basis: SupplierPriceBasis.InclusiveOfGst,
            supplierGstRate: 0.1000m,
            isPromotion: true,
            promotionEndsAtUtc: RecordedAt.AddDays(3),
            stockStatus: SupplierStockStatus.InStock,
            observedAtUtc: RecordedAt.AddHours(-2),
            sourceReference: "varnmill:coles:1234",
            sourceUrl: "https://example.test/product/1234");

        var stored = Assert.Single(await fixture.ObservationsAsync());

        Assert.Equal(fixture.ListingId, stored.SupplierListingId);
        Assert.Equal(runId, stored.PriceCollectorRunId);
        Assert.Equal(26.4000m, stored.PackPrice);
        Assert.Equal(32.0000m, stored.RegularPackPrice);
        Assert.Equal(SupplierPriceObservationPolicy.Currency, stored.Currency);
        Assert.Equal(SupplierPriceBasis.InclusiveOfGst, stored.PriceBasis);
        Assert.Equal(0.1000m, stored.SupplierGstRate);
        Assert.True(stored.IsPromotion);
        Assert.Equal(RecordedAt.AddDays(3), stored.PromotionEndsAtUtc);
        Assert.Equal(DateTimeKind.Utc, stored.PromotionEndsAtUtc!.Value.Kind);
        Assert.Equal(SupplierStockStatus.InStock, stored.StockStatus);
        Assert.Equal(RecordedAt.AddHours(-2), stored.ObservedAtUtc);
        Assert.Equal(DateTimeKind.Utc, stored.ObservedAtUtc.Kind);
        Assert.Equal("varnmill:coles:1234", stored.SourceReference);
        Assert.Equal("https://example.test/product/1234", stored.SourceUrl);
    }

    /// <summary>
    /// An ordinary, non-promotional reading of a supplier that publishes no GST rate: the unknown
    /// values are stored as unknown rather than as zeros, which is what lets the comparison show the
    /// offer without comparing it.
    /// </summary>
    [Fact]
    public async Task An_observation_keeps_its_unknowns_unknown()
    {
        await using var fixture = await ObservationFixture.CreateAsync();

        await fixture.AddObservationAsync(
            fixture.ListingId,
            await fixture.AddRunAsync(PriceCollectorRunStatus.Succeeded),
            packPrice: 22.80m,
            basis: SupplierPriceBasis.ExclusiveOfGst,
            stockStatus: SupplierStockStatus.Unknown);

        var stored = Assert.Single(await fixture.ObservationsAsync());

        Assert.Null(stored.RegularPackPrice);
        Assert.Null(stored.SupplierGstRate);
        Assert.Null(stored.PromotionEndsAtUtc);
        Assert.Null(stored.SourceReference);
        Assert.Null(stored.SourceUrl);
        Assert.False(stored.IsPromotion);
        Assert.Equal(SupplierStockStatus.Unknown, stored.StockStatus);
    }

    /// <summary>
    /// A run records what it accounted for, including a partial one - and the counts are its own,
    /// never inferred from how many observations happen to cite it.
    /// </summary>
    [Fact]
    public async Task A_collector_run_records_its_outcome_and_its_counts()
    {
        await using var fixture = await ObservationFixture.CreateAsync();

        var runId = await fixture.AddRunAsync(
            PriceCollectorRunStatus.PartiallySucceeded,
            listingsChecked: 40,
            observationsRecorded: 37,
            failures: 3);

        var stored = Assert.Single(await fixture.RunsAsync());

        Assert.Equal(runId, stored.Id);
        Assert.Equal(PriceCollectorRunStatus.PartiallySucceeded, stored.Status);
        Assert.Equal(RecordedAt, stored.StartedAtUtc);
        Assert.Equal(RecordedAt.AddMinutes(20), stored.CompletedAtUtc);
        Assert.Equal(DateTimeKind.Utc, stored.CompletedAtUtc!.Value.Kind);
        Assert.Equal(40, stored.ListingsCheckedCount);
        Assert.Equal(37, stored.ObservationsRecordedCount);
        Assert.Equal(3, stored.FailureCount);
    }

    #endregion

    #region The latest observation per listing

    /// <summary>
    /// The read the comparison is built on: one row per listing, the newest by observed instant. It
    /// is over two listings and several observations each, because "latest" is only meaningful when
    /// there is an older row it has to beat and a neighbour it must not borrow from.
    /// </summary>
    [Fact]
    public async Task The_latest_observation_of_each_listing_is_its_newest_by_observed_instant()
    {
        await using var fixture = await ObservationFixture.CreateAsync();

        var secondListing = await fixture.AddListingAsync("SKU-2");
        var runId = await fixture.AddRunAsync(PriceCollectorRunStatus.Succeeded);

        await fixture.AddObservationAsync(
            fixture.ListingId,
            runId,
            packPrice: 30.00m,
            observedAtUtc: RecordedAt.AddDays(-2));
        var newestOfFirst = await fixture.AddObservationAsync(
            fixture.ListingId,
            runId,
            packPrice: 26.40m,
            observedAtUtc: RecordedAt.AddHours(-1));
        await fixture.AddObservationAsync(
            fixture.ListingId,
            runId,
            packPrice: 28.00m,
            observedAtUtc: RecordedAt.AddDays(-1));
        var newestOfSecond = await fixture.AddObservationAsync(
            secondListing,
            runId,
            packPrice: 24.00m,
            observedAtUtc: RecordedAt.AddHours(-3));

        var latest = await fixture.LatestObservationPerListingAsync();

        Assert.Equal(
            [(fixture.ListingId, newestOfFirst, 26.40m), (secondListing, newestOfSecond, 24.00m)],
            latest);
    }

    /// <summary>
    /// Two observations of one listing at the same instant - a run that read a page twice - are
    /// broken by the later row, so "latest" has exactly one answer rather than whichever row the
    /// database happened to return first.
    /// </summary>
    [Fact]
    public async Task Two_observations_at_one_instant_are_broken_by_the_later_row()
    {
        await using var fixture = await ObservationFixture.CreateAsync();

        var runId = await fixture.AddRunAsync(PriceCollectorRunStatus.Succeeded);

        await fixture.AddObservationAsync(
            fixture.ListingId,
            runId,
            packPrice: 30.00m,
            observedAtUtc: RecordedAt);
        var later = await fixture.AddObservationAsync(
            fixture.ListingId,
            runId,
            packPrice: 26.40m,
            observedAtUtc: RecordedAt);

        var latest = Assert.Single(await fixture.LatestObservationPerListingAsync());

        Assert.Equal((fixture.ListingId, later, 26.40m), latest);
    }

    #endregion

    #region A failed or missing check keeps the last good price

    /// <summary>
    /// The issue's own criterion. A listing holds its latest accepted observation and its last
    /// successful check; a later run then fails and records nothing at all, and the listing is
    /// <strong>unchanged</strong> - the last good price is still there, with the instant it was read
    /// at and the instant it last moved.
    /// </summary>
    [Fact]
    public async Task A_failed_run_that_records_nothing_leaves_the_last_good_price_in_place()
    {
        await using var fixture = await ObservationFixture.CreateAsync();

        var goodRun = await fixture.AddRunAsync(PriceCollectorRunStatus.Succeeded);
        var goodObservation = await fixture.AddObservationAsync(
            fixture.ListingId,
            goodRun,
            packPrice: 26.40m,
            observedAtUtc: RecordedAt.AddHours(-1));

        await fixture.RecordAcceptedCheckAsync(
            fixture.ListingId,
            goodObservation,
            checkedAtUtc: RecordedAt.AddHours(-1),
            priceChangedAtUtc: RecordedAt.AddHours(-1));

        // The failed run: recorded, counted, and carrying no observation of its own.
        await fixture.AddRunAsync(
            PriceCollectorRunStatus.Failed,
            startedAtUtc: RecordedAt.AddHours(2),
            listingsChecked: 1,
            observationsRecorded: 0,
            failures: 1);

        var listing = await fixture.ListingAsync(fixture.ListingId);

        Assert.Equal(goodObservation, listing.LatestObservationId);
        Assert.Equal(RecordedAt.AddHours(-1), listing.LastCheckedAtUtc);
        Assert.Equal(RecordedAt.AddHours(-1), listing.LastPriceChangeAtUtc);
        Assert.Equal(26.40m, (await fixture.ObservationsAsync()).Single().PackPrice);
    }

    /// <summary>
    /// And the observation a listing is pointing at cannot be deleted from under it: the pointer
    /// restricts, so "the last good price" cannot be removed by tidying the price history.
    /// </summary>
    [Fact]
    public async Task The_observation_a_listing_points_at_cannot_be_deleted()
    {
        await using var fixture = await ObservationFixture.CreateAsync();

        var runId = await fixture.AddRunAsync(PriceCollectorRunStatus.Succeeded);
        var observationId = await fixture.AddObservationAsync(fixture.ListingId, runId, 26.40m);
        await fixture.RecordAcceptedCheckAsync(
            fixture.ListingId,
            observationId,
            checkedAtUtc: RecordedAt,
            priceChangedAtUtc: RecordedAt);

        await Assert.ThrowsAsync<DbUpdateException>(
            () => fixture.DeleteObservationAsync(observationId));

        Assert.Single(await fixture.ObservationsAsync());
    }

    /// <summary>
    /// A successful re-read that finds the same price moves the check instant and leaves the last
    /// price change alone, which is why the two are separate columns.
    /// </summary>
    [Fact]
    public async Task A_re_read_at_the_same_price_moves_the_check_instant_and_not_the_price_change()
    {
        await using var fixture = await ObservationFixture.CreateAsync();

        var firstRun = await fixture.AddRunAsync(PriceCollectorRunStatus.Succeeded);
        var first = await fixture.AddObservationAsync(
            fixture.ListingId,
            firstRun,
            packPrice: 26.40m,
            observedAtUtc: RecordedAt.AddDays(-1));
        await fixture.RecordAcceptedCheckAsync(
            fixture.ListingId,
            first,
            checkedAtUtc: RecordedAt.AddDays(-1),
            priceChangedAtUtc: RecordedAt.AddDays(-1));

        var secondRun = await fixture.AddRunAsync(
            PriceCollectorRunStatus.Succeeded,
            startedAtUtc: RecordedAt);
        var second = await fixture.AddObservationAsync(
            fixture.ListingId,
            secondRun,
            packPrice: 26.40m,
            observedAtUtc: RecordedAt);
        await fixture.RecordAcceptedCheckAsync(
            fixture.ListingId,
            second,
            checkedAtUtc: RecordedAt,
            priceChangedAtUtc: RecordedAt.AddDays(-1));

        var listing = await fixture.ListingAsync(fixture.ListingId);

        Assert.Equal(second, listing.LatestObservationId);
        Assert.Equal(RecordedAt, listing.LastCheckedAtUtc);
        Assert.Equal(RecordedAt.AddDays(-1), listing.LastPriceChangeAtUtc);

        // Both readings are kept: the table is append-only, so the history is the evidence.
        Assert.Equal(2, (await fixture.ObservationsAsync()).Count);
    }

    #endregion

    #region The schema refuses what the policies refuse

    /// <summary>
    /// A pack price of zero divides into a unit price of zero, which compares as cheaper than every
    /// real offer. The database refuses it, not only <c>SupplierPriceObservationPolicy</c>.
    /// </summary>
    [Fact]
    public async Task A_pack_price_of_zero_is_refused()
    {
        await using var fixture = await ObservationFixture.CreateAsync();
        var runId = await fixture.AddRunAsync(PriceCollectorRunStatus.Succeeded);

        var refusal = await Assert.ThrowsAsync<DbUpdateException>(
            () => fixture.AddObservationAsync(fixture.ListingId, runId, packPrice: 0m));

        Assert.IsType<SqliteException>(refusal.InnerException);
        Assert.Empty(await fixture.ObservationsAsync());
    }

    /// <summary>A regular price of zero is refused for the same reason, while unknown stays allowed.</summary>
    [Fact]
    public async Task A_regular_price_of_zero_is_refused()
    {
        await using var fixture = await ObservationFixture.CreateAsync();
        var runId = await fixture.AddRunAsync(PriceCollectorRunStatus.Succeeded);

        await Assert.ThrowsAsync<DbUpdateException>(
            () => fixture.AddObservationAsync(
                fixture.ListingId,
                runId,
                packPrice: 26.40m,
                regularPackPrice: 0m));
    }

    /// <summary>
    /// A rate that is not a proportion - 10 where 0.10 belongs - would convert a price to eleven
    /// times itself, so the column refuses it.
    /// </summary>
    [Fact]
    public async Task A_gst_rate_that_is_not_a_proportion_is_refused()
    {
        await using var fixture = await ObservationFixture.CreateAsync();
        var runId = await fixture.AddRunAsync(PriceCollectorRunStatus.Succeeded);

        await Assert.ThrowsAsync<DbUpdateException>(
            () => fixture.AddObservationAsync(
                fixture.ListingId,
                runId,
                packPrice: 26.40m,
                supplierGstRate: 10m));
    }

    /// <summary>
    /// A promotion end date on a price that is not a promotion would silently withdraw a real offer
    /// from the comparison once that date passed.
    /// </summary>
    [Fact]
    public async Task A_promotion_end_date_without_a_promotion_is_refused()
    {
        await using var fixture = await ObservationFixture.CreateAsync();
        var runId = await fixture.AddRunAsync(PriceCollectorRunStatus.Succeeded);

        await Assert.ThrowsAsync<DbUpdateException>(
            () => fixture.AddObservationAsync(
                fixture.ListingId,
                runId,
                packPrice: 26.40m,
                isPromotion: false,
                promotionEndsAtUtc: RecordedAt.AddDays(1)));
    }

    /// <summary>
    /// Any currency but AUD is refused on arrival rather than compared as though it were dollars: a
    /// second currency is a decision about conversion rates nobody has made.
    /// </summary>
    [Fact]
    public async Task A_price_in_another_currency_is_refused()
    {
        await using var fixture = await ObservationFixture.CreateAsync();
        var runId = await fixture.AddRunAsync(PriceCollectorRunStatus.Succeeded);

        await Assert.ThrowsAsync<DbUpdateException>(
            () => fixture.AddObservationAsync(
                fixture.ListingId,
                runId,
                packPrice: 26.40m,
                currency: "NZD"));
    }

    /// <summary>A negative count describes nothing a run did.</summary>
    [Fact]
    public async Task A_negative_run_count_is_refused()
    {
        await using var fixture = await ObservationFixture.CreateAsync();

        await Assert.ThrowsAsync<DbUpdateException>(
            () => fixture.AddRunAsync(PriceCollectorRunStatus.Failed, failures: -1));
    }

    /// <summary>
    /// A listing that records a successful check without holding an observation, or a price change
    /// with no observation at all, cannot say how old its price is - and each half of that rule is the
    /// database's as well as <c>SupplierListingPriceStatePolicy</c>'s.
    /// </summary>
    [Fact]
    public async Task A_listing_cannot_record_a_check_without_an_observation()
    {
        await using var fixture = await ObservationFixture.CreateAsync();

        await Assert.ThrowsAsync<DbUpdateException>(
            () => fixture.RecordAcceptedCheckAsync(
                fixture.ListingId,
                latestObservationId: null,
                checkedAtUtc: RecordedAt,
                priceChangedAtUtc: null));

        await Assert.ThrowsAsync<DbUpdateException>(
            () => fixture.RecordAcceptedCheckAsync(
                fixture.ListingId,
                latestObservationId: null,
                checkedAtUtc: null,
                priceChangedAtUtc: RecordedAt));
    }

    /// <summary>
    /// Provenance that can disappear is not provenance: neither the listing an observation is a price
    /// of nor the run that read it can be deleted from under it.
    /// </summary>
    [Fact]
    public async Task A_listing_or_a_run_with_observations_cannot_be_deleted()
    {
        await using var fixture = await ObservationFixture.CreateAsync();
        var runId = await fixture.AddRunAsync(PriceCollectorRunStatus.Succeeded);
        await fixture.AddObservationAsync(fixture.ListingId, runId, 26.40m);

        await Assert.ThrowsAsync<DbUpdateException>(
            () => fixture.DeleteListingAsync(fixture.ListingId));
        await Assert.ThrowsAsync<DbUpdateException>(() => fixture.DeleteRunAsync(runId));

        Assert.Single(await fixture.ObservationsAsync());
    }

    /// <summary>
    /// A freshness limit of zero would read as "never stale" or "always stale" depending on who
    /// resolved it, so the supplier master record refuses it - in the database, beside
    /// <c>SupplierPriceFreshnessPolicy</c>.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(SupplierPriceFreshnessPolicy.MaxLimitHours + 1)]
    public async Task A_freshness_limit_outside_the_accepted_range_is_refused(int limitHours)
    {
        await using var fixture = await ObservationFixture.CreateAsync();

        await Assert.ThrowsAsync<DbUpdateException>(
            () => fixture.SetFreshnessLimitAsync(limitHours));
    }

    #endregion

    #region The stored facts are what the comparison needs

    /// <summary>
    /// The storage and the policy meet: the stored listing, its supplier master record's freshness
    /// limit and basis, and the latest observation of each listing are read into
    /// <c>CheapestUnitOfferPolicy</c>'s own candidate shape, and the answer is the cheaper supplier
    /// per can.
    ///
    /// <para>It is here rather than in the Domain tests because the claim is about the
    /// <strong>schema</strong>: everything that comparison needs is stored, and the per-supplier
    /// freshness limit in particular comes from a column rather than from code that names a supplier.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_stored_rows_are_everything_the_comparison_needs()
    {
        await using var fixture = await ObservationFixture.CreateAsync();

        var cheaperSupplier = await fixture.AddSupplierPresetAsync("Beta Grocer", pricesIncludeGst: false);
        var cheaperListing = await fixture.AddListingAsync("SKU-2", supplierPresetId: cheaperSupplier);
        var runId = await fixture.AddRunAsync(PriceCollectorRunStatus.Succeeded);

        await fixture.ConfirmListingAsync(fixture.ListingId, unitsPerPack: 24);
        await fixture.ConfirmListingAsync(cheaperListing, unitsPerPack: 24);

        await fixture.AddObservationAsync(
            fixture.ListingId,
            runId,
            packPrice: 26.40m,
            basis: SupplierPriceBasis.InclusiveOfGst,
            observedAtUtc: RecordedAt.AddHours(-1));
        await fixture.AddObservationAsync(
            cheaperListing,
            runId,
            packPrice: 22.80m,
            basis: SupplierPriceBasis.ExclusiveOfGst,
            observedAtUtc: RecordedAt.AddHours(-1));

        var candidates = await fixture.CandidateOffersAsync();

        var comparison = CheapestUnitOfferPolicy.Compare(
            candidates,
            CheapestUnitOfferPolicy.StandardGstRate,
            CheapestUnitOfferPolicy.BasisFor(businessIsGstRegistered: true),
            RecordedAt);

        Assert.Equal(0.95m, comparison.CheapestUnitPrice);
        Assert.Equal(cheaperListing, Assert.Single(comparison.Cheapest).SupplierListingId);
    }

    /// <summary>
    /// The shipped supplier master records carry the owner's freshness limits as data: BIG W and
    /// Kmart are checked once a week (owner decision, 1 November 2026) and hold 174 hours, and every
    /// other record holds the daily 30. It is
    /// asserted here because "stored per supplier master record, not hard-coded per supplier name" is
    /// the constraint this column exists for.
    /// </summary>
    [Fact]
    public async Task The_shipped_supplier_records_carry_their_own_freshness_limits()
    {
        await using var fixture = await ObservationFixture.CreateAsync();

        var limits = await fixture.ShippedFreshnessLimitsAsync();

        string[] weekly = ["BIG W", "KMART"];
        Assert.All(weekly, name => Assert.Equal(SupplierPriceFreshnessPolicy.WeeklyCheckLimitHours, limits[name]));
        Assert.All(
            limits.Where(limit => !weekly.Contains(limit.Key)),
            limit => Assert.Equal(SupplierPriceFreshnessPolicy.DailyCheckLimitHours, limit.Value));
    }

    #endregion

    /// <summary>
    /// A migrated SQLite database with one supplier master record, one master product and one listing
    /// of it - the smallest world in which an observed price means anything.
    /// </summary>
    private sealed class ObservationFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<AppDbContext> _options;

        private ObservationFixture(SqliteConnection connection, DbContextOptions<AppDbContext> options)
        {
            _connection = connection;
            _options = options;
        }

        public int SupplierPresetId { get; private set; }

        public long MasterProductId { get; private set; }

        public long ListingId { get; private set; }

        public static async Task<ObservationFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            var fixture = new ObservationFixture(
                connection,
                new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);

            await using var setup = TestAppDbContext.Unrestricted(fixture._options);
            await setup.Database.MigrateAsync();

            // A name none of the nine shipped presets holds, so these tests are about this listing
            // rather than about the seed. Inclusive prices, like the supermarkets'.
            fixture.SupplierPresetId = await fixture.AddSupplierPresetAsync(
                "Alpha Grocer",
                pricesIncludeGst: true);
            fixture.MasterProductId = await fixture.AddMasterProductAsync();
            fixture.ListingId = await fixture.AddListingAsync("SKU-1");

            return fixture;
        }

        public async Task<int> AddSupplierPresetAsync(string name, bool pricesIncludeGst)
        {
            await using var db = TestAppDbContext.Unrestricted(_options);

            var preset = new SupplierPreset
            {
                Name = name,
                NormalisedName = SupplierPresetPolicy.NormalisedName(name),
                IsEnabled = true,
                ServesNationwide = true,
                PricesIncludeGst = pricesIncludeGst,
                CreatedAtUtc = RecordedAt,
                UpdatedAtUtc = RecordedAt,
            };

            db.SupplierPresets.Add(preset);
            await db.SaveChangesAsync();

            return preset.Id;
        }

        public async Task<long> AddMasterProductAsync()
        {
            await using var db = TestAppDbContext.Unrestricted(_options);

            var master = new MasterProduct
            {
                Brand = "Coca-Cola",
                Name = "Classic",
                SizeValue = 375m,
                SizeUnit = MasterProductSizeUnit.Millilitre,
                NormalisedKey = MasterProductPolicy.NormalisedKey(
                    "Coca-Cola",
                    "Classic",
                    375m,
                    MasterProductSizeUnit.Millilitre),
                CreatedAtUtc = RecordedAt,
                UpdatedAtUtc = RecordedAt,
            };

            db.MasterProducts.Add(master);
            await db.SaveChangesAsync();

            return master.Id;
        }

        public async Task<long> AddListingAsync(string supplierProductId, int? supplierPresetId = null)
        {
            await using var db = TestAppDbContext.Unrestricted(_options);

            var listing = new SupplierListing
            {
                SupplierPresetId = supplierPresetId ?? SupplierPresetId,
                MasterProductId = MasterProductId,
                SupplierProductId = supplierProductId,
                MatchStatus = SupplierListingMatchStatus.Proposed,
                CreatedAtUtc = RecordedAt,
                UpdatedAtUtc = RecordedAt,
            };

            db.SupplierListings.Add(listing);
            await db.SaveChangesAsync();

            return listing.Id;
        }

        /// <summary>
        /// Confirms a listing's match and records its pack size - the state a confirmed, comparable
        /// listing is in. Deciding a match is #928's work, so the state is arranged directly.
        /// </summary>
        public async Task ConfirmListingAsync(long listingId, int unitsPerPack)
        {
            await using var db = TestAppDbContext.Unrestricted(_options);

            var listing = await db.SupplierListings.SingleAsync(row => row.Id == listingId);
            listing.MatchStatus = SupplierListingMatchStatus.Confirmed;
            listing.UnitsPerPack = unitsPerPack;
            listing.UpdatedAtUtc = RecordedAt;

            await db.SaveChangesAsync();
        }

        public async Task<long> AddRunAsync(
            PriceCollectorRunStatus status,
            DateTime? startedAtUtc = null,
            int listingsChecked = 1,
            int observationsRecorded = 1,
            int failures = 0)
        {
            await using var db = TestAppDbContext.Unrestricted(_options);

            var started = startedAtUtc ?? RecordedAt;
            var run = new PriceCollectorRun
            {
                StartedAtUtc = started,
                CompletedAtUtc = PriceCollectorRunPolicy.IsFinished(status)
                    ? started.AddMinutes(20)
                    : null,
                Status = status,
                ListingsCheckedCount = listingsChecked,
                ObservationsRecordedCount = observationsRecorded,
                FailureCount = failures,
            };

            db.PriceCollectorRuns.Add(run);
            await db.SaveChangesAsync();

            return run.Id;
        }

        public async Task<long> AddObservationAsync(
            long supplierListingId,
            long priceCollectorRunId,
            decimal packPrice,
            decimal? regularPackPrice = null,
            string? currency = null,
            SupplierPriceBasis basis = SupplierPriceBasis.InclusiveOfGst,
            decimal? supplierGstRate = null,
            bool isPromotion = false,
            DateTime? promotionEndsAtUtc = null,
            SupplierStockStatus stockStatus = SupplierStockStatus.InStock,
            DateTime? observedAtUtc = null,
            string? sourceReference = null,
            string? sourceUrl = null)
        {
            await using var db = TestAppDbContext.Unrestricted(_options);

            var observation = new SupplierPriceObservation
            {
                SupplierListingId = supplierListingId,
                PriceCollectorRunId = priceCollectorRunId,
                PackPrice = packPrice,
                RegularPackPrice = regularPackPrice,
                Currency = currency ?? SupplierPriceObservationPolicy.Currency,
                PriceBasis = basis,
                SupplierGstRate = supplierGstRate,
                IsPromotion = isPromotion,
                PromotionEndsAtUtc = promotionEndsAtUtc,
                StockStatus = stockStatus,
                ObservedAtUtc = observedAtUtc ?? RecordedAt,
                SourceReference = sourceReference,
                SourceUrl = sourceUrl,
                CreatedAtUtc = RecordedAt,
            };

            db.SupplierPriceObservations.Add(observation);
            await db.SaveChangesAsync();

            return observation.Id;
        }

        /// <summary>
        /// Records an accepted check on a listing, which is what the import of #937 will do: the
        /// latest observation, the successful check instant and - when the price moved - the change
        /// instant.
        /// </summary>
        public async Task RecordAcceptedCheckAsync(
            long listingId,
            long? latestObservationId,
            DateTime? checkedAtUtc,
            DateTime? priceChangedAtUtc)
        {
            await using var db = TestAppDbContext.Unrestricted(_options);

            var listing = await db.SupplierListings.SingleAsync(row => row.Id == listingId);
            listing.LatestObservationId = latestObservationId;
            listing.LastCheckedAtUtc = checkedAtUtc;
            listing.LastPriceChangeAtUtc = priceChangedAtUtc;

            await db.SaveChangesAsync();
        }

        public async Task SetFreshnessLimitAsync(int limitHours)
        {
            await using var db = TestAppDbContext.Unrestricted(_options);

            var preset = await db.SupplierPresets.SingleAsync(row => row.Id == SupplierPresetId);
            preset.PriceCheckFreshnessHours = limitHours;

            await db.SaveChangesAsync();
        }

        public async Task<SupplierListing> ListingAsync(long listingId)
        {
            await using var db = TestAppDbContext.Unrestricted(_options);

            return await db.SupplierListings.AsNoTracking().SingleAsync(row => row.Id == listingId);
        }

        public async Task<IReadOnlyList<SupplierPriceObservation>> ObservationsAsync()
        {
            await using var db = TestAppDbContext.Unrestricted(_options);

            return await db.SupplierPriceObservations
                .AsNoTracking()
                .OrderBy(observation => observation.Id)
                .ToListAsync();
        }

        public async Task<IReadOnlyList<PriceCollectorRun>> RunsAsync()
        {
            await using var db = TestAppDbContext.Unrestricted(_options);

            return await db.PriceCollectorRuns.AsNoTracking().OrderBy(run => run.Id).ToListAsync();
        }

        /// <summary>
        /// The newest observation of each listing, by observed instant and then by row - the read a
        /// price summary is built from, expressed in SQL rather than in memory because the ordering
        /// is what is under test.
        ///
        /// <para>It is written as "no newer observation of this listing exists" rather than as a
        /// grouped <c>First()</c>, which the SQLite provider does not translate. The tie-break is part
        /// of the predicate, so one listing has exactly one latest row rather than whichever one the
        /// database happens to return first.</para>
        /// </summary>
        public async Task<IReadOnlyList<(long ListingId, long ObservationId, decimal PackPrice)>>
            LatestObservationPerListingAsync()
        {
            await using var db = TestAppDbContext.Unrestricted(_options);

            var latest = await db.SupplierPriceObservations
                .AsNoTracking()
                .Where(observation => !db.SupplierPriceObservations.Any(newer =>
                    newer.SupplierListingId == observation.SupplierListingId
                        && (newer.ObservedAtUtc > observation.ObservedAtUtc
                            || (newer.ObservedAtUtc == observation.ObservedAtUtc
                                && newer.Id > observation.Id))))
                .OrderBy(observation => observation.SupplierListingId)
                .Select(observation => new
                {
                    observation.SupplierListingId,
                    observation.Id,
                    observation.PackPrice,
                })
                .ToListAsync();

            return latest
                .Select(observation =>
                    (observation.SupplierListingId, observation.Id, observation.PackPrice))
                .ToList();
        }

        /// <summary>
        /// Every confirmed listing with its latest observation and its supplier master record's own
        /// basis and freshness limit, in <c>CheapestUnitOfferPolicy</c>'s candidate shape. The join
        /// the summary endpoint of #930 will make, written here to prove the schema carries all of it.
        /// </summary>
        public async Task<IReadOnlyList<SupplierUnitOffer>> CandidateOffersAsync()
        {
            await using var db = TestAppDbContext.Unrestricted(_options);

            var rows = await db.SupplierPriceObservations
                .AsNoTracking()
                .Join(
                    db.SupplierListings.AsNoTracking(),
                    observation => observation.SupplierListingId,
                    listing => listing.Id,
                    (observation, listing) => new { observation, listing })
                .Join(
                    db.SupplierPresets.AsNoTracking(),
                    pair => pair.listing.SupplierPresetId,
                    preset => preset.Id,
                    (pair, preset) => new { pair.observation, pair.listing, preset })
                .ToListAsync();

            return rows
                .Select(row => new SupplierUnitOffer(
                    row.listing.Id,
                    row.listing.MatchStatus == SupplierListingMatchStatus.Confirmed,
                    row.listing.UnitsPerPack,
                    row.observation.PackPrice,
                    row.observation.PriceBasis,
                    row.observation.SupplierGstRate,
                    row.observation.StockStatus,
                    row.observation.PromotionEndsAtUtc,
                    row.observation.ObservedAtUtc,
                    row.preset.PriceCheckFreshnessHours))
                .ToList();
        }

        /// <summary>The freshness limit of each supplier master record this application shipped.</summary>
        public async Task<IReadOnlyDictionary<string, int>> ShippedFreshnessLimitsAsync()
        {
            await using var db = TestAppDbContext.Unrestricted(_options);

            return await db.SupplierPresets
                .AsNoTracking()
                .Where(preset => preset.Id != SupplierPresetId)
                .ToDictionaryAsync(
                    preset => preset.NormalisedName,
                    preset => preset.PriceCheckFreshnessHours);
        }

        public async Task DeleteObservationAsync(long observationId)
        {
            await using var db = TestAppDbContext.Unrestricted(_options);

            db.SupplierPriceObservations.Remove(
                await db.SupplierPriceObservations.SingleAsync(row => row.Id == observationId));

            await db.SaveChangesAsync();
        }

        public async Task DeleteListingAsync(long listingId)
        {
            await using var db = TestAppDbContext.Unrestricted(_options);

            db.SupplierListings.Remove(
                await db.SupplierListings.SingleAsync(row => row.Id == listingId));

            await db.SaveChangesAsync();
        }

        public async Task DeleteRunAsync(long runId)
        {
            await using var db = TestAppDbContext.Unrestricted(_options);

            db.PriceCollectorRuns.Remove(
                await db.PriceCollectorRuns.SingleAsync(row => row.Id == runId));

            await db.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
    }
}
