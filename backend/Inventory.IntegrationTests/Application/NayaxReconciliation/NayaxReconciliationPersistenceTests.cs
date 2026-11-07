using Inventory.Application.NayaxReconciliation;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Domain.NayaxSqs;
using Inventory.Infrastructure.Persistence;
using InventoryApi.Tests.Application.NayaxSqs;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Application.NayaxReconciliation;

/// <summary>
/// Issue #1104's claims that only a real database can settle: that <strong>no verification state
/// changes what counts</strong>, that an hourly run beside a queue delivery of the same new
/// transaction still produces one sale and one effect, and that the business's own success instant
/// is written as an ordinary tenant-owned update of its own sender row.
///
/// <para>A real SQLite <strong>file</strong> for the concurrency case, because two writers and
/// SQLite's own locking do not exist on a shared in-memory database - it has one connection, which
/// would be the same writer under another name (AGENTS.md § Database and migrations).</para>
/// </summary>
public class NayaxReconciliationPersistenceTests
{
    private static readonly DateTime RunStarted = new(2026, 11, 7, 3, 0, 0, DateTimeKind.Utc);

    private static readonly Guid RunId = new("33333333-3333-3333-3333-333333333333");

    /// <summary>
    /// <strong>In this issue, no state changes what counts.</strong> A sale moved to
    /// <see cref="NayaxSqsVerification.Conflict"/> or
    /// <see cref="NayaxSqsVerification.NeedsInvestigation"/> is still a completed sale by the
    /// application's own rule - status <c>12</c> - and the product's costed position is exactly what
    /// it was before the evidence was recorded. Excluding a row from stock, costs or reports is issue
    /// #1105's, and nothing here anticipates it.
    /// </summary>
    [Theory]
    [InlineData(NayaxSqsVerification.Conflict)]
    [InlineData(NayaxSqsVerification.NeedsInvestigation)]
    public async Task A_conflicting_or_flagged_sale_still_counts_and_its_costing_is_untouched(
        NayaxSqsVerification state)
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();

        await using (var save = fixture.Context())
        {
            await NayaxSqsSaleFixture.Store(save).PersistSqsSaleAsync(
                NayaxSqsSaleFixture.SqsSale(NayaxTransactionStatusIds.Completed),
                CancellationToken.None);
        }

        await using (var drain = fixture.Context())
        {
            await NayaxSqsSaleFixture.Store(drain).RebuildInventoryCostsAsync(CancellationToken.None);
        }

        long saleId;
        int? costedQuantityBefore;
        decimal? inventoryValueBefore;
        await using (var before = fixture.Context())
        {
            saleId = (await before.NayaxSales.AsNoTracking().SingleAsync()).Id;
            var costedBefore = await before.Products.AsNoTracking()
                .SingleAsync(row => row.Id == NayaxSqsSaleFixture.ProductA);
            costedQuantityBefore = costedBefore.CostingQuantity;
            inventoryValueBefore = costedBefore.InventoryValue;
        }

        // The sale is consumed exactly once before any evidence exists, which is the baseline the
        // claim below is about.
        Assert.Equal(9, costedQuantityBefore);
        Assert.Equal(18m, inventoryValueBefore);

        await using (var evidence = fixture.Context())
        {
            var written = await new EfNayaxSaleProviderEvidenceStore(evidence)
                .RecordComparisonAsync(
                    new NayaxSaleComparisonEvidence(
                        saleId,
                        RunId,
                        RunStarted,
                        Provider(),
                        state is NayaxSqsVerification.Conflict
                            ? Fields(amount: NayaxSaleFieldComparison.Mismatch)
                            : Fields(instant: NayaxSaleFieldComparison.Mismatch),
                        state),
                    CancellationToken.None);

            Assert.True(written.Written);
            Assert.Equal(state, written.State);
        }

        await using var after = fixture.Context();
        var sale = await after.NayaxSales.AsNoTracking().SingleAsync();

        // The state moved and nothing else did. The status is what stock, costing and revenue follow.
        Assert.Equal(state, sale.SqsVerification);
        Assert.Equal(NayaxTransactionStatusIds.Completed, sale.TransactionStatusId);
        Assert.NotNull(sale.CostOfGoodsSold);

        // And the business's own completed-sale query still returns it, which is what "still counts"
        // means in practice.
        Assert.Single(
            await after.NayaxSales.AsNoTracking()
                .Where(row => row.TransactionStatusId == NayaxTransactionStatusIds.Completed)
                .ToListAsync());

        var product = await after.Products.AsNoTracking()
            .SingleAsync(row => row.Id == NayaxSqsSaleFixture.ProductA);
        Assert.Equal(costedQuantityBefore, product.CostingQuantity);
        Assert.Equal(inventoryValueBefore, product.InventoryValue);
    }

    /// <summary>
    /// <strong>The required concurrency case for this issue.</strong> The hourly run's own save path -
    /// <c>PersistLatestSalesAsync</c> over the items it read - and a queue delivery of the same new
    /// transaction, started together against one file. The result is one sale row, one costing-inventory
    /// consumption, and exactly one piece of evidence afterwards with the state applied once.
    /// </summary>
    [Fact]
    public async Task An_hourly_run_and_a_queue_delivery_of_one_transaction_produce_one_row_and_one_effect()
    {
        await using var fixture = await NayaxSqsSaleFixture.OnDiskAsync();

        await using var hourly = fixture.Context();
        await using var queue = fixture.SecondWriter();

        var hourlyStore = NayaxSqsSaleFixture.Store(hourly);
        var queueStore = NayaxSqsSaleFixture.Store(queue);

        // Started together on two connections. Which one takes SQLite's write lock first is genuinely
        // up to SQLite; what is asserted is that the outcome does not depend on the answer.
        await Task.WhenAll(
            Task.Run(() => hourlyStore.PersistLatestSalesAsync(
                [NayaxSqsSaleFixture.LastSalesItem()], CancellationToken.None)),
            Task.Run(() => queueStore.PersistSqsSaleAsync(
                NayaxSqsSaleFixture.SqsSale(NayaxTransactionStatusIds.Completed),
                CancellationToken.None)));

        long saleId;
        await using (var drain = fixture.Context())
        {
            saleId = (await drain.NayaxSales.AsNoTracking().SingleAsync()).Id;
            await NayaxSqsSaleFixture.Store(drain).RebuildInventoryCostsAsync(CancellationToken.None);
        }

        // Now the hourly run's own verification pass over the item it read.
        await using (var evidence = fixture.Context())
        {
            await new EfNayaxSaleProviderEvidenceStore(evidence)
                .RecordComparisonAsync(
                    new NayaxSaleComparisonEvidence(
                        saleId, RunId, RunStarted, Provider(), Fields(), NayaxSqsVerification.Verified),
                    CancellationToken.None);
        }

        await using var read = fixture.Context();

        // One row, one unit consumed, one piece of evidence - whichever side inserted the sale.
        var sale = await read.NayaxSales.AsNoTracking().SingleAsync();
        Assert.Equal(NayaxTransactionStatusIds.Completed, sale.TransactionStatusId);

        var product = await read.Products.AsNoTracking()
            .SingleAsync(row => row.Id == NayaxSqsSaleFixture.ProductA);
        Assert.Equal(9, product.CostingQuantity);
        Assert.Equal(18m, product.InventoryValue);

        var evidenceRows = await read.NayaxSaleProviderEvidences.AsNoTracking().ToListAsync();

        // Which side won SQLite's write lock decides the sale's provenance, and both answers are
        // correct - what must not happen is a state applied twice, or evidence about a sale that has
        // no queue delivery to verify.
        if (sale.ReceivedViaSqsAtUtc is null)
        {
            // The hourly run's own save inserted it, so it carries no verification state at all and
            // nothing gives it one: there is no queue delivery to verify.
            Assert.Null(sale.SqsVerification);
            Assert.Empty(evidenceRows);
        }
        else
        {
            // The queue delivery inserted it, so it carried Unverified and this run verified it, once.
            Assert.Equal(NayaxSqsVerification.Verified, sale.SqsVerification);
            var row = Assert.Single(evidenceRows);
            Assert.Equal(saleId, row.NayaxSaleId);
            Assert.Equal(NayaxSqsVerification.Verified, row.ResultingState);
        }
    }

    /// <summary>
    /// The success instant is written on the business's <em>own</em> sender row, as an ordinary
    /// tenant-owned update: the central query filter finds the row, the enforcer checks the write, and
    /// nothing else about the mapping is touched - not the sender id, the enabled flag, the
    /// last-message instant or the revision.
    /// </summary>
    [Fact]
    public async Task The_success_instant_is_written_on_the_businesss_own_sender_row()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();
        await fixture.RecordSenderAsync(NayaxSqsSaleFixture.BusinessA, "AIDAFICTIONALSENDERAA");

        await using (var db = fixture.Context())
        {
            await new EfNayaxReconciliationSuccessStore(db)
                .RecordSucceededAsync(RunStarted, CancellationToken.None);
        }

        await using var read = fixture.Unrestricted();
        var sender = await read.BusinessNayaxSqsSenders.AsNoTracking().SingleAsync();

        Assert.Equal(RunStarted, sender.LastReconciliationSucceededAtUtc);
        Assert.Equal(DateTimeKind.Utc, sender.LastReconciliationSucceededAtUtc!.Value.Kind);

        Assert.Equal("AIDAFICTIONALSENDERAA", sender.AwsSenderId);
        Assert.True(sender.IsEnabled);
        Assert.Null(sender.LastMessageReceivedAtUtc);
        Assert.Equal(1, sender.Revision);
    }

    /// <summary>
    /// A later run moves the instant on, which is what makes it "the last full success" rather than
    /// "the first".
    /// </summary>
    [Fact]
    public async Task A_later_success_moves_the_instant_on()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();
        await fixture.RecordSenderAsync(NayaxSqsSaleFixture.BusinessA, "AIDAFICTIONALSENDERAA");

        await using (var first = fixture.Context())
        {
            await new EfNayaxReconciliationSuccessStore(first)
                .RecordSucceededAsync(RunStarted, CancellationToken.None);
        }

        await using (var second = fixture.Context())
        {
            await new EfNayaxReconciliationSuccessStore(second)
                .RecordSucceededAsync(RunStarted.AddHours(1), CancellationToken.None);
        }

        await using var read = fixture.Unrestricted();
        Assert.Equal(
            RunStarted.AddHours(1),
            (await read.BusinessNayaxSqsSenders.AsNoTracking().SingleAsync())
                .LastReconciliationSucceededAtUtc);
    }

    /// <summary>
    /// One business's run never reaches another business's sender row: the write is scoped by the
    /// central filter, so business B's mapping is untouched - and a business whose row the filter does
    /// not find has nothing stamped and nothing created either.
    /// </summary>
    [Fact]
    public async Task The_success_store_never_reaches_another_businesss_sender_row()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();
        await fixture.RecordSenderAsync(NayaxSqsSaleFixture.BusinessA, "AIDAFICTIONALSENDERAA");
        await fixture.RecordSenderAsync(NayaxSqsSaleFixture.BusinessB, "AIDAFICTIONALSENDERBB");

        await using (var db = fixture.Context(NayaxSqsSaleFixture.BusinessA))
        {
            await new EfNayaxReconciliationSuccessStore(db)
                .RecordSucceededAsync(RunStarted, CancellationToken.None);
        }

        await using var read = fixture.Unrestricted();
        var senders = await read.BusinessNayaxSqsSenders.AsNoTracking()
            .OrderBy(row => row.BusinessId)
            .ToListAsync();

        Assert.Equal(RunStarted, senders[0].LastReconciliationSucceededAtUtc);
        Assert.Null(senders[1].LastReconciliationSucceededAtUtc);
    }

    /// <summary>
    /// A business with no sender mapping has nothing stamped and - deliberately - nothing created: a
    /// mapping exists because a platform administrator recorded it, and a reconciliation inventing one
    /// would be a business attributing its own sales.
    /// </summary>
    [Fact]
    public async Task The_success_store_creates_no_sender_row()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();

        await using (var db = fixture.Context())
        {
            await new EfNayaxReconciliationSuccessStore(db)
                .RecordSucceededAsync(RunStarted, CancellationToken.None);
        }

        await using var read = fixture.Unrestricted();
        Assert.Empty(await read.BusinessNayaxSqsSenders.AsNoTracking().ToListAsync());
    }

    private static NayaxProviderSaleValues Provider() => new(
        MachineId: NayaxSqsSaleFixture.MachineId,
        SettlementValue: 3.5m,
        AuthorizationInstantUtc: NayaxSqsSaleFixture.SaleInstant,
        CurrencyCode: null,
        NayaxProductId: NayaxSqsSaleFixture.NayaxProductId,
        ProductName: "Fictional Spring Water 600ml");

    private static NayaxSaleFieldComparisons Fields(
        NayaxSaleFieldComparison amount = NayaxSaleFieldComparison.Match,
        NayaxSaleFieldComparison instant = NayaxSaleFieldComparison.Match) =>
        new(
            Machine: NayaxSaleFieldComparison.Match,
            Amount: amount,
            Instant: instant,
            Currency: NayaxSaleFieldComparison.NotCompared,
            Product: NayaxSaleFieldComparison.Match);
}
