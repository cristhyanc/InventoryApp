using Inventory.Application.NayaxReconciliation;
using Inventory.Domain.NayaxSqs;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Persistence;
using InventoryApi.Tests.Application.NayaxSqs;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Issue #1104's evidence store on a real SQLite database: what it records, what it refuses to
/// record twice, which state changes it applies, and what one business can reach of another's.
///
/// <para><strong>Relational SQLite rather than the EF InMemory provider, deliberately.</strong> Every
/// write here is one <c>BEGIN IMMEDIATE</c> transaction containing the authoritative re-read of the
/// sale, the transition decided from it and the append - and the InMemory provider has no transaction
/// at all, so it could not show that any of it is one operation (AGENTS.md § Database and
/// migrations).</para>
/// </summary>
public class EfNayaxSaleProviderEvidenceStoreTests
{
    private static readonly DateTime RunStarted = new(2026, 11, 7, 3, 0, 0, DateTimeKind.Utc);

    private static readonly Guid RunId = new("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task A_matching_comparison_verifies_the_sale_and_records_every_field_result()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();
        var saleId = await StoreQueueSaleAsync(fixture);

        await using var db = fixture.Context();
        var written = await new EfNayaxSaleProviderEvidenceStore(db)
            .RecordComparisonAsync(Comparison(saleId, Provider()), CancellationToken.None);

        Assert.True(written.Written);
        Assert.NotNull(written.EvidenceId);
        Assert.True(written.StateChanged);
        Assert.Equal(NayaxSqsVerification.Verified, written.State);

        await using var read = fixture.Context();
        var row = await read.NayaxSaleProviderEvidences.AsNoTracking().SingleAsync();

        Assert.Equal(NayaxSqsSaleFixture.BusinessA, row.BusinessId);
        Assert.Equal(saleId, row.NayaxSaleId);
        Assert.Equal(RunId, row.RunId);
        Assert.Equal(RunStarted, row.ObservedAtUtc);
        Assert.Equal(DateTimeKind.Utc, row.ObservedAtUtc.Kind);
        Assert.Equal(NayaxSaleProviderEvidenceKind.Comparison, row.Kind);
        Assert.Equal(NayaxSqsVerification.Verified, row.ResultingState);

        Assert.Equal(NayaxSaleFieldComparison.Match, row.MachineComparison);
        Assert.Equal(NayaxSaleFieldComparison.Match, row.AmountComparison);
        Assert.Equal(NayaxSaleFieldComparison.Match, row.InstantComparison);
        Assert.Equal(NayaxSaleFieldComparison.NotCompared, row.CurrencyComparison);
        Assert.Equal(NayaxSaleFieldComparison.Match, row.ProductComparison);

        // The absence columns are untouched, which is what keeps the two kinds readable apart.
        Assert.Null(row.ReadMachineId);
        Assert.Null(row.Bracketed);
        Assert.Null(row.OutsideReceiptGrace);
        Assert.Null(row.ReturnedItemCount);

        Assert.Equal(NayaxSqsVerification.Verified, await VerificationOfAsync(fixture, saleId));
    }

    /// <summary>
    /// The provider's values are stored <strong>exactly as received</strong>: not rounded, not
    /// trimmed, not upper-cased. The normalisations are the comparison's and are recorded as the
    /// per-field results beside them - a row that stored this application's own arithmetic would be
    /// evidence about the wrong thing.
    /// </summary>
    [Fact]
    public async Task The_provider_values_are_stored_exactly_as_received()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();
        var saleId = await StoreQueueSaleAsync(fixture);

        var provider = Provider() with
        {
            SettlementValue = 3.499m,
            CurrencyCode = "  aud ",
            ProductName = "  Fictional Spring Water 600ml  ",
            AuthorizationInstantUtc = NayaxSqsSaleFixture.SaleInstant.AddMilliseconds(300),
        };

        await using var db = fixture.Context();
        await new EfNayaxSaleProviderEvidenceStore(db)
            .RecordComparisonAsync(Comparison(saleId, provider), CancellationToken.None);

        await using var read = fixture.Context();
        var row = await read.NayaxSaleProviderEvidences.AsNoTracking().SingleAsync();

        Assert.Equal(3.499m, row.ProviderSettlementValue);
        Assert.Equal("  aud ", row.ProviderCurrencyCode);
        Assert.Equal("  Fictional Spring Water 600ml  ", row.ProviderProductName);
        Assert.Equal(NayaxSqsSaleFixture.MachineId, row.ProviderMachineId);
        Assert.Equal(NayaxSqsSaleFixture.NayaxProductId, row.ProviderNayaxProductId);
        Assert.Equal(
            NayaxSqsSaleFixture.SaleInstant.AddMilliseconds(300), row.ProviderAuthorizationInstantUtc);
        Assert.Equal(DateTimeKind.Utc, row.ProviderAuthorizationInstantUtc!.Value.Kind);
    }

    /// <summary>
    /// The hourly run re-reads the same rolling window, so most of what it sees it has already seen.
    /// An identical observation that changes no state writes nothing - and the sale's state and the
    /// one existing row are both left exactly as they were.
    /// </summary>
    [Fact]
    public async Task A_repeated_identical_observation_writes_nothing()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();
        var saleId = await StoreQueueSaleAsync(fixture);

        await using (var first = fixture.Context())
        {
            Assert.True((await new EfNayaxSaleProviderEvidenceStore(first)
                .RecordComparisonAsync(Comparison(saleId, Provider()), CancellationToken.None))
                .Written);
        }

        await using var db = fixture.Context();
        var second = await new EfNayaxSaleProviderEvidenceStore(db)
            .RecordComparisonAsync(
                Comparison(saleId, Provider(), new Guid("22222222-2222-2222-2222-222222222222")),
                CancellationToken.None);

        Assert.False(second.Written);
        Assert.Null(second.EvidenceId);
        Assert.False(second.StateChanged);
        Assert.Equal(NayaxSqsVerification.Verified, second.State);

        await using var read = fixture.Context();
        var row = await read.NayaxSaleProviderEvidences.AsNoTracking().SingleAsync();
        Assert.Equal(RunId, row.RunId);
    }

    /// <summary>
    /// A different observation is a new row even when the state does not move: the provider now
    /// reports a different amount, which is a contradiction the first run did not see.
    /// </summary>
    [Fact]
    public async Task A_different_observation_appends_a_row()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();
        var saleId = await StoreQueueSaleAsync(fixture);

        await using (var first = fixture.Context())
        {
            await new EfNayaxSaleProviderEvidenceStore(first)
                .RecordComparisonAsync(Comparison(saleId, Provider()), CancellationToken.None);
        }

        await using (var second = fixture.Context())
        {
            var conflicting = await new EfNayaxSaleProviderEvidenceStore(second)
                .RecordComparisonAsync(
                    Comparison(saleId, Provider() with { SettlementValue = 9.99m }),
                    CancellationToken.None);

            Assert.True(conflicting.Written);
            Assert.True(conflicting.StateChanged);
            Assert.Equal(NayaxSqsVerification.Conflict, conflicting.State);
        }

        await using var read = fixture.Context();
        var rows = await read.NayaxSaleProviderEvidences.AsNoTracking().OrderBy(row => row.Id).ToListAsync();

        // Both rows survive: the earlier evidence is kept, so what the sale was verified by is still
        // readable after the contradiction.
        Assert.Equal(2, rows.Count);
        Assert.Equal(NayaxSqsVerification.Verified, rows[0].ResultingState);
        Assert.Equal(NayaxSqsVerification.Conflict, rows[1].ResultingState);
        Assert.Equal(NayaxSqsVerification.Conflict, await VerificationOfAsync(fixture, saleId));
    }

    /// <summary>
    /// A contradiction is terminal for this reconciliation: later matching evidence is recorded and
    /// says truthfully that the sale stayed conflicting. Only an Owner decision (#1105) moves it.
    /// </summary>
    [Fact]
    public async Task Matching_evidence_about_a_conflicting_sale_is_recorded_and_moves_nothing()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();
        var saleId = await StoreQueueSaleAsync(fixture);

        await using (var conflicting = fixture.Context())
        {
            await new EfNayaxSaleProviderEvidenceStore(conflicting)
                .RecordComparisonAsync(
                    Comparison(saleId, Provider() with { SettlementValue = 9.99m }),
                    CancellationToken.None);
        }

        await using var db = fixture.Context();
        var later = await new EfNayaxSaleProviderEvidenceStore(db)
            .RecordComparisonAsync(Comparison(saleId, Provider()), CancellationToken.None);

        Assert.True(later.Written);
        Assert.False(later.StateChanged);
        Assert.Equal(NayaxSqsVerification.Conflict, later.State);

        await using var read = fixture.Context();
        var rows = await read.NayaxSaleProviderEvidences.AsNoTracking().OrderBy(row => row.Id).ToListAsync();
        Assert.Equal(2, rows.Count);

        // The appended row's resulting state is the state the sale actually holds, not the state the
        // observation argued for.
        Assert.Equal(NayaxSqsVerification.Conflict, rows[1].ResultingState);
        Assert.Equal(NayaxSqsVerification.Conflict, await VerificationOfAsync(fixture, saleId));
    }

    /// <summary>
    /// An absence row records what the response did return, the sale's own instants and the two
    /// observations - and nothing that could be read as the response being complete.
    /// </summary>
    [Fact]
    public async Task An_absence_row_records_the_window_the_instants_and_the_two_observations()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();
        var saleId = await StoreQueueSaleAsync(fixture);

        var window = new NayaxProviderMachineWindow(
            MachineId: NayaxSqsSaleFixture.MachineId,
            ReturnedItemCount: 10,
            EarliestReturnedInstantUtc: NayaxSqsSaleFixture.SaleInstant.AddHours(-2),
            LatestReturnedInstantUtc: NayaxSqsSaleFixture.SaleInstant.AddHours(2));

        var observation = NayaxSaleProviderAbsencePolicy.Observe(
            window,
            NayaxSqsSaleFixture.SaleInstant,
            NayaxSqsSaleFixture.ReceivedAtUtc,
            RunStarted);
        Assert.True(observation.RecordsEvidence);

        await using var db = fixture.Context();
        var written = await new EfNayaxSaleProviderEvidenceStore(db)
            .RecordAbsenceAsync(
                new NayaxSaleAbsenceEvidence(
                    saleId,
                    RunId,
                    RunStarted,
                    window,
                    NayaxSqsSaleFixture.SaleInstant,
                    NayaxSqsSaleFixture.ReceivedAtUtc,
                    observation),
                CancellationToken.None);

        Assert.True(written.Written);
        Assert.Equal(NayaxSqsVerification.NeedsInvestigation, written.State);

        await using var read = fixture.Context();
        var row = await read.NayaxSaleProviderEvidences.AsNoTracking().SingleAsync();

        Assert.Equal(NayaxSaleProviderEvidenceKind.Absence, row.Kind);
        Assert.Equal(NayaxSqsSaleFixture.MachineId, row.ReadMachineId);
        Assert.Equal(10, row.ReturnedItemCount);
        Assert.Equal(NayaxSqsSaleFixture.SaleInstant.AddHours(-2), row.ReturnedEarliestInstantUtc);
        Assert.Equal(NayaxSqsSaleFixture.SaleInstant.AddHours(2), row.ReturnedLatestInstantUtc);
        Assert.Equal(NayaxSqsSaleFixture.SaleInstant, row.SaleAuthorizationInstantUtc);
        Assert.Equal(NayaxSqsSaleFixture.ReceivedAtUtc, row.SaleReceivedViaSqsAtUtc);
        Assert.True(row.Bracketed);
        Assert.True(row.OutsideReceiptGrace);
        Assert.Equal(NayaxSqsVerification.NeedsInvestigation, row.ResultingState);

        // The comparison columns are untouched.
        Assert.Null(row.ProviderMachineId);
        Assert.Null(row.MachineComparison);
        Assert.Null(row.AmountComparison);

        Assert.Equal(NayaxSqsVerification.NeedsInvestigation, await VerificationOfAsync(fixture, saleId));
    }

    /// <summary>
    /// <strong>No column on an absence row says the provider's response was complete.</strong> This is
    /// the structural half of the issue's rule that absence is never proof: there is nothing for a
    /// later reader to misread as one. The row's own columns are asserted by name, so adding such a
    /// column would have to be a deliberate decision rather than a quiet one.
    /// </summary>
    [Fact]
    public async Task No_evidence_column_claims_the_provider_response_was_complete()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();
        await using var db = fixture.Context();

        var columns = db.Model
            .FindEntityType(typeof(Inventory.Infrastructure.Models.NayaxSaleProviderEvidence))!
            .GetProperties()
            .Select(property => property.Name)
            .ToList();

        Assert.DoesNotContain(columns, name => name.Contains("Complete", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(columns, name => name.Contains("Missing", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(columns, name => name.Contains("Fake", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(columns, name => name.Contains("Proven", StringComparison.OrdinalIgnoreCase));

        // What an absence row does carry: a count, two instants, the sale's own two instants and the
        // two observations - every one of them something that was seen.
        Assert.Contains("ReturnedItemCount", columns);
        Assert.Contains("ReturnedEarliestInstantUtc", columns);
        Assert.Contains("ReturnedLatestInstantUtc", columns);
        Assert.Contains("Bracketed", columns);
        Assert.Contains("OutsideReceiptGrace", columns);
    }

    /// <summary>
    /// A sale the queue never delivered carries no verification value, so there is nothing this
    /// reconciliation may say about it: nothing is recorded and no state is invented.
    /// </summary>
    [Fact]
    public async Task A_sale_that_never_came_from_the_queue_is_never_given_a_state()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();

        await using (var db = fixture.Context())
        {
            await NayaxSqsSaleFixture.Store(db).PersistLatestSalesAsync(
                [NayaxSqsSaleFixture.LastSalesItem()], CancellationToken.None);
        }

        long saleId;
        await using (var read = fixture.Context())
        {
            var sale = await read.NayaxSales.AsNoTracking().SingleAsync();
            Assert.Null(sale.SqsVerification);
            saleId = sale.Id;
        }

        await using var writing = fixture.Context();
        var written = await new EfNayaxSaleProviderEvidenceStore(writing)
            .RecordComparisonAsync(Comparison(saleId, Provider()), CancellationToken.None);

        Assert.False(written.Written);
        Assert.Null(written.State);
        Assert.False(written.StateChanged);

        await using var after = fixture.Context();
        Assert.Empty(await after.NayaxSaleProviderEvidences.AsNoTracking().ToListAsync());
        Assert.Null((await after.NayaxSales.AsNoTracking().SingleAsync()).SqsVerification);
    }

    /// <summary>
    /// The reads are the ordinary filtered ones, so business B's identical transaction is not among
    /// what business A's run compares - and the write cannot reach it either.
    /// </summary>
    [Fact]
    public async Task A_run_for_one_business_reads_and_writes_only_that_businesss_rows()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();
        var saleA = await StoreQueueSaleAsync(fixture, NayaxSqsSaleFixture.BusinessA);
        var saleB = await StoreQueueSaleAsync(fixture, NayaxSqsSaleFixture.BusinessB);

        await using var db = fixture.Context(NayaxSqsSaleFixture.BusinessA);
        var store = new EfNayaxSaleProviderEvidenceStore(db);

        // The transaction id is the same on both rows - the two businesses' own Nayax accounts may
        // legitimately issue the same one - so this read is the one that would cross if the filters
        // were not what finds the rows.
        var found = await store.FindQueueSalesByTransactionIdsAsync(
            [NayaxSqsSaleFixture.TransactionId], CancellationToken.None);

        var only = Assert.Single(found);
        Assert.Equal(saleA, only.SaleId);

        await store.RecordComparisonAsync(Comparison(saleA, Provider()), CancellationToken.None);

        // Business B's sale was not read, and asking about it by key writes nothing either.
        var crossing = await store.RecordComparisonAsync(
            Comparison(saleB, Provider()), CancellationToken.None);
        Assert.False(crossing.Written);

        await using var read = fixture.Unrestricted();
        var row = Assert.Single(await read.NayaxSaleProviderEvidences.AsNoTracking().ToListAsync());
        Assert.Equal(NayaxSqsSaleFixture.BusinessA, row.BusinessId);
        Assert.Equal(saleA, row.NayaxSaleId);

        Assert.Equal(
            NayaxSqsVerification.Unverified,
            (await read.NayaxSales.AsNoTracking().SingleAsync(sale => sale.Id == saleB)).SqsVerification);
    }

    /// <summary>
    /// The bracketing read is bounded by the response's own range, strictly: a sale at either
    /// boundary or outside it is not read at all, because those instants are what the window reached
    /// rather than something inside it.
    /// </summary>
    [Fact]
    public async Task The_bracketing_read_is_bounded_strictly_by_the_responses_own_range()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();

        var inside = NayaxSqsSaleFixture.SaleInstant;
        var earliest = inside.AddHours(-1);
        var latest = inside.AddHours(1);

        await StoreQueueSaleAsync(fixture, transactionId: 9001, saleInstant: inside);
        await StoreQueueSaleAsync(fixture, transactionId: 9002, saleInstant: earliest);
        await StoreQueueSaleAsync(fixture, transactionId: 9003, saleInstant: latest);
        await StoreQueueSaleAsync(fixture, transactionId: 9004, saleInstant: earliest.AddMinutes(-1));
        await StoreQueueSaleAsync(fixture, transactionId: 9005, saleInstant: latest.AddMinutes(1));

        await using var db = fixture.Context();
        var found = await new EfNayaxSaleProviderEvidenceStore(db)
            .FindQueueSalesBracketedByAsync(
                NayaxSqsSaleFixture.MachineId, earliest, latest, CancellationToken.None);

        Assert.Equal([9001L], found.Select(sale => sale.TransactionId).Order().ToArray());
    }

    /// <summary>
    /// A machine's absence read asks only about that machine: a sale on another machine inside the
    /// same instants is a different question with different evidence behind it.
    /// </summary>
    [Fact]
    public async Task The_bracketing_read_asks_only_about_the_machine_that_was_read()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();

        var inside = NayaxSqsSaleFixture.SaleInstant;
        await StoreQueueSaleAsync(fixture, transactionId: 9101, saleInstant: inside);

        await using (var other = fixture.Context())
        {
            var input = NayaxSqsSaleFixture.SqsSale(
                suppliedStatusId: 12, transactionId: 9102, saleInstant: inside, messageId: "message-other");
            input.Report.MachineID = NayaxSqsSaleFixture.MachineId + 1;
            await NayaxSqsSaleFixture.Store(other).PersistSqsSaleAsync(input, CancellationToken.None);
        }

        await using var db = fixture.Context();
        var found = await new EfNayaxSaleProviderEvidenceStore(db)
            .FindQueueSalesBracketedByAsync(
                NayaxSqsSaleFixture.MachineId,
                inside.AddHours(-1),
                inside.AddHours(1),
                CancellationToken.None);

        Assert.Equal([9101L], found.Select(sale => sale.TransactionId).Order().ToArray());
    }

    /// <summary>
    /// Nothing to ask about reads nothing at all: an empty id set does not become an unfiltered read
    /// of every queue-delivered sale the business has.
    /// </summary>
    [Fact]
    public async Task An_empty_transaction_id_set_reads_nothing()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();
        await StoreQueueSaleAsync(fixture);

        await using var db = fixture.Context();

        Assert.Empty(
            await new EfNayaxSaleProviderEvidenceStore(db)
                .FindQueueSalesByTransactionIdsAsync([], CancellationToken.None));
    }

    /// <summary>
    /// <strong>The evidence and the state it caused are one commit.</strong> The write takes SQLite's
    /// write lock before its own reads, so a writer that moved the sale's state in between is read
    /// rather than overwritten - which is what will keep issue #1105's Owner decisions from
    /// interleaving with a run. Here the other writer moves the sale to <c>Conflict</c> while this
    /// run is about to verify it, and the run's own matching evidence correctly leaves it conflicting.
    /// </summary>
    [Fact]
    public async Task The_write_decides_from_the_state_it_re_reads_inside_its_own_transaction()
    {
        await using var fixture = await NayaxSqsSaleFixture.OnDiskAsync();
        var saleId = await StoreQueueSaleAsync(fixture);

        // The "other writer": an Owner decision's shape, committed between this run's earlier read
        // of the sale and the evidence write below.
        await using (var other = fixture.SecondWriter())
        {
            var sale = await other.NayaxSales.SingleAsync(row => row.Id == saleId);
            sale.SqsVerification = NayaxSqsVerification.Conflict;
            await other.SaveChangesAsync();
        }

        await using var db = fixture.Context();
        var written = await new EfNayaxSaleProviderEvidenceStore(db)
            .RecordComparisonAsync(Comparison(saleId, Provider()), CancellationToken.None);

        Assert.True(written.Written);
        Assert.False(written.StateChanged);
        Assert.Equal(NayaxSqsVerification.Conflict, written.State);
        Assert.Equal(NayaxSqsVerification.Conflict, await VerificationOfAsync(fixture, saleId));
    }

    /// <summary>
    /// <strong>The same claim, through the pipeline that really runs it.</strong> One scoped
    /// <c>AppDbContext</c> serves a whole business's reconciliation, and the run's own save
    /// (<c>PersistLatestSalesAsync</c>, issue #1103) loads and <em>tracks</em> every sale the response
    /// spoke about before anything is compared. EF Core answers a later query for a tracked key with
    /// the instance it is already tracking, without refreshing it, so a "re-read" that is only a
    /// query would decide this write from the values that earlier read saw - and the write lock would
    /// be protecting nothing. Here the second writer commits an Owner decision's shape
    /// (<c>Conflict</c>) in that exact window, and the write records and keeps the committed state
    /// rather than the tracked <c>Unverified</c> one.
    /// </summary>
    [Fact]
    public async Task The_write_decides_from_the_committed_state_even_when_the_run_already_tracked_the_sale()
    {
        await using var fixture = await NayaxSqsSaleFixture.OnDiskAsync();
        var saleId = await StoreQueueSaleAsync(fixture);

        // One context for the whole run, exactly as the reconciliation's own service scope gives it.
        await using var db = fixture.Context();

        // The run's save over the item it read, which is what leaves the sale tracked in it.
        await NayaxSqsSaleFixture.Store(db)
            .PersistLatestSalesAsync([NayaxSqsSaleFixture.LastSalesItem()], CancellationToken.None);

        // The stale value the write must not decide from, named so this test says what it is about.
        Assert.Equal(NayaxSqsVerification.Unverified, TrackedSale(db, saleId).SqsVerification);

        // The competing writer, on its own connection: committed after that save and before the
        // evidence write below.
        await using (var other = fixture.SecondWriter())
        {
            var sale = await other.NayaxSales.SingleAsync(row => row.Id == saleId);
            sale.SqsVerification = NayaxSqsVerification.Conflict;
            await other.SaveChangesAsync();
        }

        var written = await new EfNayaxSaleProviderEvidenceStore(db)
            .RecordComparisonAsync(Comparison(saleId, Provider()), CancellationToken.None);

        // Decided from Conflict, which is terminal: the matching evidence is appended beside it and
        // moves nothing. Decided from the tracked Unverified, this run would instead have verified a
        // sale somebody had just contradicted - and overwritten their decision.
        Assert.True(written.Written);
        Assert.False(written.StateChanged);
        Assert.Equal(NayaxSqsVerification.Conflict, written.State);

        await using var read = fixture.Context();
        var row = await read.NayaxSaleProviderEvidences.AsNoTracking().SingleAsync();
        Assert.Equal(NayaxSqsVerification.Conflict, row.ResultingState);
        Assert.Equal(NayaxSqsVerification.Conflict, await VerificationOfAsync(fixture, saleId));
    }

    /// <summary>
    /// <strong>And the comparison's own inputs are read in that same window.</strong> A field result
    /// is a statement about the stored sale, so it has to be decided from the sale the write reads
    /// rather than from the one the run read earlier. Here the second writer commits a different
    /// settlement value while the run's save has the row tracked, and the provider's unchanged 3.50
    /// is an authoritative contradiction instead of the match the stale values would have reported.
    /// </summary>
    [Fact]
    public async Task The_comparison_is_decided_from_the_sale_values_the_write_lock_re_reads()
    {
        await using var fixture = await NayaxSqsSaleFixture.OnDiskAsync();
        var saleId = await StoreQueueSaleAsync(fixture);

        await using var db = fixture.Context();

        await NayaxSqsSaleFixture.Store(db)
            .PersistLatestSalesAsync([NayaxSqsSaleFixture.LastSalesItem()], CancellationToken.None);

        Assert.Equal(3.5m, TrackedSale(db, saleId).SettlementValue);

        await using (var other = fixture.SecondWriter())
        {
            var sale = await other.NayaxSales.SingleAsync(row => row.Id == saleId);
            sale.SettlementValue = 9.99m;
            await other.SaveChangesAsync();
        }

        var written = await new EfNayaxSaleProviderEvidenceStore(db)
            .RecordComparisonAsync(Comparison(saleId, Provider()), CancellationToken.None);

        Assert.True(written.Written);
        Assert.True(written.StateChanged);
        Assert.Equal(NayaxSqsVerification.Conflict, written.State);

        await using var read = fixture.Context();
        var row = await read.NayaxSaleProviderEvidences.AsNoTracking().SingleAsync();

        // The amount was compared against the committed 9.99 and disagrees; everything else still
        // agrees, and the provider's own value is recorded exactly as it was received.
        Assert.Equal(NayaxSaleFieldComparison.Mismatch, row.AmountComparison);
        Assert.Equal(NayaxSaleFieldComparison.Match, row.MachineComparison);
        Assert.Equal(NayaxSaleFieldComparison.Match, row.InstantComparison);
        Assert.Equal(3.5m, row.ProviderSettlementValue);
        Assert.Equal(NayaxSqsVerification.Conflict, row.ResultingState);

        Assert.Equal(NayaxSqsVerification.Conflict, await VerificationOfAsync(fixture, saleId));
    }

    /// <summary>
    /// The sale as <paramref name="db"/> is tracking it: the instance a tracking query inside the
    /// evidence write would be answered with, and therefore the values that write must not decide
    /// from. Its presence is the precondition the two cases above are about.
    /// </summary>
    private static Inventory.Infrastructure.Models.NayaxSales TrackedSale(AppDbContext db, long saleId) =>
        db.ChangeTracker
            .Entries<Inventory.Infrastructure.Models.NayaxSales>()
            .Single(entry => entry.Entity.Id == saleId)
            .Entity;

    /// <summary>
    /// One comparison observation. It carries the provider's values and nothing of this
    /// application's: the field results and the state they argue for are the store's own, decided
    /// from the sale it re-reads inside its write lock.
    /// </summary>
    private static NayaxSaleComparisonEvidence Comparison(
        long saleId, NayaxProviderSaleValues provider, Guid? runId = null) =>
        new(saleId, runId ?? RunId, RunStarted, provider);

    private static NayaxProviderSaleValues Provider() => new(
        MachineId: NayaxSqsSaleFixture.MachineId,
        SettlementValue: 3.5m,
        AuthorizationInstantUtc: NayaxSqsSaleFixture.SaleInstant,
        CurrencyCode: null,
        NayaxProductId: NayaxSqsSaleFixture.NayaxProductId,
        ProductName: "Fictional Spring Water 600ml");

    /// <summary>
    /// One queue-delivered sale, saved through the real store so it carries the provenance and the
    /// <c>Unverified</c> state a real message's sale carries.
    /// </summary>
    private static async Task<long> StoreQueueSaleAsync(
        NayaxSqsSaleFixture fixture,
        int businessId = NayaxSqsSaleFixture.BusinessA,
        long transactionId = NayaxSqsSaleFixture.TransactionId,
        DateTime? saleInstant = null)
    {
        await using (var db = fixture.Context(businessId))
        {
            await NayaxSqsSaleFixture.Store(db).PersistSqsSaleAsync(
                NayaxSqsSaleFixture.SqsSale(
                    suppliedStatusId: 12,
                    transactionId: transactionId,
                    saleInstant: saleInstant,
                    messageId: $"message-{businessId}-{transactionId}"),
                CancellationToken.None);
        }

        await using var read = fixture.Context(businessId);
        var sale = await read.NayaxSales.AsNoTracking()
            .SingleAsync(row => row.TransactionID == transactionId);

        Assert.Equal(NayaxSqsVerification.Unverified, sale.SqsVerification);
        return sale.Id;
    }

    private static async Task<NayaxSqsVerification?> VerificationOfAsync(
        NayaxSqsSaleFixture fixture, long saleId)
    {
        await using var db = fixture.Unrestricted();
        return (await db.NayaxSales.AsNoTracking().SingleAsync(row => row.Id == saleId)).SqsVerification;
    }
}
