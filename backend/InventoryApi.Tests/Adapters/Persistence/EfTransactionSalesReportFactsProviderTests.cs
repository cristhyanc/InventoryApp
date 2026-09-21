using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Inventory.Domain.Reporting.Transactions;
using InventoryApi.Adapters.Persistence;
using InventoryApi.Data;
using InventoryApi.Integrations.Nayax;
using InventoryApi.Models;
using InventoryApi.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Relational SQLite tests: the transaction sales report needs every transaction status (not only
/// completed sales, unlike the already-migrated aggregate reports), date-range/machine scoping, and
/// raw effective-dated fee/commission facts, which depend on SQL translation.
/// </summary>
public class EfTransactionSalesReportFactsProviderTests
{
    private static async Task<AppDbContext> CreateSqliteAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private sealed class FakeNayaxLynxClient : INayaxLynxClient
    {
        private readonly List<NayaxMachine> _machines;

        public FakeNayaxLynxClient(params NayaxMachine[] machines) => _machines = machines.ToList();

        public Task<List<NayaxMachine>> GetMachinesAsync(CancellationToken ct = default) => Task.FromResult(_machines);

        public Task<List<NayaxDevice>> GetDevicesAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<NayaxMachineProduct>> GetMachineProductsAsync(long machineId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<NayaxMachineProduct>> CreateMachineProductsAsync(long machineId, List<NayaxMachineProduct> products, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<NayaxProduct>> GetProductsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<NayaxProductGroup>> GetProductGroupssAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<NayaxLastSalesReport>> GetMachineLastSalesAsync(long machineId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<NayaxMachine> GetMachineAsync(long machineId, CancellationToken ct = default) => throw new NotImplementedException();
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
        var provider = new EfTransactionSalesReportFactsProvider(db);

        var facts = await provider.GetFactsAsync(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1), null, CancellationToken.None);

        Assert.Equal(4, facts.Transactions.Count);
        Assert.Contains(facts.Transactions, x => x.TransactionId == 2 && x.Status == TransactionSaleStatus.Pending);
        Assert.Contains(facts.Transactions, x => x.TransactionId == 3 && x.Status == TransactionSaleStatus.Refunded);
        Assert.Contains(facts.Transactions, x => x.TransactionId == 4 && x.Status == TransactionSaleStatus.Unknown);
    }

    [Fact]
    public async Task Date_range_is_inclusive_of_both_boundary_days_and_excludes_the_day_after()
    {
        await using var db = await CreateSqliteAsync();
        db.NayaxSales.AddRange(
            new NayaxSales { TransactionID = 1, MachineID = 10, SettlementValue = 1m, MachineAuthorizationTime = new DateTime(2025, 7, 31, 23, 59, 0), TransactionStatusId = NayaxTransactionStatusIds.Completed },
            new NayaxSales { TransactionID = 2, MachineID = 10, SettlementValue = 2m, MachineAuthorizationTime = new DateTime(2025, 8, 1, 0, 0, 0), TransactionStatusId = NayaxTransactionStatusIds.Completed },
            new NayaxSales { TransactionID = 3, MachineID = 10, SettlementValue = 4m, MachineAuthorizationTime = new DateTime(2025, 8, 2, 0, 0, 0), TransactionStatusId = NayaxTransactionStatusIds.Completed },
            new NayaxSales { TransactionID = 4, MachineID = 10, SettlementValue = 8m, MachineAuthorizationTime = new DateTime(2025, 8, 3, 0, 0, 0), TransactionStatusId = NayaxTransactionStatusIds.Completed });
        await db.SaveChangesAsync();
        var provider = new EfTransactionSalesReportFactsProvider(db);

        var facts = await provider.GetFactsAsync(new DateTime(2025, 8, 1), new DateTime(2025, 8, 2), null, CancellationToken.None);

        Assert.Equal(new long[] { 2, 3 }, facts.Transactions.Select(x => x.TransactionId).OrderBy(x => x));
    }

    [Fact]
    public async Task Machine_filter_scopes_transactions_to_the_selected_machine_only()
    {
        await using var db = await CreateSqliteAsync();
        db.NayaxSales.AddRange(
            new NayaxSales { TransactionID = 1, MachineID = 10, SettlementValue = 10m, MachineAuthorizationTime = new DateTime(2025, 8, 1), TransactionStatusId = NayaxTransactionStatusIds.Completed },
            new NayaxSales { TransactionID = 2, MachineID = 20, SettlementValue = 30m, MachineAuthorizationTime = new DateTime(2025, 8, 1), TransactionStatusId = NayaxTransactionStatusIds.Completed });
        await db.SaveChangesAsync();
        var provider = new EfTransactionSalesReportFactsProvider(db);

        var facts = await provider.GetFactsAsync(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1), 10, CancellationToken.None);

        Assert.Equal(1, Assert.Single(facts.Transactions).TransactionId);
    }

    [Fact]
    public async Task Persisted_cost_flag_and_cost_source_are_mapped_from_the_sale()
    {
        await using var db = await CreateSqliteAsync();
        db.NayaxSales.Add(new NayaxSales
        {
            TransactionID = 1, MachineID = 10, SettlementValue = 10m, MachineAuthorizationTime = new DateTime(2025, 8, 1),
            TransactionStatusId = NayaxTransactionStatusIds.Completed, CostOfGoodsSold = 4m,
            CostingStatus = SaleCostingStatus.Costed, CostSource = SaleCostSource.InventoryLedger
        });
        await db.SaveChangesAsync();
        var provider = new EfTransactionSalesReportFactsProvider(db);

        var facts = await provider.GetFactsAsync(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1), null, CancellationToken.None);

        var row = Assert.Single(facts.Transactions);
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
            SiteId = 91, EffectiveFrom = new DateTime(2025, 1, 1), CommissionRate = 0.1m, Basis = CommissionBasis.CardSales
        });
        await db.SaveChangesAsync();
        var provider = new EfTransactionSalesReportFactsProvider(db);

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
            TransactionID = 1, MachineID = 10, MachineName = "Alpha One", NayaxProductId = 1, ProductName = "Water",
            SettlementValue = 10m, MachineAuthorizationTime = new DateTime(2025, 8, 1), TransactionStatusId = NayaxTransactionStatusIds.Completed
        });
        await db.SaveChangesAsync();
        var nayax = new FakeNayaxLynxClient(new NayaxMachine { MachineID = 10, MachineName = "Alpha One", CustomerID = 91 });
        var provider = new EfTransactionSalesReportFactsProvider(db, nayax);

        var facts = await provider.GetFactsAsync(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1), null, CancellationToken.None);

        var row = Assert.Single(facts.Transactions);
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
            TransactionID = 1, MachineID = 10, SettlementValue = 10m,
            MachineAuthorizationTime = new DateTime(2025, 8, 1), TransactionStatusId = NayaxTransactionStatusIds.Completed
        });
        await db.SaveChangesAsync();
        var provider = new EfTransactionSalesReportFactsProvider(db, null);

        var facts = await provider.GetFactsAsync(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1), null, CancellationToken.None);

        Assert.True(facts.SiteMappingUnavailable);
        Assert.Null(Assert.Single(facts.Transactions).SiteId);
    }
}
