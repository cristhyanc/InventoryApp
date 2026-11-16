using Inventory.Application.Reporting.Transactions;
using Inventory.Domain.FinancialConfiguration;
using TransactionCommissionBasis = Inventory.Domain.FinancialConfiguration.CommissionBasis;
using TransactionSaleStatus = Inventory.Domain.FinancialConfiguration.NayaxTransactionStatus;
using Inventory.Infrastructure.Reporting.Persistence;
using Inventory.Infrastructure.Data;
using Inventory.Application.Nayax;
using Inventory.Application.Time;
using Inventory.Infrastructure.Models;
using InventoryApi.Tests.Application.Time;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Relational SQLite tests: the transaction sales report needs every transaction status (not only
/// completed sales, unlike the already-migrated aggregate reports), date-range/machine scoping, and
/// raw effective-dated fee/commission facts, which depend on SQL translation.
///
/// <para>The provider runs the real <c>Australia/Sydney</c> calendar here, because since issue #1252
/// the requested dates are inclusive <em>business</em> dates and the sales are selected between the
/// UTC instants those Sydney days cover.</para>
/// </summary>
public class EfTransactionSalesReportFactsProviderTests
{
    private static readonly IBusinessCalendar Sydney =
        new FixedSydneyTime(new DateTime(2025, 8, 1, 3, 0, 0)).Calendar;

    private static EfTransactionSalesReportFactsProvider Provider(
        AppDbContext db, INayaxLynxClient? nayax = null) => new(db, Sydney, nayax);

    // The provider now returns Transactions as an asynchronous stream (see
    // TransactionSalesReportFacts), so tests materialise it explicitly instead of using it as a list.
    private static async Task<List<TransactionSalesReportFactsRow>> ToListAsync(
        IAsyncEnumerable<TransactionSalesReportFactsRow> rows)
    {
        var list = new List<TransactionSalesReportFactsRow>();
        await foreach (var row in rows) list.Add(row);
        return list;
    }

    private static async Task<AppDbContext> CreateSqliteAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var db = TestAppDbContext.Unrestricted(options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private sealed class FakeNayaxLynxClient : INayaxLynxClient
    {
        private readonly List<NayaxMachine> _machines;

        public FakeNayaxLynxClient(params NayaxMachine[] machines) => _machines = machines.ToList();

        public Task<List<NayaxMachine>> GetMachinesAsync(CancellationToken ct = default) => Task.FromResult(_machines);

        public Task<List<NayaxMachine>> GetMachinesFreshAsync(CancellationToken ct = default) => Task.FromResult(_machines);

        public Task<List<NayaxDevice>> GetDevicesAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<NayaxMachineProduct>> GetMachineProductsAsync(long machineId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<NayaxMachineProduct>> GetMachineProductsFreshAsync(long machineId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<NayaxMachineProduct>> CreateMachineProductsAsync(long machineId, List<NayaxMachineProduct> products, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<NayaxProduct>> GetProductsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<NayaxProductGroup>> GetProductGroupssAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<NayaxLastSalesReport>> GetMachineLastSalesAsync(long machineId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<NayaxMachine> GetMachineAsync(long machineId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<NayaxMachineAlert>> GetMachineLastAlertsAsync(long machineId, CancellationToken ct = default) => throw new NotImplementedException();
    }

    [Fact]
    public async Task Every_transaction_status_is_returned_not_only_completed_sales()
    {
        await using var db = await CreateSqliteAsync();
        db.NayaxSales.AddRange(
            new NayaxSales { TransactionID = 1, MachineID = 10, SettlementValue = 10m, TransactionStatusId = NayaxTransactionStatusIds.Completed, MachineAuthorizationTime = new DateTime(2025, 8, 1) },
            new NayaxSales { TransactionID = 2, MachineID = 10, SettlementValue = 5m, TransactionStatusId = NayaxTransactionStatusIds.PendingSettlementNotFinal, MachineAuthorizationTime = new DateTime(2025, 8, 1) },
            new NayaxSales { TransactionID = 3, MachineID = 10, SettlementValue = 4m, TransactionStatusId = NayaxTransactionStatusIds.Refunded, MachineAuthorizationTime = new DateTime(2025, 8, 1) },
            new NayaxSales { TransactionID = 4, MachineID = 10, SettlementValue = 3m, TransactionStatusId = null, MachineAuthorizationTime = new DateTime(2025, 8, 1) });
        await db.SaveChangesAsync();
        var provider = Provider(db);

        var facts = await provider.GetFactsAsync(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1), null, CancellationToken.None);
        var transactions = await ToListAsync(facts.Transactions);

        Assert.Equal(4, transactions.Count);
        Assert.Contains(transactions, x => x.TransactionId == 2 && x.Status == TransactionSaleStatus.Pending);
        Assert.Contains(transactions, x => x.TransactionId == 3 && x.Status == TransactionSaleStatus.Refunded);
        Assert.Contains(transactions, x => x.TransactionId == 4 && x.Status == TransactionSaleStatus.Unknown);
    }

    /// <summary>
    /// The requested range is a pair of inclusive <em>business</em> dates and its day boundary is
    /// Sydney midnight, not UTC midnight (issue #1252; this test asserted the UTC boundary before,
    /// which is the behaviour the repository owner's 13 November 2026 decision replaced).
    ///
    /// Sydney is +10 in August, so the business days 1 and 2 August 2025 run from 2025-07-31T14:00Z
    /// up to, exclusively, 2025-08-02T14:00Z. The instant one second before that start and the
    /// instant at the exclusive end are out; the start itself and the last instant before the next
    /// Sydney midnight are in.
    /// </summary>
    [Fact]
    public async Task Date_range_is_inclusive_of_both_boundary_business_days_and_excludes_the_day_after()
    {
        var startUtc = new DateTime(2025, 7, 31, 14, 0, 0, DateTimeKind.Utc);
        var endExclusiveUtc = new DateTime(2025, 8, 2, 14, 0, 0, DateTimeKind.Utc);
        Assert.Equal(startUtc, Sydney.StartOfBusinessDayUtc(new DateTime(2025, 8, 1)));
        Assert.Equal(endExclusiveUtc, Sydney.StartOfBusinessDayUtc(new DateTime(2025, 8, 3)));

        await using var db = await CreateSqliteAsync();
        db.NayaxSales.AddRange(
            new NayaxSales { TransactionID = 1, MachineID = 10, SettlementValue = 1m, MachineAuthorizationTime = startUtc.AddSeconds(-1), TransactionStatusId = NayaxTransactionStatusIds.Completed },
            new NayaxSales { TransactionID = 2, MachineID = 10, SettlementValue = 2m, MachineAuthorizationTime = startUtc, TransactionStatusId = NayaxTransactionStatusIds.Completed },
            new NayaxSales { TransactionID = 3, MachineID = 10, SettlementValue = 4m, MachineAuthorizationTime = endExclusiveUtc.AddSeconds(-1), TransactionStatusId = NayaxTransactionStatusIds.Completed },
            new NayaxSales { TransactionID = 4, MachineID = 10, SettlementValue = 8m, MachineAuthorizationTime = endExclusiveUtc, TransactionStatusId = NayaxTransactionStatusIds.Completed });
        await db.SaveChangesAsync();
        var provider = Provider(db);

        var facts = await provider.GetFactsAsync(new DateTime(2025, 8, 1), new DateTime(2025, 8, 2), null, CancellationToken.None);
        var transactions = await ToListAsync(facts.Transactions);

        Assert.Equal(new long[] { 2, 3 }, transactions.Select(x => x.TransactionId).OrderBy(x => x));
        // Each row also carries the Sydney date its instant falls on, which is what the use case
        // resolves the sale's fee rate and commission agreement by.
        Assert.Equal(
            [new DateTime(2025, 8, 1), new DateTime(2025, 8, 2)],
            transactions.OrderBy(x => x.TransactionId).Select(x => x.BusinessDate).ToList());
    }

    [Fact]
    public async Task Machine_filter_scopes_transactions_to_the_selected_machine_only()
    {
        await using var db = await CreateSqliteAsync();
        db.NayaxSales.AddRange(
            new NayaxSales { TransactionID = 1, MachineID = 10, SettlementValue = 10m, MachineAuthorizationTime = new DateTime(2025, 8, 1), TransactionStatusId = NayaxTransactionStatusIds.Completed },
            new NayaxSales { TransactionID = 2, MachineID = 20, SettlementValue = 30m, MachineAuthorizationTime = new DateTime(2025, 8, 1), TransactionStatusId = NayaxTransactionStatusIds.Completed });
        await db.SaveChangesAsync();
        var provider = Provider(db);

        var facts = await provider.GetFactsAsync(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1), 10, CancellationToken.None);
        var transactions = await ToListAsync(facts.Transactions);

        Assert.Equal(1, Assert.Single(transactions).TransactionId);
    }

    [Fact]
    public async Task Persisted_cost_flag_and_cost_source_are_mapped_from_the_sale()
    {
        await using var db = await CreateSqliteAsync();
        db.NayaxSales.Add(new NayaxSales
        {
            TransactionID = 1,
            MachineID = 10,
            SettlementValue = 10m,
            MachineAuthorizationTime = new DateTime(2025, 8, 1),
            TransactionStatusId = NayaxTransactionStatusIds.Completed,
            CostOfGoodsSold = 4m,
            CostingStatus = SaleCostingStatus.Costed,
            CostSource = SaleCostSource.InventoryLedger
        });
        await db.SaveChangesAsync();
        var provider = Provider(db);

        var facts = await provider.GetFactsAsync(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1), null, CancellationToken.None);
        var transactions = await ToListAsync(facts.Transactions);

        var row = Assert.Single(transactions);
        Assert.True(row.HasPersistedCost);
        Assert.Equal("Inventory Ledger", row.CostSource);
        Assert.Equal("Costed", row.CostingStatus);
    }

    [Fact]
    public async Task Fee_rates_up_to_the_range_end_and_all_commission_agreements_are_returned()
    {
        await using var db = await CreateSqliteAsync();
        db.NayaxProcessingFeeRates.Add(new NayaxProcessingFeeRate { EffectiveFrom = new DateTime(2025, 1, 1), FeeExGst = 0.2m });
        db.NayaxProcessingFeeRates.Add(new NayaxProcessingFeeRate { EffectiveFrom = new DateTime(2025, 12, 1), FeeExGst = 0.9m });
        db.SiteCommissionAgreements.Add(new SiteCommissionAgreement
        {
            SiteId = 91,
            EffectiveFrom = new DateTime(2025, 1, 1),
            CommissionRate = 0.1m,
            Basis = CommissionBasis.CardSales
        });
        await db.SaveChangesAsync();
        var provider = Provider(db);

        var facts = await provider.GetFactsAsync(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1), null, CancellationToken.None);

        Assert.Equal(0.2m, Assert.Single(facts.FeeRates).FeeExGst);
        var agreement = Assert.Single(facts.CommissionAgreements);
        Assert.Equal(91, agreement.SiteId);
        Assert.Equal(TransactionCommissionBasis.CardSales, agreement.Basis);
    }

    [Fact]
    public async Task Site_and_product_are_resolved_from_the_live_nayax_machine_directory_and_catalogue()
    {
        await using var db = await CreateSqliteAsync();
        db.Products.Add(new Product { Id = 1, Name = "Water" });
        db.NayaxSales.Add(new NayaxSales
        {
            TransactionID = 1,
            MachineID = 10,
            MachineName = "Alpha One",
            NayaxProductId = 1,
            ProductName = "Water",
            SettlementValue = 10m,
            MachineAuthorizationTime = new DateTime(2025, 8, 1),
            TransactionStatusId = NayaxTransactionStatusIds.Completed
        });
        await db.SaveChangesAsync();
        var nayax = new FakeNayaxLynxClient(new NayaxMachine { MachineID = 10, MachineName = "Alpha One", CustomerID = 91 });
        var provider = Provider(db, nayax);

        var facts = await provider.GetFactsAsync(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1), null, CancellationToken.None);
        var transactions = await ToListAsync(facts.Transactions);

        var row = Assert.Single(transactions);
        Assert.Equal(91, row.SiteId);
        Assert.Equal("Alpha", row.SiteName);
        Assert.False(facts.SiteMappingUnavailable);
        Assert.Equal(1, Assert.Single(facts.ProductCatalogue).Id);
    }

    [Fact]
    public async Task Missing_nayax_client_makes_site_mapping_unavailable()
    {
        await using var db = await CreateSqliteAsync();
        db.NayaxSales.Add(new NayaxSales
        {
            TransactionID = 1,
            MachineID = 10,
            SettlementValue = 10m,
            MachineAuthorizationTime = new DateTime(2025, 8, 1),
            TransactionStatusId = NayaxTransactionStatusIds.Completed
        });
        await db.SaveChangesAsync();
        var provider = Provider(db, nayax: null);

        var facts = await provider.GetFactsAsync(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1), null, CancellationToken.None);
        var transactions = await ToListAsync(facts.Transactions);

        Assert.True(facts.SiteMappingUnavailable);
        Assert.Null(Assert.Single(transactions).SiteId);
    }

    [Fact]
    public async Task Cancellation_during_enumeration_propagates_and_stops_the_stream_before_every_row_is_consumed()
    {
        await using var db = await CreateSqliteAsync();
        db.NayaxSales.AddRange(
            new NayaxSales { TransactionID = 1, MachineID = 10, SettlementValue = 10m, MachineAuthorizationTime = new DateTime(2025, 8, 1), TransactionStatusId = NayaxTransactionStatusIds.Completed },
            new NayaxSales { TransactionID = 2, MachineID = 10, SettlementValue = 10m, MachineAuthorizationTime = new DateTime(2025, 8, 1), TransactionStatusId = NayaxTransactionStatusIds.Completed },
            new NayaxSales { TransactionID = 3, MachineID = 10, SettlementValue = 10m, MachineAuthorizationTime = new DateTime(2025, 8, 1), TransactionStatusId = NayaxTransactionStatusIds.Completed });
        await db.SaveChangesAsync();
        var provider = Provider(db);
        using var cts = new CancellationTokenSource();

        var facts = await provider.GetFactsAsync(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1), null, cts.Token);

        var seen = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in facts.Transactions.WithCancellation(cts.Token))
            {
                seen++;
                cts.Cancel();
            }
        });

        // Only the row seen before cancellation was pulled: the stream is a one-pass, row-at-a-time
        // enumeration of the EF query rather than a list completed up front before returning.
        Assert.Equal(1, seen);
    }
}
