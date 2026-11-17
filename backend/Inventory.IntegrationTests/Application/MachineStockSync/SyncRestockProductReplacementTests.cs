using System.Text.Json;
using Inventory.Application.Machines;
using Inventory.Application.MachineStockSync;
using Inventory.Application.Nayax;
using Inventory.Domain.Nayax;
using Inventory.Domain.Providers;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using InventoryApi.Tests.Application.Nayax;
using InventoryApi.Tests.Application.Time;
using InventoryApi.Tests.Domain.Nayax;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Application.MachineStockSync;

/// <summary>
/// Nayax product replacements in Sync Restock (issue #1325), over relational SQLite and the real
/// <see cref="EfMachineStockEventStore"/>: a "Machine Product Changed" alert (Event 510) is imported
/// beside 501 alerts with the same deduplication, stored with both reported names, shown with
/// suggestions computed from the live machine read, refused by every apply path, resolvable only as
/// "already recorded manually", never re-matched, and counted as pending work like a 501.
///
/// <para>The alert is the owner's real, anonymised capture
/// (<see cref="NayaxProductChangedAlertFixture"/>), with only its machine and event ids set to this
/// fixture's.</para>
/// </summary>
public class SyncRestockProductReplacementTests
{
    private const long NayaxMachineId = 900;
    private const long LocalMachineId = 31;
    private const int BusinessId = 1;
    private const int Mdb = 58;

    private static readonly MachineIdentity Machine = new(LocalMachineId, NayaxMachineId);

    /// <summary>The captured alert's own instant (EventDateTimeGMT).</summary>
    private static readonly DateTime ReplacedAt = new(2026, 11, 15, 4, 33, 46, 723, DateTimeKind.Utc);

    /// <summary>A local product linked to the Nayax product the slot now reports.</summary>
    private const long LinkedIncomingId = 300;
    private const long NayaxIncomingId = 300;
    private const string LinkedIncomingName = "Red Bull Red Edition 250mL";

    /// <summary>A local product named exactly as the outgoing product.</summary>
    private const long OutgoingId = 301;
    private const string OutgoingName = "V Energy Drink Sugarfree Original 250ML";

    /// <summary>A local product named exactly as the incoming product, but not linked to the slot.</summary>
    private const long NamedIncomingId = 302;
    private const string IncomingName = "Redbull Red Edition";

    private static async Task<DbContextOptions<AppDbContext>> CreateDatabaseAsync(SqliteConnection connection)
    {
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using (var schema = TestAppDbContext.Unrestricted(options))
        {
            await schema.Database.EnsureCreatedAsync();
            schema.Businesses.Add(new Business
            {
                Id = BusinessId,
                Name = "Vending A",
                Status = BusinessStatus.Active,
                SalesProvider = ExternalProvider.Nayax,
                CreatedAtUtc = ReplacedAt.AddDays(-30),
            });
            schema.Machines.Add(new LocalMachine
            {
                BusinessId = BusinessId,
                Id = LocalMachineId,
                Name = "Avenue Hotel",
                ExternalMachineId = NayaxExternalId.From(NayaxMachineId),
            });
            await schema.SaveChangesAsync();
        }

        await using (var seed = Scoped(options))
        {
            seed.Products.AddRange(
                Product(LinkedIncomingId, LinkedIncomingName, NayaxExternalId.From(NayaxIncomingId)),
                Product(OutgoingId, OutgoingName, null),
                Product(NamedIncomingId, IncomingName, null));
            await seed.SaveChangesAsync();
        }

        return options;
    }

    private static Product Product(long id, string name, string? externalProductId) => new()
    {
        Id = id,
        Name = name,
        ExternalProductId = externalProductId,
        QuantityInStock = 40,
    };

    private static AppDbContext Scoped(DbContextOptions<AppDbContext> options) =>
        TestAppDbContext.For(options, BusinessId);

    private static EfMachineStockEventStore Store(AppDbContext db) =>
        new(db, TestCostingUseCases.RecordMovement(db), TestCostingUseCases.Rebuild(db));

    private static SyncMachineStockFromNayax Sync(AppDbContext db, INayaxLynxClient nayax)
    {
        var store = Store(db);
        return new SyncMachineStockFromNayax(
            nayax,
            store,
            TestMachineStockSyncUseCases.Rematch(store, new EfNayaxProductLinkStore(db), nayax),
            new FakeClock(ReplacedAt.AddHours(1)),
            new FakeNayaxConnectionStore("op", "token", NayaxConnectionStatus.Ready),
            new FakeMachineRestockSyncCoverageStore(),
            new FakeBusinessCalendar(ReplacedAt.AddHours(1)));
    }

    private static ApplyMachineStockSync Apply(AppDbContext db, INayaxLynxClient nayax)
    {
        var store = Store(db);
        return TestMachineStockSyncUseCases.Apply(
            store, new EfNayaxProductLinkStore(db), new RecordingMachineStockSyncLog(), nayax);
    }

    /// <summary>The captured 510 alert, re-addressed to this fixture's machine and event id.</summary>
    private static NayaxMachineAlert CapturedReplacement(long eventLogId = 1, DateTime? at = null, string? eventData = null)
    {
        var alert = JsonSerializer.Deserialize<NayaxMachineAlert>(NayaxProductChangedAlertFixture.Json)!;
        alert.MachineId = NayaxMachineId;
        alert.EventLogId = eventLogId;
        if (at is { } instant)
        {
            alert.EventDateTimeGmt = instant;
        }

        if (eventData is not null)
        {
            alert.EventData = eventData;
        }

        return alert;
    }

    private static NayaxMachineAlert StockAlert(long eventLogId, string eventData, DateTime at) => new()
    {
        EventLogId = eventLogId,
        MachineId = NayaxMachineId,
        EventCode = NayaxMachineAlertEventCodes.StockAdjustForMachine,
        EventDescription = "Stock Adjust for Machine",
        EventData = eventData,
        EventDateTimeGmt = at,
        EventDateTimeVmc = DateTime.SpecifyKind(at.AddHours(11), DateTimeKind.Unspecified),
    };

    private static NayaxMachineProduct Slot(
        string name = IncomingName, long nayaxProductId = NayaxIncomingId, int? par = 10, int? missing = 2) => new()
        {
            NayaxProductID = nayaxProductId,
            MachineID = NayaxMachineId,
            MDBCode = Mdb,
            ProductName = name,
            PAR = par,
            MissingStockByMDB = missing,
        };

    private static Mock<INayaxLynxClient> Nayax(
        List<NayaxMachineAlert> alerts, List<NayaxMachineProduct>? machineProducts = null)
    {
        var mock = new Mock<INayaxLynxClient>();
        mock.Setup(x => x.GetMachineLastAlertsAsync(NayaxMachineId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(alerts);
        mock.Setup(x => x.GetMachineProductsFreshAsync(NayaxMachineId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(machineProducts ?? [Slot()]);
        return mock;
    }

    #region Import

    [Fact]
    public async Task The_captured_alert_is_imported_with_both_names_and_no_match_or_quantity()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateDatabaseAsync(connection);

        await using (var db = Scoped(options))
        {
            var preview = await Sync(db, Nayax([CapturedReplacement()]).Object).Handle(Machine, CancellationToken.None);
            Assert.Equal(1, preview.NewEventCount);
        }

        await using var verify = Scoped(options);
        var stored = await verify.NayaxMachineStockEvents.SingleAsync();
        Assert.Equal(NayaxMachineAlertEventCodes.MachineProductChanged, stored.EventCode);
        Assert.Equal(NayaxProductChangedAlertFixture.EventData, stored.RawEventData);
        Assert.Contains("\"EventDescription\":\"Machine Product Changed\"", stored.RawSourceMetadata, StringComparison.Ordinal);
        Assert.Equal(Mdb, stored.ParsedMdb);
        Assert.Equal(IncomingName, stored.ParsedProductName);
        Assert.Equal(OutgoingName, stored.ParsedOutgoingProductName);
        Assert.Null(stored.ParsedQuantity);
        Assert.Null(stored.MatchedProductId);
        Assert.Equal(NayaxStockEventMatchStatus.NeedsReview, stored.MatchStatus);
        Assert.Null(stored.NeedsReviewReason);
        Assert.Equal(NayaxStockEventProcessingStatus.Unprocessed, stored.ProcessingStatus);
        Assert.Equal(ReplacedAt, stored.EventDateTimeGmt);
    }

    [Fact]
    public async Task A_repeated_sync_imports_nothing_twice_and_other_alert_codes_are_still_ignored()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateDatabaseAsync(connection);
        var other = CapturedReplacement(eventLogId: 3);
        other.EventCode = 100;
        var nayax = Nayax([CapturedReplacement(1), CapturedReplacement(1), other]);

        await using (var first = Scoped(options))
        {
            Assert.Equal(1, (await Sync(first, nayax.Object).Handle(Machine, CancellationToken.None)).NewEventCount);
        }

        await using (var second = Scoped(options))
        {
            var preview = await Sync(second, nayax.Object).Handle(Machine, CancellationToken.None);
            Assert.Equal(0, preview.NewEventCount);
            Assert.Single(preview.Events);
        }

        await using var verify = Scoped(options);
        Assert.Equal(1, await verify.NayaxMachineStockEvents.CountAsync());
    }

    [Fact]
    public async Task An_unparsable_replacement_is_kept_as_needs_review_with_its_raw_text_and_reason()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateDatabaseAsync(connection);
        const string garbled = "[operator] 'swapped the snacks around in MDB 58";

        await using (var db = Scoped(options))
        {
            var preview = await Sync(db, Nayax([CapturedReplacement(eventData: garbled)]).Object)
                .Handle(Machine, CancellationToken.None);

            var row = Assert.Single(preview.Events);
            Assert.Equal(NayaxMachineAlertEventCodes.MachineProductChanged, row.EventCode);
            Assert.Equal(NayaxStockEventMatchStatus.NeedsReview, row.MatchStatus);
            Assert.Contains("has replaced product", row.NeedsReviewReason, StringComparison.Ordinal);
            Assert.Null(row.ProductReplacement);
        }

        await using var verify = Scoped(options);
        var stored = await verify.NayaxMachineStockEvents.SingleAsync();
        Assert.Equal(garbled, stored.RawEventData);
        Assert.Null(stored.ParsedMdb);
        Assert.Null(stored.ParsedProductName);
        Assert.Null(stored.ParsedOutgoingProductName);
    }

    #endregion

    #region Suggestions

    [Fact]
    public async Task The_preview_suggests_the_live_slot_product_its_units_and_the_outgoing_product_by_name()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateDatabaseAsync(connection);
        await using var db = Scoped(options);

        var preview = await Sync(db, Nayax([CapturedReplacement()], [Slot(par: 10, missing: 3)]).Object)
            .Handle(Machine, CancellationToken.None);

        var row = Assert.Single(preview.Events);
        var replacement = Assert.IsType<NayaxProductReplacementPreviewDto>(row.ProductReplacement);
        Assert.Equal(OutgoingName, replacement.OutgoingProductName);
        Assert.Equal(IncomingName, replacement.IncomingProductName);

        // The slot still reports the incoming product and is linked, so the linked product wins
        // over the product that merely shares the name.
        Assert.Equal(LinkedIncomingId, replacement.SuggestedIncomingProductId);
        Assert.Equal(LinkedIncomingName, replacement.SuggestedIncomingProductName);
        Assert.Equal(NayaxReplacementSuggestionSource.LiveSlot, replacement.IncomingSuggestionSource);
        Assert.Equal(7, replacement.SuggestedIncomingQuantity);
        Assert.Null(replacement.SuggestedIncomingQuantityReason);

        Assert.Equal(OutgoingId, replacement.SuggestedOutgoingProductId);
        Assert.Equal(NayaxReplacementSuggestionSource.ExactName, replacement.OutgoingSuggestionSource);

        // Nothing about it is a refill.
        Assert.Null(row.ParsedQuantity);
        Assert.Null(row.MatchedProductId);
        Assert.False(row.IsPossibleDuplicate);
        Assert.Empty(preview.ProductImpacts);
    }

    [Fact]
    public async Task A_slot_that_changed_again_suggests_by_name_and_offers_no_quantity()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateDatabaseAsync(connection);
        await using var db = Scoped(options);

        var preview = await Sync(db, Nayax([CapturedReplacement()], [Slot(name: "Mother 500mL", nayaxProductId: 999)]).Object)
            .Handle(Machine, CancellationToken.None);

        var replacement = Assert.Single(preview.Events).ProductReplacement!;
        Assert.Equal(NamedIncomingId, replacement.SuggestedIncomingProductId);
        Assert.Equal(NayaxReplacementSuggestionSource.ExactName, replacement.IncomingSuggestionSource);
        Assert.Null(replacement.SuggestedIncomingQuantity);
        Assert.Contains("now reports 'Mother 500mL'", replacement.SuggestedIncomingQuantityReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_live_machine_read_still_lists_the_replacement_with_name_advice_and_the_reason()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateDatabaseAsync(connection);

        await using (var importDb = Scoped(options))
        {
            await Sync(importDb, Nayax([CapturedReplacement()]).Object).Handle(Machine, CancellationToken.None);
        }

        // A later sync imports nothing new, so the preview's own live read is the only machine read,
        // and Nayax fails it. The stored replacement must still be listed and resolvable.
        var nayax = Nayax([CapturedReplacement()]);
        nayax.Setup(x => x.GetMachineProductsFreshAsync(NayaxMachineId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Nayax is unavailable."));

        await using var db = Scoped(options);
        var preview = await Sync(db, nayax.Object).Handle(Machine, CancellationToken.None);

        var row = Assert.Single(preview.Events);
        var replacement = Assert.IsType<NayaxProductReplacementPreviewDto>(row.ProductReplacement);
        Assert.Equal(NamedIncomingId, replacement.SuggestedIncomingProductId);
        Assert.Equal(NayaxReplacementSuggestionSource.ExactName, replacement.IncomingSuggestionSource);
        Assert.Equal(OutgoingId, replacement.SuggestedOutgoingProductId);
        Assert.Null(replacement.SuggestedIncomingQuantity);
        Assert.Contains(
            NayaxProductReplacementSuggestionPolicy.SlotReadUnavailableReason,
            replacement.SuggestedIncomingQuantityReason,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cancelled_live_machine_read_is_not_swallowed()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateDatabaseAsync(connection);

        await using (var importDb = Scoped(options))
        {
            await Sync(importDb, Nayax([CapturedReplacement()]).Object).Handle(Machine, CancellationToken.None);
        }

        var nayax = Nayax([CapturedReplacement()]);
        nayax.Setup(x => x.GetMachineProductsFreshAsync(NayaxMachineId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        await using var db = Scoped(options);
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => Sync(db, nayax.Object).Handle(Machine, CancellationToken.None));
    }

    [Fact]
    public async Task A_name_tie_or_no_match_leaves_the_suggestion_empty_with_a_reason()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateDatabaseAsync(connection);
        await using (var seed = Scoped(options))
        {
            // A second active product with the outgoing name makes it a tie; retiring the only
            // product with the incoming name leaves that one with no active match.
            seed.Products.Add(Product(303, OutgoingName + " (old)", null));
            (await seed.Products.SingleAsync(p => p.Id == NamedIncomingId)).IsActive = false;
            await seed.SaveChangesAsync();
        }

        await using var db = Scoped(options);
        var preview = await Sync(db, Nayax([CapturedReplacement()], []).Object).Handle(Machine, CancellationToken.None);

        var replacement = Assert.Single(preview.Events).ProductReplacement!;
        Assert.Null(replacement.SuggestedOutgoingProductId);
        Assert.Contains("More than one active product", replacement.OutgoingSuggestionReason, StringComparison.Ordinal);
        Assert.Null(replacement.SuggestedIncomingProductId);
        Assert.Contains("No active product is named 'Redbull Red Edition'", replacement.IncomingSuggestionReason, StringComparison.Ordinal);
        Assert.Null(replacement.SuggestedIncomingQuantity);
        Assert.Contains("does not currently report MDB 58", replacement.SuggestedIncomingQuantityReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_newer_pending_event_for_the_mdb_withholds_the_quantity_and_a_near_501_is_flagged_on_both_rows()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateDatabaseAsync(connection);
        await using var db = Scoped(options);

        var preview = await Sync(db, Nayax([
                CapturedReplacement(),
                StockAlert(2, $"[operator] 'Adjusted Stock, Product MDB: {Mdb} | {IncomingName} | 8", ReplacedAt.AddHours(2)),
            ]).Object)
            .Handle(Machine, CancellationToken.None);

        Assert.Equal(2, preview.Events.Count);
        var replacementRow = preview.Events.Single(row => row.EventCode == NayaxMachineAlertEventCodes.MachineProductChanged);
        var adjustmentRow = preview.Events.Single(row => row.EventCode == NayaxMachineAlertEventCodes.StockAdjustForMachine);

        Assert.Null(replacementRow.ProductReplacement!.SuggestedIncomingQuantity);
        Assert.Contains("newer pending event", replacementRow.ProductReplacement.SuggestedIncomingQuantityReason, StringComparison.Ordinal);

        Assert.True(replacementRow.IsPossibleReplacementDoubleCount);
        Assert.Contains("1 pending stock adjustment for MDB 58", replacementRow.ReplacementDoubleCountNotes, StringComparison.Ordinal);
        Assert.True(adjustmentRow.IsPossibleReplacementDoubleCount);
        Assert.Contains("pending product replacement for MDB 58", adjustmentRow.ReplacementDoubleCountNotes, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_501_more_than_24_hours_away_or_on_another_mdb_is_not_flagged()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateDatabaseAsync(connection);
        await using var db = Scoped(options);

        var preview = await Sync(db, Nayax([
                CapturedReplacement(),
                StockAlert(2, $"Product MDB: {Mdb} | {IncomingName} | 8", ReplacedAt.AddHours(-25)),
                StockAlert(3, $"Product MDB: 7 | Coke | 8", ReplacedAt.AddHours(1)),
            ]).Object)
            .Handle(Machine, CancellationToken.None);

        Assert.All(preview.Events, row => Assert.False(row.IsPossibleReplacementDoubleCount));

        // The older 501 is not newer, and the other MDB is not this one, so the quantity stands.
        var replacement = preview.Events.Single(row => row.ProductReplacement is not null).ProductReplacement!;
        Assert.Equal(8, replacement.SuggestedIncomingQuantity);
    }

    [Fact]
    public async Task A_501_on_the_page_is_flagged_even_when_the_from_date_hides_the_replacement()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateDatabaseAsync(connection);
        await using var db = Scoped(options);

        var preview = await Sync(db, Nayax([
                CapturedReplacement(),
                StockAlert(2, $"Product MDB: {Mdb} | {IncomingName} | 8", ReplacedAt.AddHours(3)),
            ]).Object)
            .Handle(Machine, CancellationToken.None, fromDateGmt: ReplacedAt.AddHours(1));

        var adjustment = Assert.Single(preview.Events);
        Assert.Equal(NayaxMachineAlertEventCodes.StockAdjustForMachine, adjustment.EventCode);
        Assert.True(adjustment.IsPossibleReplacementDoubleCount);
    }

    #endregion

    #region Every reader of the event table

    [Fact]
    public async Task Apply_refuses_a_replacement_and_writes_nothing()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateDatabaseAsync(connection);
        var nayax = Nayax([CapturedReplacement()]);
        int eventId;

        await using (var db = Scoped(options))
        {
            eventId = Assert.Single((await Sync(db, nayax.Object).Handle(Machine, CancellationToken.None)).Events).Id;
        }

        await using (var db = Scoped(options))
        {
            var response = await Apply(db, nayax.Object).Handle(Machine, [eventId], CancellationToken.None);

            var result = Assert.Single(response.Results);
            Assert.Equal(NayaxStockEventApplyOutcome.NotApplicable, result.Outcome);
            Assert.Equal(NayaxMachineStockApplyPolicy.ProductReplacementMessage, result.Message);
            Assert.Null(result.StockAdjustmentId);
        }

        await using var verify = Scoped(options);
        var stored = await verify.NayaxMachineStockEvents.SingleAsync();
        Assert.Equal(NayaxStockEventProcessingStatus.Unprocessed, stored.ProcessingStatus);
        Assert.Null(stored.StockAdjustmentId);
        Assert.Empty(await verify.StockAdjustments.ToListAsync());
        Assert.All(await verify.Products.ToListAsync(), product => Assert.Equal(40, product.QuantityInStock));
    }

    [Fact]
    public async Task Applying_a_replacement_as_a_separate_restock_is_refused()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateDatabaseAsync(connection);
        int eventId;

        await using (var db = Scoped(options))
        {
            eventId = Assert.Single((await Sync(db, Nayax([CapturedReplacement()]).Object)
                .Handle(Machine, CancellationToken.None)).Events).Id;
        }

        await using (var db = Scoped(options))
        {
            var result = await new ResolveMachineStockDuplicate(Store(db), new RecordingMachineStockSyncLog())
                .Handle(Machine, eventId, NayaxDuplicateResolutionChoice.ApplyAsSeparateRestock, CancellationToken.None);

            Assert.Equal(NayaxStockEventApplyOutcome.NotApplicable, result.Outcome);
            Assert.Null(result.StockAdjustmentId);
        }

        await using var verify = Scoped(options);
        Assert.Empty(await verify.StockAdjustments.ToListAsync());
        Assert.Equal(NayaxDuplicateResolution.None, (await verify.NayaxMachineStockEvents.SingleAsync()).DuplicateResolution);
    }

    [Fact]
    public async Task Already_recorded_accepts_a_replacement_writes_no_stock_and_clears_it_from_the_list()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateDatabaseAsync(connection);
        var nayax = Nayax([CapturedReplacement()]);
        int eventId;

        await using (var db = Scoped(options))
        {
            eventId = Assert.Single((await Sync(db, nayax.Object).Handle(Machine, CancellationToken.None)).Events).Id;
        }

        await using (var db = Scoped(options))
        {
            var response = await new ResolveMachineStockEventsAsAlreadyRecorded(Store(db))
                .Handle(Machine, [eventId], CancellationToken.None);

            Assert.Equal(NayaxStockEventApplyOutcome.Reconciled, Assert.Single(response.Results).Outcome);
        }

        await using (var db = Scoped(options))
        {
            // Applying it afterwards is the settled replay, still with no movement.
            var replay = await Apply(db, nayax.Object).Handle(Machine, [eventId], CancellationToken.None);
            Assert.Equal(NayaxStockEventApplyOutcome.Reconciled, Assert.Single(replay.Results).Outcome);

            var preview = await Sync(db, nayax.Object).Handle(Machine, CancellationToken.None);
            Assert.Empty(preview.Events);
            Assert.Equal(1, preview.HiddenReconciledCount);
        }

        await using var verify = Scoped(options);
        var stored = await verify.NayaxMachineStockEvents.SingleAsync();
        Assert.Equal(NayaxDuplicateResolution.ReconciledManually, stored.DuplicateResolution);
        Assert.Null(stored.MatchedManualStockAdjustmentId);
        Assert.Empty(await verify.StockAdjustments.ToListAsync());
        Assert.All(await verify.Products.ToListAsync(), product => Assert.Equal(40, product.QuantityInStock));
    }

    [Fact]
    public async Task A_replacement_is_never_a_rematch_candidate_and_a_later_sync_leaves_it_unmatched()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateDatabaseAsync(connection);
        var nayax = Nayax([CapturedReplacement()]);

        await using (var db = Scoped(options))
        {
            await Sync(db, nayax.Object).Handle(Machine, CancellationToken.None);
        }

        await using (var db = Scoped(options))
        {
            Assert.Empty(await Store(db).FindRematchCandidatesAsync(NayaxMachineId, CancellationToken.None));
            await Sync(db, nayax.Object).Handle(Machine, CancellationToken.None);
        }

        await using var verify = Scoped(options);
        var stored = await verify.NayaxMachineStockEvents.SingleAsync();
        Assert.Null(stored.MatchedProductId);
        Assert.Equal(NayaxStockEventMatchStatus.NeedsReview, stored.MatchStatus);
        Assert.Null(stored.NeedsReviewReason);
    }

    [Fact]
    public async Task The_machine_ledger_counts_a_pending_replacement_as_an_unprocessed_stock_event()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateDatabaseAsync(connection);

        await using (var db = Scoped(options))
        {
            await Sync(db, Nayax([CapturedReplacement()]).Object).Handle(Machine, CancellationToken.None);
        }

        await using var read = Scoped(options);
        var facts = await new EfMachineStockLedgerStore(read).GetLedgerFactsAsync(Machine, [], CancellationToken.None);

        Assert.Equal(1, facts.PendingNayaxStockEventCount);
        Assert.Empty(facts.Refills);
    }

    #endregion
}
