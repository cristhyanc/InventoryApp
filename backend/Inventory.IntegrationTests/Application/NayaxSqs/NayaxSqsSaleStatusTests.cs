using Inventory.Application.Reporting.ProductProfitability;
using Inventory.Application.Reporting.Shared;
using Inventory.Application.Reporting.Transactions;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Domain.NayaxSqs;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using Inventory.Infrastructure.Reporting.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Application.NayaxSqs;

/// <summary>
/// Issue #1103's required status regression, over relational SQLite and the real store, the real sale
/// costing and the real inventory-cost replay.
///
/// <para><strong>The defect this is about.</strong> The store derived every inserted sale's status
/// from its amount: a positive settlement value was a completed sale (<c>12</c>) and anything else was
/// cancelled (<c>250</c>). That rule existed because the <c>lastSales</c> payload carries no status
/// field at all. An SQS message does carry one, so <strong>a declined transaction that charged money -
/// status <c>26</c> with a positive <c>SeValue</c> - would have been stored as a completed sale</strong>,
/// counted as revenue and consumed a unit of costing inventory that was never sold. That is the case
/// the two rules disagree about, and it is the first test below.</para>
///
/// <para>The variants matter as much: the <c>lastSales</c> path must keep the amount rule exactly,
/// because it supplies no status and the documented response has no status field, and neither path may
/// ever overwrite a status the other decided.</para>
/// </summary>
public class NayaxSqsSaleStatusTests
{
    #region The required regression

    /// <summary>
    /// <strong>The regression.</strong> A cancelled/declined SQS message (<c>26</c>) that charged
    /// $3.50 is saved with status <c>26</c>, is not a completed sale, consumes no costing inventory
    /// and adds no revenue.
    /// </summary>
    [Fact]
    public async Task A_declined_sqs_message_that_charged_money_is_saved_as_declined_and_consumes_nothing()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();

        await PersistSqsAsync(fixture, NayaxTransactionStatusIds.CancelledOrDeclined26);

        await using var db = fixture.Context();
        var sale = await db.NayaxSales.AsNoTracking().SingleAsync();

        Assert.Equal(NayaxTransactionStatusIds.CancelledOrDeclined26, sale.TransactionStatusId);
        Assert.False(NayaxTransactionStatusClassifier.IsCompletedSale(sale.TransactionStatusId));
        Assert.Equal(3.5m, sale.SettlementValue);

        // Not costed, because only a completed sale has a historical cost at all.
        Assert.Equal(SaleCostingStatus.Pending, sale.CostingStatus);
        Assert.Null(sale.CostOfGoodsSold);

        // And no unit of business-owned costing inventory left: the replay consumes completed sales
        // and nothing else.
        var product = await db.Products.AsNoTracking().SingleAsync(row => row.Id == NayaxSqsSaleFixture.ProductA);
        Assert.Equal(10, product.CostingQuantity);
        Assert.Equal(20m, product.InventoryValue);

        // No revenue either: the profitability report reads completed sales, so the product has no row.
        Assert.Empty(await ProfitabilityRowsAsync(fixture));

        // It is still visible in status reporting rather than discarded (AGENTS.md § Sales, payment
        // methods, and statuses).
        var reported = Assert.Single(await TransactionRowsAsync(fixture));
        Assert.Equal(NayaxTransactionStatusIds.CancelledOrDeclined26, reported.TransactionStatusId);
        Assert.False(reported.IsCompleted);
    }

    /// <summary>
    /// The other side of the same rule: a completed SQS message is saved as <c>12</c> and consumes
    /// exactly one unit, once, at the product's own AVCO cost.
    /// </summary>
    [Fact]
    public async Task A_completed_sqs_message_is_saved_as_completed_and_consumes_one_unit_once()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();

        await PersistSqsAsync(fixture, NayaxTransactionStatusIds.Completed);

        await using var db = fixture.Context();
        var sale = await db.NayaxSales.AsNoTracking().SingleAsync();

        Assert.Equal(NayaxTransactionStatusIds.Completed, sale.TransactionStatusId);
        Assert.Equal(NayaxSqsSaleFixture.ProductA, sale.ProductId);
        Assert.Equal(2m, sale.CostOfGoodsSold);
        Assert.Equal(SaleCostSource.InventoryLedger, sale.CostSource);

        var product = await db.Products.AsNoTracking().SingleAsync(row => row.Id == NayaxSqsSaleFixture.ProductA);
        Assert.Equal(9, product.CostingQuantity);
        Assert.Equal(18m, product.InventoryValue);

        var row = Assert.Single(await ProfitabilityRowsAsync(fixture));
        Assert.Equal(NayaxSqsSaleFixture.ProductA, row.ProductId);
        Assert.Equal(2m, row.CostOfGoods);
    }

    /// <summary>
    /// A redelivered message consumes nothing a second time: the transaction is already stored, the
    /// database's unique index on <c>(BusinessId, TransactionID)</c> is what says so, and the enrich
    /// path finds nothing left to fill in.
    /// </summary>
    [Fact]
    public async Task A_redelivered_completed_message_does_not_consume_a_second_unit()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();

        var first = await PersistSqsAsync(fixture, NayaxTransactionStatusIds.Completed);
        var second = await PersistSqsAsync(
            fixture, NayaxTransactionStatusIds.Completed, messageId: "message-2");

        Assert.True(first.Inserted);
        Assert.False(second.Inserted);

        await using var db = fixture.Context();
        Assert.Single(await db.NayaxSales.AsNoTracking().ToListAsync());

        var product = await db.Products.AsNoTracking().SingleAsync(row => row.Id == NayaxSqsSaleFixture.ProductA);
        Assert.Equal(9, product.CostingQuantity);
    }

    #endregion

    #region The lastSales rule is unchanged

    /// <summary>
    /// A <c>lastSales</c> item supplies no status, so it keeps the settlement-value rule exactly: a
    /// positive amount is completed and anything else is cancelled. The documented response for
    /// <c>GET /v1/machines/{MachineID}/lastSales</c> has no status field at all.
    /// </summary>
    [Theory]
    [InlineData(3.5, NayaxTransactionStatusIds.Completed)]
    [InlineData(0, NayaxTransactionStatusIds.CancelledOrDeclined250)]
    public async Task A_lastSales_item_still_takes_its_status_from_its_amount(
        double settlementValue, int expectedStatus)
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();

        await using (var db = fixture.Context())
        {
            await NayaxSqsSaleFixture.Store(db).PersistLatestSalesAsync(
                [NayaxSqsSaleFixture.LastSalesItem(settlementValue: (decimal)settlementValue)],
                CancellationToken.None);
        }

        await using var read = fixture.Context();
        Assert.Equal(
            expectedStatus,
            (await read.NayaxSales.AsNoTracking().SingleAsync()).TransactionStatusId);
    }

    /// <summary>
    /// And a <c>lastSales</c> insert records no SQS provenance at all, which is what keeps "did not
    /// arrive from the queue" and "arrived from the queue" apart.
    /// </summary>
    [Fact]
    public async Task A_lastSales_insert_records_no_sqs_provenance()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();

        await using (var db = fixture.Context())
        {
            await NayaxSqsSaleFixture.Store(db).PersistLatestSalesAsync(
                [NayaxSqsSaleFixture.LastSalesItem()], CancellationToken.None);
        }

        await using var read = fixture.Context();
        var sale = await read.NayaxSales.AsNoTracking().SingleAsync();

        Assert.Null(sale.ReceivedViaSqsAtUtc);
        Assert.Null(sale.SqsFirstMessageId);
        Assert.Null(sale.SqsVerification);
    }

    #endregion

    #region Enrichment never overwrites a decided status

    /// <summary>
    /// A stored row that already carries a status keeps it, whichever path supplies a different one.
    /// A later difference between the two paths is issue #1104's comparison to record, not this
    /// store's to resolve - and silently rewriting a financial status would make that comparison
    /// impossible.
    /// </summary>
    [Fact]
    public async Task A_stored_status_is_not_overwritten_by_a_lastSales_item_that_disagrees()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();

        await PersistSqsAsync(fixture, NayaxTransactionStatusIds.CancelledOrDeclined26);

        await using (var db = fixture.Context())
        {
            // The same transaction through the other path, whose amount rule would say "completed".
            await NayaxSqsSaleFixture.Store(db).PersistLatestSalesAsync(
                [NayaxSqsSaleFixture.LastSalesItem()], CancellationToken.None);
        }

        await using var read = fixture.Context();
        var sale = await read.NayaxSales.AsNoTracking().SingleAsync();

        Assert.Equal(NayaxTransactionStatusIds.CancelledOrDeclined26, sale.TransactionStatusId);

        var product = await read.Products.AsNoTracking().SingleAsync(row => row.Id == NayaxSqsSaleFixture.ProductA);
        Assert.Equal(10, product.CostingQuantity);
    }

    /// <summary>And the same claim with the paths the other way round.</summary>
    [Fact]
    public async Task A_stored_status_is_not_overwritten_by_an_sqs_message_that_disagrees()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();

        await using (var db = fixture.Context())
        {
            await NayaxSqsSaleFixture.Store(db).PersistLatestSalesAsync(
                [NayaxSqsSaleFixture.LastSalesItem()], CancellationToken.None);
        }

        var result = await PersistSqsAsync(fixture, NayaxTransactionStatusIds.CancelledOrDeclined26);

        Assert.False(result.Inserted);

        await using var read = fixture.Context();
        var sale = await read.NayaxSales.AsNoTracking().SingleAsync();

        Assert.Equal(NayaxTransactionStatusIds.Completed, sale.TransactionStatusId);

        // And the row it enriched is still not an SQS-created row: the provenance belongs to the
        // insert that created it.
        Assert.Null(sale.ReceivedViaSqsAtUtc);
        Assert.Null(sale.SqsFirstMessageId);
        Assert.Null(sale.SqsVerification);
    }

    /// <summary>
    /// A stored row whose status is <c>NULL</c> takes the supplied one, exactly as the amount-derived
    /// fallback already filled it. That is the one case enrichment writes a status at all.
    /// </summary>
    [Fact]
    public async Task A_stored_row_with_no_status_takes_the_status_the_message_supplies()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();
        await InsertStatuslessSaleAsync(fixture);

        var result = await PersistSqsAsync(fixture, NayaxTransactionStatusIds.CancelledOrDeclined26);

        Assert.False(result.Inserted);

        await using var read = fixture.Context();
        Assert.Equal(
            NayaxTransactionStatusIds.CancelledOrDeclined26,
            (await read.NayaxSales.AsNoTracking().SingleAsync()).TransactionStatusId);
    }

    /// <summary>
    /// And a <c>lastSales</c> enrichment of the same row still fills it from the amount, because that
    /// path supplies nothing - the fallback is for "no status supplied", not for "lastSales".
    /// </summary>
    [Fact]
    public async Task A_stored_row_with_no_status_takes_the_amount_rule_from_a_lastSales_item()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();
        await InsertStatuslessSaleAsync(fixture);

        await using (var db = fixture.Context())
        {
            await NayaxSqsSaleFixture.Store(db).PersistLatestSalesAsync(
                [NayaxSqsSaleFixture.LastSalesItem()], CancellationToken.None);
        }

        await using var read = fixture.Context();
        Assert.Equal(
            NayaxTransactionStatusIds.Completed,
            (await read.NayaxSales.AsNoTracking().SingleAsync()).TransactionStatusId);
    }

    #endregion

    #region Provenance

    /// <summary>
    /// The provenance arrives on the same insert as the row: there is no moment at which a
    /// queue-sourced sale exists without saying so, and <c>SqsVerification</c> starts at
    /// <c>Unverified</c> for issue #1104 to move.
    /// </summary>
    [Fact]
    public async Task An_sqs_insert_records_its_provenance_on_the_same_insert()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();

        await PersistSqsAsync(fixture, NayaxTransactionStatusIds.Completed);

        await using var db = fixture.Context();
        var sale = await db.NayaxSales.AsNoTracking().SingleAsync();

        Assert.Equal(NayaxSqsSaleFixture.ReceivedAtUtc, sale.ReceivedViaSqsAtUtc);
        Assert.Equal("message-1", sale.SqsFirstMessageId);
        Assert.Equal(NayaxSqsVerification.Unverified, sale.SqsVerification);
    }

    /// <summary>
    /// A redelivery leaves the first delivery's message id alone: the column is provenance ("this
    /// delivery created the sale"), not a moving record of the last thing that touched the row.
    /// </summary>
    [Fact]
    public async Task A_redelivery_does_not_rewrite_the_first_message_id()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();

        await PersistSqsAsync(fixture, NayaxTransactionStatusIds.Completed);
        await PersistSqsAsync(
            fixture,
            NayaxTransactionStatusIds.Completed,
            messageId: "message-2",
            receivedAtUtc: NayaxSqsSaleFixture.ReceivedAtUtc.AddMinutes(5));

        await using var db = fixture.Context();
        var sale = await db.NayaxSales.AsNoTracking().SingleAsync();

        Assert.Equal("message-1", sale.SqsFirstMessageId);
        Assert.Equal(NayaxSqsSaleFixture.ReceivedAtUtc, sale.ReceivedViaSqsAtUtc);
    }

    #endregion

    private static async Task<Inventory.Application.SalesSync.NayaxSqsSalePersistResult> PersistSqsAsync(
        NayaxSqsSaleFixture fixture,
        int statusId,
        string messageId = "message-1",
        DateTime? receivedAtUtc = null)
    {
        await using var db = fixture.Context();
        var store = NayaxSqsSaleFixture.Store(db);

        var persisted = await store.PersistSqsSaleAsync(
            NayaxSqsSaleFixture.SqsSale(statusId, messageId: messageId, receivedAtUtc: receivedAtUtc),
            CancellationToken.None);

        // The reader drains the pending rebuilds right after the commit, so these cases do the same:
        // what a stored sale did to costing inventory is only visible once the replay has run.
        await store.RebuildInventoryCostsAsync(CancellationToken.None);

        return persisted;
    }

    /// <summary>
    /// A stored sale whose status is <c>NULL</c> - the state the amount-derived fallback was written
    /// for. Nothing in the application inserts one today, so it is written directly.
    /// </summary>
    private static async Task InsertStatuslessSaleAsync(NayaxSqsSaleFixture fixture)
    {
        await using var db = fixture.Context();
        db.NayaxSales.Add(new NayaxSales
        {
            TransactionID = NayaxSqsSaleFixture.TransactionId,
            TransactionStatusId = null,
            MachineID = NayaxSqsSaleFixture.MachineId,
            ProductId = NayaxSqsSaleFixture.ProductA,
            Provider = Inventory.Domain.Providers.ExternalProvider.Nayax,
            NayaxProductId = NayaxSqsSaleFixture.NayaxProductId,
            ProviderProductIdIsRaw = true,
            SettlementValue = 3.5m,
            MachineAuthorizationTime = NayaxSqsSaleFixture.SaleInstant,
        });
        await db.SaveChangesAsync();
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

                    // Every status, because the claim is that a declined transaction stays
                    // *visible* in status reporting: the report's own default shows completed sales
                    // only, which would hide exactly the row this asserts is there.
                    Status: "all"),
                CancellationToken.None);

        return report.Rows;
    }
}
