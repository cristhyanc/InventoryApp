using System.Linq.Expressions;
using Inventory.Application.Machines;
using Inventory.Application.Reporting.Daily;
using Inventory.Application.Reporting.ProductProfitability;
using Inventory.Application.Reporting.Shared;
using Inventory.Application.Reporting.Transactions;
using Inventory.Application.SalesSync;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Domain.MachineStock;
using Inventory.Domain.NayaxSqs;
using Inventory.Domain.Providers;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using Inventory.Infrastructure.Reporting.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Application.NayaxSqs;

/// <summary>
/// Issue #1107, over relational SQLite with the real store, the real sale costing and the real
/// inventory-cost replay: <strong>when Nayax refunds a sale the money is reversed and the item is
/// still gone</strong>.
///
/// <para>One rule used to decide both, so a status-<c>62</c> row consumed no stock - the opposite of
/// the owner's decision of 8 November 2026. The rule is now two: a completed sale (status <c>12</c>,
/// money and every report) and a stock-consuming sale (status <c>12</c> or <c>62</c>, physical
/// machine stock and costing inventory). These cases are about both halves at once, because either
/// one on its own would be wrong: a refunded sale that still counted as revenue, or a refunded sale
/// whose unit came back to inventory it never returned to.</para>
///
/// <para>A real SQLite database rather than the EF InMemory provider, for the reasons
/// <see cref="NayaxSqsSaleFixture"/> gives: the status update is one <c>BEGIN IMMEDIATE</c>
/// transaction, the audit row commits with it, and the figures these cases assert come out of
/// translated queries over a relational schema. The concurrent-delivery case goes further and runs
/// over a real SQLite <em>file</em> with two connections, because two writers - and therefore the
/// write lock the "apply once" guarantee rests on - do not exist on one shared connection.</para>
/// </summary>
public class NayaxSqsRefundTests
{
    #region The refund is applied, once, and audited

    /// <summary>
    /// <strong>The acceptance criterion at the centre of the issue.</strong> A stored sale that moves
    /// from <c>12</c> to <c>62</c> keeps its unit consumed in costing inventory and keeps the unit
    /// cost it was sold at: <c>CostingQuantity</c>, <c>InventoryValue</c>, <c>QuantityInStock</c> and
    /// the sale's own cost are exactly what they were before the refund - before and after a rebuild
    /// of the product.
    /// </summary>
    [Fact]
    public async Task A_refund_keeps_the_unit_consumed_at_its_original_cost_and_changes_no_figure()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();
        await PersistCompletedSaleAsync(fixture);

        var before = await PositionAsync(fixture);
        // Physical storage stock is untouched by a sale - the unit left a machine, not the storeroom
        // (AGENTS.md § Inventory and historical costing invariants) - and the costing position is one
        // unit and $2 lighter at the product's own AVCO cost.
        Assert.Equal((10, 9, 18m, 2m), before);

        var applied = await ApplyRefundAsync(fixture);

        Assert.Equal(NayaxSqsRefundApplication.Applied, applied);

        await using (var read = fixture.Context())
        {
            var sale = await read.NayaxSales.AsNoTracking().SingleAsync();
            Assert.Equal(NayaxTransactionStatusIds.Refunded, sale.TransactionStatusId);

            // The sale's own cost is the one it was sold at, untouched: a refund reverses money, and
            // historical COGS is not money that moves.
            Assert.Equal(2m, sale.UnitCostAtSale);
            Assert.Equal(2m, sale.CostOfGoodsSold);
            Assert.Equal(SaleCostingStatus.Costed, sale.CostingStatus);
            Assert.Equal(SaleCostSource.InventoryLedger, sale.CostSource);
        }

        Assert.Equal(before, await PositionAsync(fixture));

        // Applying the refund recorded nothing to replay, which is the point of the rule split: the
        // sale consumes stock at 12 and at 62 alike, so there is no work for a drain to find.
        await RebuildAsync(fixture);
        Assert.Equal(before, await PositionAsync(fixture));
        await using (var pending = fixture.Context())
        {
            Assert.Empty(await pending.PendingInventoryCostRebuilds.AsNoTracking().ToListAsync());
        }

        // And a full replay of the product - which any later sale of it triggers anyway - still comes
        // out at exactly the same position. This is where a rule that had stopped consuming the unit
        // would have handed it back to inventory it never returned to.
        await RebuildProductAsync(fixture);
        Assert.Equal(before, await PositionAsync(fixture));
    }

    /// <summary>
    /// The audit row records what changed, which message said so and when - and it commits with the
    /// status, so "the sale is refunded" and "something recorded why" cannot disagree.
    /// </summary>
    [Fact]
    public async Task Applying_a_refund_writes_one_audit_row_naming_the_message_that_caused_it()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();
        await PersistCompletedSaleAsync(fixture);

        await ApplyRefundAsync(fixture);

        await using var read = fixture.Context();
        var sale = await read.NayaxSales.AsNoTracking().SingleAsync();
        var audit = await read.NayaxSaleStatusChangeAudits.AsNoTracking().SingleAsync();

        Assert.Equal(sale.Id, audit.NayaxSaleId);
        Assert.Equal(NayaxSqsSaleFixture.TransactionId, audit.TransactionId);
        Assert.Equal(NayaxTransactionStatusIds.Completed, audit.PreviousTransactionStatusId);
        Assert.Equal(NayaxTransactionStatusIds.Refunded, audit.NewTransactionStatusId);
        Assert.Equal("refund-message-1", audit.SourceMessageId);
        Assert.Equal(NayaxSqsSaleFixture.RefundReceivedAtUtc, audit.OccurredAtUtc);
        Assert.Equal(DateTimeKind.Utc, audit.OccurredAtUtc.Kind);
        Assert.Equal(NayaxSqsSaleFixture.BusinessA, audit.BusinessId);
    }

    /// <summary>
    /// A redelivered refund - a standard queue may deliver one more than once - has no further
    /// effect: the status is already <c>62</c>, nothing is written, and there is no second audit row.
    /// </summary>
    [Fact]
    public async Task A_redelivered_refund_changes_nothing_and_writes_no_second_audit_row()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();
        await PersistCompletedSaleAsync(fixture);
        await ApplyRefundAsync(fixture);

        var again = await ApplyRefundAsync(fixture, messageId: "refund-message-2");

        Assert.Equal(NayaxSqsRefundApplication.AlreadyRefunded, again);

        await using var read = fixture.Context();
        Assert.Equal(
            NayaxTransactionStatusIds.Refunded,
            (await read.NayaxSales.AsNoTracking().SingleAsync()).TransactionStatusId);

        var audit = Assert.Single(await read.NayaxSaleStatusChangeAudits.AsNoTracking().ToListAsync());
        Assert.Equal("refund-message-1", audit.SourceMessageId);

        // And still exactly one unit consumed, at its original cost.
        Assert.Equal((10, 9, 18m, 2m), await PositionAsync(fixture));
    }

    /// <summary>
    /// <strong>The same refund delivered twice at once is applied once.</strong> A standard queue can
    /// deliver one message more than once, and two receivers can hold the two copies at the same
    /// instant - so "apply once" has to survive the state changing between this method's own read and
    /// its write, not merely between two sequential calls.
    ///
    /// <para>Two deliveries over a real SQLite <strong>file</strong> with two connections, which is
    /// the only way two writers and SQLite's own locking exist at all (a shared in-memory database has
    /// one connection and therefore no contention to have). Both open the same <c>BEGIN IMMEDIATE</c>
    /// transaction, so the loser waits for the winner's commit and then reads the committed status
    /// <c>62</c> - and answers "already refunded" rather than writing a second status change, a second
    /// audit row or a second consumed unit. Which connection wins is genuinely up to SQLite; what is
    /// asserted is that the outcome does not depend on the answer.</para>
    /// </summary>
    [Fact]
    public async Task Two_deliveries_of_one_refund_at_once_apply_it_once_and_audit_it_once()
    {
        await using var fixture = await NayaxSqsSaleFixture.OnDiskAsync();
        await PersistCompletedSaleAsync(fixture);

        await using var first = fixture.Context();
        await using var second = fixture.SecondWriter();

        var both = await Task.WhenAll(
            Task.Run(() => NayaxSqsSaleFixture.Store(first).ApplySqsRefundAsync(
                NayaxSqsSaleFixture.SqsRefund(messageId: "refund-message-1"), CancellationToken.None)),
            Task.Run(() => NayaxSqsSaleFixture.Store(second).ApplySqsRefundAsync(
                NayaxSqsSaleFixture.SqsRefund(messageId: "refund-message-2"), CancellationToken.None)));

        Assert.Single(both.Where(applied => applied == NayaxSqsRefundApplication.Applied));
        Assert.Single(both.Where(applied => applied == NayaxSqsRefundApplication.AlreadyRefunded));

        await using var read = fixture.Context();
        Assert.Equal(
            NayaxTransactionStatusIds.Refunded,
            (await read.NayaxSales.AsNoTracking().SingleAsync()).TransactionStatusId);

        // One audit row, recording the move from the completed status - never two, and never one
        // claiming 62 was the previous status, which is what a second write would have recorded.
        var audit = Assert.Single(await read.NayaxSaleStatusChangeAudits.AsNoTracking().ToListAsync());
        Assert.Equal(NayaxTransactionStatusIds.Completed, audit.PreviousTransactionStatusId);
        Assert.Equal(NayaxTransactionStatusIds.Refunded, audit.NewTransactionStatusId);

        // Nothing to replay, and still exactly the one unit consumed at its original cost.
        Assert.Empty(await read.PendingInventoryCostRebuilds.AsNoTracking().ToListAsync());
        Assert.Equal((10, 9, 18m, 2m), await PositionAsync(fixture));
    }

    #endregion

    #region Nothing else undoes it

    /// <summary>
    /// A status-<c>12</c> message for a sale already stored as <c>62</c> - a late redelivery - goes
    /// through the existing enrich-only path: it inserts nothing, never changes the status back, and
    /// consumes no second unit. The store has never overwritten a decided status and still does not;
    /// the refund path is the one writer.
    /// </summary>
    [Fact]
    public async Task A_late_status_12_redelivery_does_not_undo_the_refund_or_consume_a_second_unit()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();
        await PersistCompletedSaleAsync(fixture);
        await ApplyRefundAsync(fixture);

        var redelivered = await PersistCompletedSaleAsync(fixture, messageId: "message-2");

        Assert.False(redelivered.Inserted);

        await using var read = fixture.Context();
        var sale = Assert.Single(await read.NayaxSales.AsNoTracking().ToListAsync());

        Assert.Equal(NayaxTransactionStatusIds.Refunded, sale.TransactionStatusId);
        Assert.Equal((10, 9, 18m, 2m), await PositionAsync(fixture));

        // The enrich path writes no audit row either: nothing about the status changed.
        Assert.Single(await read.NayaxSaleStatusChangeAudits.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// And neither does <c>lastSales</c>, whose amount rule would read this transaction as completed.
    /// Only the SQS refund path changes a stored status.
    /// </summary>
    [Fact]
    public async Task A_lastSales_refresh_does_not_undo_the_refund()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();
        await PersistCompletedSaleAsync(fixture);
        await ApplyRefundAsync(fixture);

        await using (var db = fixture.Context())
        {
            await NayaxSqsSaleFixture.Store(db).PersistLatestSalesAsync(
                [NayaxSqsSaleFixture.LastSalesItem()], CancellationToken.None);
        }

        await using var read = fixture.Context();
        Assert.Equal(
            NayaxTransactionStatusIds.Refunded,
            (await read.NayaxSales.AsNoTracking().SingleAsync()).TransactionStatusId);
        Assert.Equal((10, 9, 18m, 2m), await PositionAsync(fixture));
    }

    #endregion

    #region Everything the refund may not be applied to

    /// <summary>
    /// A refund matching no stored sale is not saved as a sale - a refund is evidence about a sale,
    /// never a sale - and the message is left for the dead-letter queue. That is the safe outcome if
    /// the unverified delivery shape this rests on turns out to be wrong, and the only way a real
    /// sample reaches a person.
    /// </summary>
    [Fact]
    public async Task A_refund_matching_no_stored_sale_saves_nothing_at_all()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();

        var applied = await ApplyRefundAsync(fixture);

        Assert.Equal(NayaxSqsRefundApplication.NoMatchingSale, applied);

        await using var read = fixture.Context();
        Assert.Empty(await read.NayaxSales.AsNoTracking().ToListAsync());
        Assert.Empty(await read.NayaxSaleStatusChangeAudits.AsNoTracking().ToListAsync());
        Assert.Empty(await read.PendingInventoryCostRebuilds.AsNoTracking().ToListAsync());

        // The product is untouched: nothing consumed and nothing returned.
        Assert.Equal((10, 10, 20m, (decimal?)null), await PositionAsync(fixture));
    }

    /// <summary>
    /// A refund whose machine, amount or product differs from the stored sale is not applied. The two
    /// are then not plainly the same transaction, whatever the transaction id says, and a partial
    /// refund is out of scope because no evidence exists that Nayax sends one - so a differing amount
    /// is refused rather than guessed at.
    /// </summary>
    [Theory]
    [InlineData("another machine", 9999999L, 3.5, NayaxSqsSaleFixture.NayaxProductId)]
    [InlineData("a smaller amount", NayaxSqsSaleFixture.MachineId, 1.5, NayaxSqsSaleFixture.NayaxProductId)]
    [InlineData("a negated amount", NayaxSqsSaleFixture.MachineId, -3.5, NayaxSqsSaleFixture.NayaxProductId)]
    [InlineData("another product", NayaxSqsSaleFixture.MachineId, 3.5, 7777777L)]
    public async Task A_refund_that_contradicts_the_stored_sale_is_not_applied(
        string because, long machineId, double settlementValue, long nayaxProductId)
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();
        await PersistCompletedSaleAsync(fixture);

        NayaxSqsRefundApplication applied;
        await using (var db = fixture.Context())
        {
            applied = await NayaxSqsSaleFixture.Store(db).ApplySqsRefundAsync(
                NayaxSqsSaleFixture.SqsRefund(
                    machineId: machineId,
                    settlementValue: (decimal)settlementValue,
                    nayaxProductId: nayaxProductId),
                CancellationToken.None);
        }

        Assert.True(
            applied == NayaxSqsRefundApplication.Contradicted,
            $"A refund naming {because} must not be applied, but the store answered {applied}.");

        await using var read = fixture.Context();
        Assert.Equal(
            NayaxTransactionStatusIds.Completed,
            (await read.NayaxSales.AsNoTracking().SingleAsync()).TransactionStatusId);
        Assert.Empty(await read.NayaxSaleStatusChangeAudits.AsNoTracking().ToListAsync());

        // Still a completed sale, so still exactly the one unit it consumed - and still counted.
        Assert.Equal((10, 9, 18m, 2m), await PositionAsync(fixture));
        Assert.NotEmpty(await ProfitabilityRowsAsync(fixture));
    }

    /// <summary>
    /// <strong>And a contradicting message is kept even when the sale is already refunded.</strong>
    ///
    /// <para>The policy used to answer "already refunded" before looking at the compared fields, and
    /// the reader acknowledges that outcome - so a second status-<c>62</c> message for the same
    /// transaction naming a different machine, amount or product was deleted from the queue instead
    /// of being retained for dead-letter review. It is now a contradiction, which
    /// <c>ProcessNayaxSqsSale</c> reports as <c>Permanent</c>/<c>RefundContradicted</c> and therefore
    /// releases towards the dead-letter queue. Under a delivery shape Nayax does not document, that
    /// message is the evidence that the assumption is wrong.</para>
    ///
    /// <para>Nothing about the already refunded sale moves either: the refund that was applied keeps
    /// its one audit row, so the genuine-redelivery guarantee is untouched by this.</para>
    /// </summary>
    [Theory]
    [InlineData("another machine", 9999999L, 3.5, NayaxSqsSaleFixture.NayaxProductId)]
    [InlineData("a smaller amount", NayaxSqsSaleFixture.MachineId, 1.5, NayaxSqsSaleFixture.NayaxProductId)]
    [InlineData("a negated amount", NayaxSqsSaleFixture.MachineId, -3.5, NayaxSqsSaleFixture.NayaxProductId)]
    [InlineData("another product", NayaxSqsSaleFixture.MachineId, 3.5, 7777777L)]
    public async Task A_contradicting_message_is_kept_even_when_the_sale_is_already_refunded(
        string because, long machineId, double settlementValue, long nayaxProductId)
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();
        await PersistCompletedSaleAsync(fixture);
        await ApplyRefundAsync(fixture);

        NayaxSqsRefundApplication applied;
        await using (var db = fixture.Context())
        {
            applied = await NayaxSqsSaleFixture.Store(db).ApplySqsRefundAsync(
                NayaxSqsSaleFixture.SqsRefund(
                    machineId: machineId,
                    settlementValue: (decimal)settlementValue,
                    nayaxProductId: nayaxProductId,
                    messageId: "refund-message-2"),
                CancellationToken.None);
        }

        Assert.True(
            applied == NayaxSqsRefundApplication.Contradicted,
            $"A second refund message naming {because} must be kept for dead-letter review, "
                + $"but the store answered {applied}.");

        await using var read = fixture.Context();
        Assert.Equal(
            NayaxTransactionStatusIds.Refunded,
            (await read.NayaxSales.AsNoTracking().SingleAsync()).TransactionStatusId);

        // The refund that was applied, and only it: no second status change and no second audit row.
        var audit = Assert.Single(await read.NayaxSaleStatusChangeAudits.AsNoTracking().ToListAsync());
        Assert.Equal("refund-message-1", audit.SourceMessageId);

        Assert.Equal((10, 9, 18m, 2m), await PositionAsync(fixture));
    }

    /// <summary>
    /// A refund naming a stored sale that is not a completed sale has nothing to refund. Moving a
    /// declined row to refunded on a message alone would change a financial status for a transaction
    /// nobody was shown to have been charged for.
    /// </summary>
    [Fact]
    public async Task A_refund_of_a_sale_that_is_not_a_completed_sale_is_not_applied()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();

        await using (var db = fixture.Context())
        {
            await NayaxSqsSaleFixture.Store(db).PersistSqsSaleAsync(
                NayaxSqsSaleFixture.SqsSale(NayaxTransactionStatusIds.CancelledOrDeclined26),
                CancellationToken.None);
        }

        var applied = await ApplyRefundAsync(fixture);

        Assert.Equal(NayaxSqsRefundApplication.NotRefundable, applied);

        await using var read = fixture.Context();
        Assert.Equal(
            NayaxTransactionStatusIds.CancelledOrDeclined26,
            (await read.NayaxSales.AsNoTracking().SingleAsync()).TransactionStatusId);
        Assert.Empty(await read.NayaxSaleStatusChangeAudits.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// A refund cannot reach another business's sale. The lookup is
    /// <c>(BusinessId, TransactionID)</c> with the business half coming from the tenant query filter,
    /// so the <em>same</em> transaction id stored by business B is invisible to a refund applied in
    /// business A's scope - which is exactly the shape that tells "attributed by the sender" apart
    /// from "attributed by the transaction id".
    /// </summary>
    [Fact]
    public async Task A_refund_cannot_reach_another_businesss_sale_with_the_same_transaction_id()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();

        await using (var db = fixture.Context(NayaxSqsSaleFixture.BusinessB))
        {
            await NayaxSqsSaleFixture.Store(db).PersistSqsSaleAsync(
                NayaxSqsSaleFixture.SqsSale(NayaxTransactionStatusIds.Completed),
                CancellationToken.None);
        }

        var applied = await ApplyRefundAsync(fixture);

        Assert.Equal(NayaxSqsRefundApplication.NoMatchingSale, applied);

        await using var verify = fixture.Unrestricted();
        var sale = Assert.Single(await verify.NayaxSales.AsNoTracking().ToListAsync());

        Assert.Equal(NayaxSqsSaleFixture.BusinessB, sale.BusinessId);
        Assert.Equal(NayaxTransactionStatusIds.Completed, sale.TransactionStatusId);
        Assert.Empty(await verify.NayaxSaleStatusChangeAudits.AsNoTracking().ToListAsync());
    }

    #endregion

    #region Money and reports

    /// <summary>
    /// The money half: a refunded sale is out of every completed-sale total, on its original sale
    /// date, exactly as status <c>62</c> behaves today - and it stays visible as refunded, with the
    /// daily report's refunded note and count.
    /// </summary>
    [Fact]
    public async Task A_refunded_sale_leaves_every_completed_sale_total_and_keeps_its_refunded_note()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();
        await PersistCompletedSaleAsync(fixture);

        var counted = await DailyTotalsAsync(fixture);
        Assert.Equal(3.5m, counted.GrossSales);
        Assert.Equal(3.5m, counted.CardSales);
        Assert.Equal(1, counted.TransactionCount);

        await ApplyRefundAsync(fixture);

        var reversed = await DailyReportAsync(fixture);
        var reversedTotals = Assert.IsType<DailyReportTotalsDto>(reversed.Totals);

        // The money is gone from the totals, on the sale's own date rather than the refund's: the
        // report reads the sale where it is, which is where it always was.
        Assert.Equal(0m, reversedTotals.GrossSales);
        Assert.Equal(0m, reversedTotals.CardSales);
        Assert.Equal(0m, reversedTotals.CashSales);
        Assert.Equal(0, reversedTotals.TransactionCount);
        Assert.Equal(0, reversedTotals.CompletedTransactionCount);

        // And the refunded note and count are still there, so it is visible rather than missing.
        Assert.Equal(1, reversedTotals.RefundedTransactionCount);
        Assert.Contains(
            reversed.DataQuality.Notes ?? [],
            note => note.Contains("refunded Nayax transaction(s) are excluded", StringComparison.Ordinal));

        // No completed-sale COGS either: the profitability report reads completed sales.
        Assert.Empty(await ProfitabilityRowsAsync(fixture));

        // It is still in status reporting, as itself.
        var reported = Assert.Single(await TransactionRowsAsync(fixture));
        Assert.Equal(NayaxTransactionStatusIds.Refunded, reported.TransactionStatusId);
        Assert.False(reported.IsCompleted);
    }

    /// <summary>
    /// The stock half, read through the authoritative machine-stock ledger: a refunded sale is still
    /// one unit out of the machine, attributed to the product the writer recorded. Returning a
    /// refunded item to stock is explicitly out of scope, and this is where that would show.
    /// </summary>
    [Fact]
    public async Task The_machine_stock_ledger_still_counts_a_refunded_sale_as_a_unit_dispensed()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();
        await PersistCompletedSaleAsync(fixture);
        await ApplyRefundAsync(fixture);

        await using var db = fixture.Context();
        var facts = await new EfMachineStockLedgerStore(db).GetLedgerFactsAsync(
            new MachineIdentity(LocalMachineId: 1, NayaxSqsSaleFixture.MachineId),
            [NayaxSqsSaleFixture.ProductA],
            CancellationToken.None);

        var dispensed = Assert.Single(facts.Sales);
        Assert.Equal(NayaxSqsSaleFixture.TransactionId, dispensed.TransactionId);
        Assert.Equal(NayaxSqsSaleFixture.ProductA, dispensed.ProductId);
        Assert.Equal(
            MachineStockMovementKind.DispensedSale,
            MachineStockMovementPolicy.FromSale(dispensed).Kind);
    }

    #endregion

    #region Exclusions and historical rows

    /// <summary>
    /// A refunded sale the review excludes consumes nothing, exactly as an excluded completed sale
    /// does. A sale the business's own <c>lastSales</c> contradicted says nothing reliable about what
    /// left the machine either, which is why the two exclusions are shared by both rules rather than
    /// written once for money.
    /// </summary>
    [Theory]
    [InlineData(NayaxSqsVerification.Conflict)]
    [InlineData(NayaxSqsVerification.OwnerExcluded)]
    public async Task A_refunded_sale_excluded_by_review_consumes_nothing(NayaxSqsVerification excluded)
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();
        await PersistCompletedSaleAsync(fixture);
        await ApplyRefundAsync(fixture);

        // The state is written directly: how a sale reaches it is issue #1104's evidence store and
        // issue #1105's Owner decision, each of which stages the product's rebuild itself. What this
        // case is about is what the rebuild then does, so it replays the product outright.
        await using (var exclude = fixture.Context())
        {
            (await exclude.NayaxSales.SingleAsync()).SqsVerification = excluded;
            await exclude.SaveChangesAsync();
        }

        await RebuildProductAsync(fixture);

        // Back to the opening costing position: the unit is not consumed by a sale nothing vouches
        // for, exactly as an excluded completed sale consumes nothing.
        Assert.Equal((10, 10, 20m, 2m), await PositionAsync(fixture));
    }

    /// <summary>
    /// <strong>Historical rows follow the same rule with no migration rewriting them.</strong> A
    /// status-<c>62</c> row that predates this change - stored before the queue reader existed, or by
    /// the uploaded export - consumes its unit the next time its product's inventory cost is
    /// replayed. That is the owner's decision of 8 November 2026: the rule applies to all status-62
    /// rows, with no cut-off date, and the next rebuild is what picks each one up.
    /// </summary>
    [Fact]
    public async Task An_existing_status_62_row_consumes_its_unit_on_the_next_rebuild()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();

        await using (var seed = fixture.Context())
        {
            seed.NayaxSales.Add(new NayaxSales
            {
                TransactionID = NayaxSqsSaleFixture.TransactionId,
                TransactionStatusId = NayaxTransactionStatusIds.Refunded,
                MachineID = NayaxSqsSaleFixture.MachineId,
                ProductId = NayaxSqsSaleFixture.ProductA,
                Provider = ExternalProvider.Nayax,
                NayaxProductId = NayaxSqsSaleFixture.NayaxProductId,
                ProviderProductIdIsRaw = true,
                SettlementValue = 3.5m,
                MachineAuthorizationTime = NayaxSqsSaleFixture.SaleInstant,
            });
            await seed.SaveChangesAsync();
        }

        // Nothing has replayed yet, so the product still shows its opening position.
        Assert.Equal((10, 10, 20m, (decimal?)null), await PositionAsync(fixture));

        await RebuildProductAsync(fixture);

        Assert.Equal((10, 9, 18m, 2m), await PositionAsync(fixture));
    }

    #endregion

    #region The pinned predicate

    /// <summary>
    /// The EF-translatable stock-consuming predicate and the Domain rule say the same thing for every
    /// status and every verification state, as <see cref="NayaxSaleCountingPolicy"/> requires - the
    /// same pin the completed-sale predicate has. Two rules, two predicates, two pins: a predicate
    /// that drifted from its rule would move a quantity or a total silently.
    /// </summary>
    [Fact]
    public async Task The_EF_stock_consuming_predicate_matches_the_domain_rule_for_every_state()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();
        var states = Enum.GetValues<NayaxSqsVerification>().Cast<NayaxSqsVerification?>().Append(null).ToList();
        var transactionId = 4100000200L;

        await using (var seed = fixture.Context())
        {
            foreach (var state in states)
            {
                foreach (var status in new int?[] { 12, 62, 26, 55, 250, 999, null })
                {
                    seed.NayaxSales.Add(new NayaxSales
                    {
                        TransactionID = transactionId++,
                        TransactionStatusId = status,
                        MachineID = NayaxSqsSaleFixture.MachineId,
                        SettlementValue = 1m,
                        MachineAuthorizationTime = NayaxSqsSaleFixture.SaleInstant,
                        Provider = ExternalProvider.Nayax,
                        SqsVerification = state,
                    });
                }
            }

            await seed.SaveChangesAsync();
        }

        var predicate = (Expression<Func<NayaxSales, bool>>)typeof(EfLatestNayaxSalesStore).Assembly
            .GetType("Inventory.Infrastructure.Data.EfNayaxSalesQueries", throwOnError: true)!
            .GetProperty("StockConsumingSalePredicate")!
            .GetValue(null)!;

        await using var read = fixture.Context();
        var consuming = (await read.NayaxSales.AsNoTracking()
            .Where(predicate).Select(row => row.Id).ToListAsync()).ToHashSet();
        var all = await read.NayaxSales.AsNoTracking().ToListAsync();

        Assert.All(all, sale => Assert.Equal(
            NayaxSaleCountingPolicy.ConsumesStock(sale.TransactionStatusId, sale.SqsVerification),
            consuming.Contains(sale.Id)));

        // And the two predicates really are different: the status-62 rows are the whole difference.
        Assert.NotEmpty(all.Where(sale =>
            NayaxSaleCountingPolicy.ConsumesStock(sale.TransactionStatusId, sale.SqsVerification)
            && !NayaxSaleCountingPolicy.IsCountedSale(sale.TransactionStatusId, sale.SqsVerification)));
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Stores the fixture's transaction as a completed SQS sale and drains the replay, which is what
    /// the reader does right after the commit.
    /// </summary>
    private static async Task<NayaxSqsSalePersistResult> PersistCompletedSaleAsync(
        NayaxSqsSaleFixture fixture, string messageId = "message-1")
    {
        await using var db = fixture.Context();
        var store = NayaxSqsSaleFixture.Store(db);

        var persisted = await store.PersistSqsSaleAsync(
            NayaxSqsSaleFixture.SqsSale(NayaxTransactionStatusIds.Completed, messageId: messageId),
            CancellationToken.None);

        await store.RebuildInventoryCostsAsync(CancellationToken.None);

        return persisted;
    }

    private static async Task<NayaxSqsRefundApplication> ApplyRefundAsync(
        NayaxSqsSaleFixture fixture, string messageId = "refund-message-1")
    {
        await using var db = fixture.Context();

        return await NayaxSqsSaleFixture.Store(db).ApplySqsRefundAsync(
            NayaxSqsSaleFixture.SqsRefund(messageId: messageId), CancellationToken.None);
    }

    /// <summary>
    /// Drains whatever is pending. Applying a refund records nothing to replay - the status change
    /// moves no quantity, value or cost - so for most of these cases this is deliberately a no-op,
    /// and asserting the figures after it is what shows that.
    /// </summary>
    private static async Task RebuildAsync(NayaxSqsSaleFixture fixture)
    {
        await using var db = fixture.Context();
        await NayaxSqsSaleFixture.Store(db).RebuildInventoryCostsAsync(CancellationToken.None);
    }

    /// <summary>
    /// Replays one product's inventory cost from the transition cutoff, as any later rebuild of that
    /// product does - the path a historical status-62 row is picked up by.
    /// </summary>
    private static async Task RebuildProductAsync(NayaxSqsSaleFixture fixture)
    {
        await using var db = fixture.Context();
        await TestCostingUseCases.Rebuild(db).RebuildAsync(
            NayaxSqsSaleFixture.ProductA,
            NayaxSqsSaleFixture.CutoffAt.AddSeconds(1),
            cancellationToken: CancellationToken.None);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// The product's whole position in one value - physical stock, costing quantity, inventory value
    /// and the sale's own unit cost - so a case states "nothing moved" as one comparison rather than
    /// four that could each be read past.
    /// </summary>
    private static async Task<(int QuantityInStock, int? CostingQuantity, decimal? InventoryValue, decimal? SaleUnitCost)>
        PositionAsync(NayaxSqsSaleFixture fixture)
    {
        await using var db = fixture.Context();
        var product = await db.Products.AsNoTracking()
            .SingleAsync(row => row.Id == NayaxSqsSaleFixture.ProductA);
        var saleCost = await db.NayaxSales.AsNoTracking()
            .Where(sale => sale.TransactionID == NayaxSqsSaleFixture.TransactionId)
            .Select(sale => sale.UnitCostAtSale)
            .FirstOrDefaultAsync();

        return (product.QuantityInStock, product.CostingQuantity, product.InventoryValue, saleCost);
    }

    private static async Task<DailyReportTotalsDto> DailyTotalsAsync(NayaxSqsSaleFixture fixture) =>
        Assert.IsType<DailyReportTotalsDto>((await DailyReportAsync(fixture)).Totals);

    private static async Task<DailyReportDto> DailyReportAsync(NayaxSqsSaleFixture fixture)
    {
        await using var db = fixture.Context();

        return await new GetDailyReport(
                new EfDailyReportFactsProvider(
                    db,
                    TestFinancialUseCases.ProcessingFees(db),
                    TestFinancialUseCases.TrivialCalendar))
            .Handle(
                new ReportingFilterDto(
                    NayaxSqsSaleFixture.SaleInstant.Date.AddDays(-7),
                    NayaxSqsSaleFixture.SaleInstant.Date.AddDays(7)),
                CancellationToken.None);
    }

    private static async Task<IReadOnlyList<ProductProfitabilityRowDto>> ProfitabilityRowsAsync(
        NayaxSqsSaleFixture fixture)
    {
        await using var db = fixture.Context();

        var report = await new GetProductProfitabilityReport(
                new EfProductProfitabilityReportFactsProvider(db),
                new EfProductPurchaseCostFactsProvider(db))
            .Handle(
                new ReportingFilterDto(
                    NayaxSqsSaleFixture.SaleInstant.Date.AddDays(-7),
                    NayaxSqsSaleFixture.SaleInstant.Date.AddDays(7)),
                CancellationToken.None);

        return report.Rows;
    }

    private static async Task<IReadOnlyList<TransactionSalesRowDto>> TransactionRowsAsync(
        NayaxSqsSaleFixture fixture)
    {
        await using var db = fixture.Context();

        var report = await new GetTransactionSalesReport(new EfTransactionSalesReportFactsProvider(db))
            .Handle(
                new TransactionSalesFilterDto(
                    From: NayaxSqsSaleFixture.SaleInstant.Date.AddDays(-7),
                    To: NayaxSqsSaleFixture.SaleInstant.Date.AddDays(7),

                    // Every status: the claim is that a refunded row stays *visible*, and the
                    // report's own default shows completed sales only.
                    Status: "all"),
                CancellationToken.None);

        return report.Rows;
    }

    #endregion
}
