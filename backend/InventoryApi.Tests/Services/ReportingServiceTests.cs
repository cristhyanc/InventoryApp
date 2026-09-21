using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Inventory.Application.Reporting.Shared;
using Inventory.Application.Reporting.Transactions;
using InventoryApi.Data;
using InventoryApi.DTOs;
using InventoryApi.Integrations.Nayax;
using InventoryApi.Models;
using InventoryApi.Services;
using InventoryApi.Services.Interfaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Services;

public class ReportingServiceTests
{
    private static AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static ReportingService Reporting(AppDbContext db, INayaxLynxClient nayaxLynxClient = null,
        ISiteCommissionService siteCommissionService = null)
    {
        var commissions = siteCommissionService is null ? new Mock<ISiteCommissionService>() : null;
        commissions?.Setup(x => x.GetReportAsync(
                It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTime from, DateTime to, long? _, CancellationToken _) =>
                new SiteCommissionReportDto(from, to, []));
        return new ReportingService(db, new NayaxProcessingFeeService(db),
            siteCommissionService ?? commissions!.Object, nayaxLynxClient);
    }

    [Fact]
    public async Task Transaction_sales_uses_filters_estimated_fees_commission_and_full_totals()
    {
        using var db = CreateDbContext();
        db.Products.Add(new Product { Id = 1, Name = "Water", UnitPrice = 3m });
        db.NayaxProcessingFeeRates.Add(new NayaxProcessingFeeRate { EffectiveFrom = new DateTime(2025, 1, 1), FeeExGst = .20m });
        db.SiteCommissionAgreements.Add(new SiteCommissionAgreement
        {
            SiteId = 91, EffectiveFrom = new DateTime(2025, 1, 1), CommissionRate = .10m,
            Basis = CommissionBasis.CardSales
        });
        db.NayaxSales.AddRange(
            new NayaxSales { TransactionID = 1, MachineID = 10, MachineName = "Alpha One", NayaxProductId = 1, ProductName = "Water", PaymentMethod = "Credit Card", SettlementValue = 10m, NayaxProductCostPrice = 4.10m, UnitCostAtSale = 4m, CostOfGoodsSold = 4m, CostingStatus = SaleCostingStatus.Costed, CostSource = SaleCostSource.InventoryLedger, TransactionStatusId = NayaxTransactionStatusIds.Completed, MachineAuthorizationTime = new DateTime(2025, 8, 1) },
            new NayaxSales { TransactionID = 2, MachineID = 10, MachineName = "Alpha One", NayaxProductId = 1, ProductName = "Water", PaymentMethod = "Cash", SettlementValue = 5m, UnitCostAtSale = 2m, CostOfGoodsSold = 2m, CostingStatus = SaleCostingStatus.Costed, TransactionStatusId = NayaxTransactionStatusIds.Completed, MachineAuthorizationTime = new DateTime(2025, 8, 1) },
            new NayaxSales { TransactionID = 3, MachineID = 10, MachineName = "Alpha One", NayaxProductId = 1, ProductName = "Water", PaymentMethod = "Credit Card", SettlementValue = 2m, TransactionStatusId = NayaxTransactionStatusIds.Completed, MachineAuthorizationTime = new DateTime(2025, 8, 1) },
            new NayaxSales { TransactionID = 4, MachineID = 10, NayaxProductId = 1, PaymentMethod = "Credit Card", SettlementValue = 9m, TransactionStatusId = NayaxTransactionStatusIds.PendingSettlementNotFinal, MachineAuthorizationTime = new DateTime(2025, 8, 1) });
        await db.SaveChangesAsync();

        var service = Reporting(db, new TransactionTestNayaxClient());
        var report = await service.GetTransactionsAsync(new TransactionSalesFilterDto(
            new DateTime(2025, 8, 1), new DateTime(2025, 8, 1), Page: 2, PageSize: 50));

        Assert.Empty(report.Rows);
        Assert.Equal(3, report.TotalCount);
        Assert.Equal(17m, report.Totals.Sales);
        Assert.False(report.Totals.IsCogsComplete);
        Assert.Null(report.Totals.GrossProfit);

        var costedCard = await service.GetTransactionsAsync(new TransactionSalesFilterDto(
            new DateTime(2025, 8, 1), new DateTime(2025, 8, 1), ProductId: 1, PaymentType: "card", CogsStatus: "costed"));
        var row = Assert.Single(costedCard.Rows);
        Assert.Equal(1m, row.CommissionAmount);
        Assert.Equal(.20m, row.FeeExGst);
        Assert.Equal(.22m, row.FeeIncGst);
        Assert.Equal(4.78m, row.DirectProfit);
        Assert.Equal("Estimated", row.FeeSource);
        Assert.Equal(4.10m, row.NayaxProductCostPrice);
        Assert.Equal("Inventory Ledger", row.CostSource);

        var cash = await service.GetTransactionsAsync(new TransactionSalesFilterDto(
            new DateTime(2025, 8, 1), new DateTime(2025, 8, 1), PaymentType: "cash", CogsStatus: "costed"));
        var cashRow = Assert.Single(cash.Rows);
        Assert.Equal(0m, cashRow.FeeIncGst);
        Assert.Equal("Not applicable", cashRow.FeeSource);
        Assert.Equal(0m, cashRow.CommissionAmount);

        var pending = await service.GetTransactionsAsync(new TransactionSalesFilterDto(
            new DateTime(2025, 8, 1), new DateTime(2025, 8, 1), Status: "pending"));
        Assert.Equal(4, Assert.Single(pending.Rows).TransactionId);
    }

    private sealed class TransactionTestNayaxClient : INayaxLynxClient
    {
        private readonly List<NayaxMachine> _machines;

        public TransactionTestNayaxClient(params NayaxMachine[] machines) =>
            _machines = machines.Length == 0
                ? [new NayaxMachine { MachineID = 10, MachineName = "Alpha One", CustomerID = 91 }]
                : new List<NayaxMachine>(machines);

        public Task<List<NayaxDevice>> GetDevicesAsync(CancellationToken ct = default) => Task.FromResult(new List<NayaxDevice>());
        public Task<List<NayaxMachine>> GetMachinesAsync(CancellationToken ct = default) => Task.FromResult(_machines);
        public Task<List<NayaxMachineProduct>> GetMachineProductsAsync(long machineId, CancellationToken ct = default) => Task.FromResult(new List<NayaxMachineProduct>());
        public Task<List<NayaxMachineProduct>> CreateMachineProductsAsync(long machineId, List<NayaxMachineProduct> products, CancellationToken ct = default) => Task.FromResult(products);
        public Task<List<NayaxProduct>> GetProductsAsync(CancellationToken ct = default) => Task.FromResult(new List<NayaxProduct>());
        public Task<List<NayaxProductGroup>> GetProductGroupssAsync(CancellationToken ct = default) => Task.FromResult(new List<NayaxProductGroup>());
        public Task<List<NayaxLastSalesReport>> GetMachineLastSalesAsync(long machineId, CancellationToken ct = default) => Task.FromResult(new List<NayaxLastSalesReport>());
        public Task<NayaxMachine> GetMachineAsync(long machineId, CancellationToken ct = default) => Task.FromResult(new NayaxMachine { MachineID = machineId });
    }

    [Theory]
    [InlineData(false, "Commission agreements exist")]
    [InlineData(true, "Overlapping commission agreements")]
    public async Task Commission_configuration_failures_make_financial_reports_provisional(bool overlaps, string expectedWarning)
    {
        using var db = CreateDbContext();
        db.NayaxSales.Add(new NayaxSales
        {
            TransactionID = 100, MachineID = 10, MachineName = "Alpha One", SettlementValue = 10m,
            PaymentMethod = "Credit Card", TransactionStatusId = NayaxTransactionStatusIds.Completed,
            CostOfGoodsSold = 2m, CostingStatus = SaleCostingStatus.Costed,
            MachineAuthorizationTime = new DateTime(2025, 7, 15)
        });
        db.NayaxProcessingFeeRates.Add(new NayaxProcessingFeeRate { EffectiveFrom = new DateTime(2025, 1, 1), FeeExGst = .20m });
        db.SiteCommissionAgreements.Add(new SiteCommissionAgreement
        {
            SiteId = 91, EffectiveFrom = new DateTime(2025, 1, 1),
            EffectiveTo = overlaps ? null : new DateTime(2025, 6, 30), CommissionRate = .10m,
            Basis = CommissionBasis.GrossSales
        });
        if (overlaps)
            db.SiteCommissionAgreements.Add(new SiteCommissionAgreement
            {
                SiteId = 91, EffectiveFrom = new DateTime(2025, 7, 1), CommissionRate = .12m,
                Basis = CommissionBasis.GrossSales
            });
        await db.SaveChangesAsync();

        var nayax = new TransactionTestNayaxClient();
        var service = Reporting(db, nayax, new SiteCommissionService(db, nayax));
        var filter = new ReportingFilterDto(new DateTime(2025, 7, 15), new DateTime(2025, 7, 15));

        var bookkeeping = await service.GetBookkeepingAsync(filter);
        var machineProfitability = await service.GetMachineProfitabilityAsync(filter);
        var dashboard = await service.GetDashboardAsync(filter);
        var transactions = await service.GetTransactionsAsync(new TransactionSalesFilterDto(filter.From, filter.To));

        Assert.Contains(bookkeeping.DataQuality.Notes!, x => x.Contains("Commission configuration is incomplete"));
        Assert.Contains(machineProfitability.DataQuality.Notes!, x => x.Contains("Commission configuration is incomplete"));
        Assert.Contains(dashboard.DataQuality.Notes!, x => x.Contains("Commission configuration is incomplete"));
        Assert.Contains(bookkeeping.DataQuality.Notes!, x => x.Contains(expectedWarning));
        var row = Assert.Single(transactions.Rows);
        Assert.Null(row.CommissionAmount);
        Assert.Null(row.DirectProfit);
    }

    [Fact]
    public async Task Site_with_no_agreements_is_a_valid_zero_commission_case()
    {
        using var db = CreateDbContext();
        db.NayaxSales.Add(new NayaxSales
        {
            TransactionID = 101, MachineID = 10, MachineName = "Alpha One", SettlementValue = 10m,
            PaymentMethod = "Credit Card", TransactionStatusId = NayaxTransactionStatusIds.Completed,
            CostOfGoodsSold = 2m, CostingStatus = SaleCostingStatus.Costed,
            MachineAuthorizationTime = new DateTime(2025, 7, 15)
        });
        db.NayaxProcessingFeeRates.Add(new NayaxProcessingFeeRate { EffectiveFrom = new DateTime(2025, 1, 1), FeeExGst = .20m });
        await db.SaveChangesAsync();

        var nayax = new TransactionTestNayaxClient();
        var service = Reporting(db, nayax, new SiteCommissionService(db, nayax));
        var report = await service.GetTransactionsAsync(new TransactionSalesFilterDto(
            new DateTime(2025, 7, 15), new DateTime(2025, 7, 15)));

        var row = Assert.Single(report.Rows);
        Assert.Equal(0m, row.CommissionAmount);
        Assert.Equal(7.78m, row.DirectProfit);
        Assert.DoesNotContain(report.DataQuality.Notes!, x => x.Contains("commission agreement coverage"));
    }

    [Fact]
    public async Task Multiple_valid_commission_rates_do_not_make_profit_provisional()
    {
        using var db = CreateDbContext();
        db.NayaxSales.AddRange(
            new NayaxSales { TransactionID = 102, MachineID = 10, SettlementValue = 10m, PaymentMethod = "Credit Card", TransactionStatusId = NayaxTransactionStatusIds.Completed, CostOfGoodsSold = 2m, CostingStatus = SaleCostingStatus.Costed, MachineAuthorizationTime = new DateTime(2025, 6, 15) },
            new NayaxSales { TransactionID = 103, MachineID = 10, SettlementValue = 10m, PaymentMethod = "Credit Card", TransactionStatusId = NayaxTransactionStatusIds.Completed, CostOfGoodsSold = 2m, CostingStatus = SaleCostingStatus.Costed, MachineAuthorizationTime = new DateTime(2025, 7, 15) });
        db.NayaxProcessingFeeRates.Add(new NayaxProcessingFeeRate { EffectiveFrom = new DateTime(2025, 1, 1), FeeExGst = .20m });
        db.SiteCommissionAgreements.AddRange(
            new SiteCommissionAgreement { SiteId = 91, EffectiveFrom = new DateTime(2025, 1, 1), EffectiveTo = new DateTime(2025, 6, 30), CommissionRate = .10m, Basis = CommissionBasis.GrossSales },
            new SiteCommissionAgreement { SiteId = 91, EffectiveFrom = new DateTime(2025, 7, 1), CommissionRate = .12m, Basis = CommissionBasis.GrossSales });
        await db.SaveChangesAsync();

        var nayax = new TransactionTestNayaxClient();
        var report = await Reporting(db, nayax, new SiteCommissionService(db, nayax)).GetBookkeepingAsync(
            new ReportingFilterDto(new DateTime(2025, 6, 1), new DateTime(2025, 7, 31)));

        Assert.Equal(2.20m, report.SiteCommission);
        Assert.Contains(report.DataQuality.Notes!, x => x.Contains("Multiple commission rates were used"));
        Assert.DoesNotContain(report.DataQuality.Notes!, x => x.Contains("Commission configuration is incomplete"));
    }

    [Fact]
    public void Reporting_service_resolves_with_required_financial_dependencies()
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddScoped<INayaxLynxClient>(_ => new TransactionTestNayaxClient());
        services.AddScoped<INayaxProcessingFeeService, NayaxProcessingFeeService>();
        services.AddScoped<ISiteCommissionService, SiteCommissionService>();
        services.AddScoped<IReportingService, ReportingService>();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.IsType<ReportingService>(scope.ServiceProvider.GetRequiredService<IReportingService>());
    }

    [Fact]
    public async Task Cancelled_commission_lookup_propagates_from_reporting()
    {
        using var db = CreateDbContext();
        var commissions = new Mock<ISiteCommissionService>();
        commissions.Setup(x => x.GetReportAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());
        var service = Reporting(db, siteCommissionService: commissions.Object);

        await Assert.ThrowsAsync<OperationCanceledException>(() => service.GetBookkeepingAsync(
            new ReportingFilterDto(new DateTime(2025, 7, 1), new DateTime(2025, 7, 1))));
    }

    [Fact]
    public async Task Machine_profitability_classifies_payment_methods_with_sqlite()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        db.NayaxSales.AddRange(
            new NayaxSales
            {
                TransactionID = 1, MachineID = 10, MachineName = "Machine A",
                SettlementValue = 10m, PaymentMethod = "Credit Card",
                MachineAuthorizationTime = new DateTime(2025, 8, 1),
                TransactionStatusId = NayaxTransactionStatusIds.Completed, CostOfGoodsSold = 4m
            },
            new NayaxSales
            {
                TransactionID = 2, MachineID = 10, MachineName = "Machine A",
                SettlementValue = 5m, PaymentMethod = "Cash",
                MachineAuthorizationTime = new DateTime(2025, 8, 1),
                TransactionStatusId = NayaxTransactionStatusIds.Completed, CostOfGoodsSold = 2m
            });
        await db.SaveChangesAsync();

        var report = await Reporting(db).GetMachineProfitabilityAsync(
            new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1)));

        var row = Assert.Single(report.Rows);
        Assert.Equal(15m, row.Sales);
        Assert.Equal(10m, row.CardSales);
        Assert.Equal(5m, row.CashSales);
    }

    [Fact]
    public async Task Product_profitability_uses_decimal_cost_and_zero_safe_margin()
    {
        using var db = CreateDbContext();
        db.Products.Add(new Product { Id = 1, Name = "Known", UnitPrice = 99m });
        db.NayaxSales.Add(new NayaxSales
        {
            TransactionID = 1, MachineID = 10, NayaxProductId = 1,
            SettlementValue = 10m, MachineAuthorizationTime = new DateTime(2025, 8, 1)
            , TransactionStatusId = NayaxTransactionStatusIds.Completed, UnitCostAtSale = 3m, CostOfGoodsSold = 6m,
            CostingStatus = SaleCostingStatus.Costed
        });
        db.NayaxSales.Add(new NayaxSales
        {
            TransactionID = 2, MachineID = 10, NayaxProductId = 99,
            SettlementValue = 0m, ProductName = "Unknown",
            TransactionStatusId = NayaxTransactionStatusIds.Completed, MachineAuthorizationTime = new DateTime(2025, 8, 1)
        });
        await db.SaveChangesAsync();

        var report = await Reporting(db).GetProductProfitabilityAsync(
            new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1)));

        var known = Assert.Single(report.Rows, x => !x.IsUnmapped);
        Assert.Equal(6m, known.CostOfGoods);
        Assert.Equal(4m, known.GrossProfit);
        Assert.Equal(40m, known.MarginPercent);
        var unknown = Assert.Single(report.Rows, x => x.IsUnmapped);
        Assert.Null(unknown.MarginPercent);
        Assert.False(unknown.IsCogsComplete);
        Assert.True(report.DataQuality.ContainsUnmappedProducts);
    }

    [Fact]
    public async Task Product_profitability_maps_unmapped_nayax_product_by_name_before_parenthesis()
    {
        using var db = CreateDbContext();
        db.Products.Add(new Product { Id = 29, Name = "Maltese King Share 60g", UnitPrice = 4.80m });
        db.NayaxSales.Add(new NayaxSales
        {
            TransactionID = 29, MachineID = 10, NayaxProductId = 999, ProductName = "Maltese King Share 60g(29, 29 = 4.80)",
            SettlementValue = 4.80m, TransactionStatusId = NayaxTransactionStatusIds.Completed, CostOfGoodsSold = 2m,
            CostingStatus = SaleCostingStatus.Costed, MachineAuthorizationTime = new DateTime(2026, 9, 1)
        });
        await db.SaveChangesAsync();

        var report = await Reporting(db).GetProductProfitabilityAsync(
            new ReportingFilterDto(new DateTime(2026, 9, 1), new DateTime(2026, 9, 1)));

        var row = Assert.Single(report.Rows);
        Assert.Equal("Maltese King Share 60g", row.ProductName);
        Assert.False(row.IsUnmapped);
    }

    [Fact]
    public async Task Product_profitability_merges_sales_that_map_to_the_same_catalogue_product()
    {
        using var db = CreateDbContext();
        db.Products.Add(new Product { Id = 1, Name = "Nu Pure Spring Water 600mL", UnitPrice = 3m });
        db.NayaxSales.AddRange(
            new NayaxSales
            {
                TransactionID = 1, MachineID = 10, NayaxProductId = 1, ProductName = "Nu Pure Spring Water 600mL",
                SettlementValue = 3m, MachineAuthorizationTime = new DateTime(2026, 9, 1),
                TransactionStatusId = NayaxTransactionStatusIds.Completed, CostOfGoodsSold = 1m,
                CostingStatus = SaleCostingStatus.Costed
            },
            new NayaxSales
            {
                TransactionID = 2, MachineID = 10, NayaxProductId = 999, ProductName = "Nu Pure Spring Water 600mL (999)",
                SettlementValue = 3m, MachineAuthorizationTime = new DateTime(2026, 9, 1),
                TransactionStatusId = NayaxTransactionStatusIds.Completed, CostOfGoodsSold = 1m,
                CostingStatus = SaleCostingStatus.Costed
            });
        await db.SaveChangesAsync();

        var report = await Reporting(db).GetProductProfitabilityAsync(
            new ReportingFilterDto(new DateTime(2026, 9, 1), new DateTime(2026, 9, 1)));

        var row = Assert.Single(report.Rows);
        Assert.Equal(1, row.ProductId);
        Assert.Equal("Nu Pure Spring Water 600mL", row.ProductName);
        Assert.Equal(6m, row.Sales);
        Assert.Equal(2m, row.CostOfGoods);
        Assert.Equal(2, row.TransactionCount);
    }

    [Fact]
    public async Task Non_completed_statuses_and_missing_status_are_excluded_from_gross_sales()
    {
        using var db = CreateDbContext();
        db.NayaxSales.AddRange(
            new NayaxSales { TransactionID = 1, MachineID = 10, SettlementValue = 12m, TransactionStatusId = NayaxTransactionStatusIds.Completed, MachineAuthorizationTime = new DateTime(2025, 8, 1) },
            new NayaxSales { TransactionID = 2, MachineID = 10, SettlementValue = 5m, TransactionStatusId = NayaxTransactionStatusIds.PendingSettlementNotFinal, MachineAuthorizationTime = new DateTime(2025, 8, 1) },
            new NayaxSales { TransactionID = 3, MachineID = 10, SettlementValue = 4m, TransactionStatusId = NayaxTransactionStatusIds.Refunded, MachineAuthorizationTime = new DateTime(2025, 8, 1) },
            new NayaxSales { TransactionID = 4, MachineID = 10, SettlementValue = 3m, TransactionStatusId = 21, MachineAuthorizationTime = new DateTime(2025, 8, 1) },
            new NayaxSales { TransactionID = 5, MachineID = 10, SettlementValue = 7m, TransactionStatusId = null, MachineAuthorizationTime = new DateTime(2025, 8, 1) });
        await db.SaveChangesAsync();

        var report = await Reporting(db).GetDailyAsync(
            new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1)));

        var row = Assert.Single(report.Rows);
        Assert.Equal(12m, row.GrossSales);
        Assert.Equal(1, row.PendingTransactionCount);
        Assert.Equal(1, row.RefundedTransactionCount);
        Assert.Equal(2, row.UnknownStatusTransactionCount);
        Assert.Contains(report.DataQuality.Notes!, x => x.Contains("unrecognised status IDs"));
    }

    [Fact]
    public async Task Date_and_machine_filters_are_inclusive_and_machine_specific()
    {
        using var db = CreateDbContext();
        db.NayaxSales.AddRange(
            new NayaxSales { TransactionID = 1, MachineID = 10, SettlementValue = 5m, TransactionStatusId = NayaxTransactionStatusIds.Completed, MachineAuthorizationTime = new DateTime(2025, 7, 1, 23, 59, 59) },
            new NayaxSales { TransactionID = 2, MachineID = 11, SettlementValue = 8m, MachineAuthorizationTime = new DateTime(2025, 7, 1, 12, 0, 0) },
            new NayaxSales { TransactionID = 3, MachineID = 10, SettlementValue = 9m, MachineAuthorizationTime = new DateTime(2025, 7, 2) });
        await db.SaveChangesAsync();

        var report = await Reporting(db).GetDailyAsync(
            new ReportingFilterDto(new DateTime(2025, 7, 1), new DateTime(2025, 7, 1), 10));

        var row = Assert.Single(report.Rows);
        Assert.Equal(5m, row.Sales);
        Assert.Equal(1, row.TransactionCount);
    }

    [Fact]
    public async Task Daily_report_includes_payment_split_cogs_quality_and_period_reimbursement()
    {
        using var db = CreateDbContext();
        db.Products.Add(new Product { Id = 1, Name = "Known", UnitPrice = 99m });
        db.NayaxSales.AddRange(
            new NayaxSales
            {
                TransactionID = 30, MachineID = 10, NayaxProductId = 1, SettlementValue = 10m,
                PaymentMethod = "Credit Card", MachineAuthorizationTime = new DateTime(2025, 8, 1)
                , TransactionStatusId = NayaxTransactionStatusIds.Completed, UnitCostAtSale = 2m, CostOfGoodsSold = 2m,
                CostingStatus = SaleCostingStatus.Costed
            },
            new NayaxSales
            {
                TransactionID = 31, MachineID = 10, NayaxProductId = 99, SettlementValue = 5m,
                PaymentMethod = "Cash", TransactionStatusId = NayaxTransactionStatusIds.Completed, MachineAuthorizationTime = new DateTime(2025, 8, 1)
            });
        db.ImportedReimbursements.Add(new ImportedReimbursement
        {
            ReimbursementStartDate = new DateTime(2025, 8, 1),
            ReimbursementEndDate = new DateTime(2025, 8, 1),
            ReimbursementPayoutDate = new DateTime(2025, 8, 3),
            Total = 10m,
            Fees = { new ImportedFee { FeeTypeDescription = "Processing fee", TotalSum = 1m, TotalSumWithVat = 1.1m, VatPercentage = 10m } }
        });
        await db.SaveChangesAsync();

        var report = await Reporting(db).GetDailyAsync(
            new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1)));

        var row = Assert.Single(report.Rows);
        Assert.Equal(15m, row.GrossSales);
        Assert.Equal(10m, row.CardSales);
        Assert.Equal(5m, row.CashSales);
        Assert.Equal(7.5m, row.AverageSale);
        Assert.False(row.IsCogsComplete);
        Assert.Equal(1, row.UncostedTransactionCount);
        Assert.Equal(5m, row.UncostedSalesAmount);
        Assert.Equal(10m, row.ImportedReimbursement);
        Assert.Equal(1.1m, row.NayaxFeesIncludingGst);
        Assert.Equal("Warning", row.ReconciliationStatus);
        Assert.Equal(15m, report.Totals!.GrossSales);
    }

    [Fact]
    public async Task Daily_totals_do_not_treat_missing_completed_cogs_as_authoritative_zero()
    {
        using var db = CreateDbContext();
        db.NayaxSales.AddRange(
            new NayaxSales { TransactionID = 213, MachineID = 10, SettlementValue = 10m, CostOfGoodsSold = 4m, CostingStatus = SaleCostingStatus.Costed, TransactionStatusId = NayaxTransactionStatusIds.Completed, MachineAuthorizationTime = new DateTime(2025, 8, 1) },
            new NayaxSales { TransactionID = 214, MachineID = 10, SettlementValue = 5m, TransactionStatusId = NayaxTransactionStatusIds.Completed, MachineAuthorizationTime = new DateTime(2025, 8, 1) });
        await db.SaveChangesAsync();

        var report = await Reporting(db).GetDailyAsync(new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1)));

        var row = Assert.Single(report.Rows);
        Assert.Equal(15m, row.GrossSales);
        Assert.Equal(4m, row.PartialCostOfGoods);
        Assert.Null(row.CostOfGoods);
        Assert.False(row.IsCogsComplete);
        Assert.Equal(1, row.UncostedTransactionCount);
        Assert.Equal(5m, row.UncostedSalesAmount);
        Assert.Null(row.GrossProfit);
        Assert.Null(row.GrossMarginPercent);

        var totals = report.Totals!;
        Assert.Equal(15m, totals.GrossSales);
        Assert.Equal(4m, totals.PartialCostOfGoods);
        Assert.Null(totals.CostOfGoods);
        Assert.False(totals.IsCogsComplete);
        Assert.Equal(1, totals.UncostedTransactionCount);
        Assert.Equal(5m, totals.UncostedSalesAmount);
        Assert.Null(totals.GrossProfit);
        Assert.Null(totals.GrossMarginPercent);
    }

    [Theory]
    [InlineData("2025-06-30", "FY2024-25")]
    [InlineData("2025-07-01", "FY2025-26")]
    public void Australian_financial_year_has_correct_boundary(string dateText, string label)
    {
        var date = DateTime.Parse(dateText);
        Assert.Equal(label, AustralianFyHelper.Label(date));
        Assert.Equal(label, AustralianFinancialYear.Label(date));
    }

    [Fact]
    public async Task Bookkeeping_and_gst_use_centralised_decimal_formulas()
    {
        using var db = CreateDbContext();
        db.NayaxSales.Add(new NayaxSales
        {
            TransactionID = 8, MachineID = 10, NayaxProductId = null,
            SettlementValue = 110m, TransactionStatusId = NayaxTransactionStatusIds.Completed, MachineAuthorizationTime = new DateTime(2025, 8, 1)
        });
        db.ImportedReimbursements.Add(new ImportedReimbursement
        {
            ReimbursementStartDate = new DateTime(2025, 8, 1),
            ReimbursementEndDate = new DateTime(2025, 8, 31),
            Total = 110m,
            Fees = { new ImportedFee { FeeTypeDescription = "Processing fee", TotalSum = 10m, TotalSumWithVat = 11m, VatPercentage = 10m } }
        });
        await db.SaveChangesAsync();

        var service = Reporting(db);
        var bookkeeping = await service.GetBookkeepingAsync(new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 31)));
        var gst = await service.GetGstAsync(new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 31)));

        Assert.Equal(110m, bookkeeping.Sales);
        Assert.Equal(10m, bookkeeping.GstOnSales);
        Assert.Equal(1m, bookkeeping.GstOnFees, 2);
        Assert.Equal(100m, gst.TaxableSales);
        Assert.Equal(9m, gst.NetGst, 2);
        Assert.Equal(0m, ReportingCalculations.MarginPercent(0m, 4m));
    }

    [Fact]
    public async Task Bookkeeping_net_profit_deducts_receipt_operating_costs()
    {
        using var db = CreateDbContext();
        db.NayaxSales.Add(new NayaxSales
        {
            TransactionID = 20, MachineID = 10, SettlementValue = 100m,
            CostOfGoodsSold = 0m, CostingStatus = SaleCostingStatus.Costed,
            TransactionStatusId = NayaxTransactionStatusIds.Completed, MachineAuthorizationTime = new DateTime(2025, 8, 1)
        });
        db.Receipts.Add(new Receipt
        {
            Title = "Supplier receipt",
            PurchaseDate = new DateTime(2025, 8, 15),
            DeliveryCost = 2m,
            PackageCost = 3m
        });
        await db.SaveChangesAsync();

        var nayax = new TransactionTestNayaxClient();
        var report = await Reporting(db, nayax, new SiteCommissionService(db, nayax)).GetBookkeepingAsync(
            new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 31)));

        Assert.Equal(0m, report.OtherOperatingExpenses);
        Assert.Equal(95m, report.NetProfit);
    }

    [Fact]
    public async Task Bookkeeping_marks_profit_unavailable_when_completed_sale_has_no_persisted_cogs()
    {
        using var db = CreateDbContext();
        db.NayaxSales.AddRange(
            new NayaxSales { TransactionID = 201, MachineID = 10, SettlementValue = 10m, CostOfGoodsSold = 4m, CostingStatus = SaleCostingStatus.Costed, TransactionStatusId = NayaxTransactionStatusIds.Completed, MachineAuthorizationTime = new DateTime(2025, 8, 1) },
            new NayaxSales { TransactionID = 202, MachineID = 10, SettlementValue = 5m, CostOfGoodsSold = null, TransactionStatusId = NayaxTransactionStatusIds.Completed, MachineAuthorizationTime = new DateTime(2025, 8, 1) });
        await db.SaveChangesAsync();

        var report = await Reporting(db).GetBookkeepingAsync(new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1)));

        Assert.Equal(15m, report.Sales);
        Assert.Equal(4m, report.PartialCostOfGoods);
        Assert.False(report.IsCogsComplete);
        Assert.Equal(1, report.UncostedTransactionCount);
        Assert.Equal(5m, report.UncostedSalesAmount);
        Assert.Null(report.CostOfGoods);
        Assert.Null(report.GrossProfit);
        Assert.Null(report.NetProfit);
        Assert.Null(report.NetMarginPercent);
        Assert.Contains(report.DataQuality.Notes!, x => x.Contains("no persisted COGS"));
    }

    [Fact]
    public async Task Machine_profitability_is_unavailable_only_for_machine_with_incomplete_cogs()
    {
        using var db = CreateDbContext();
        db.NayaxSales.AddRange(
            new NayaxSales { TransactionID = 203, MachineID = 10, MachineName = "A", SettlementValue = 10m, CostOfGoodsSold = 4m, TransactionStatusId = NayaxTransactionStatusIds.Completed, MachineAuthorizationTime = new DateTime(2025, 8, 1) },
            new NayaxSales { TransactionID = 204, MachineID = 11, MachineName = "B", SettlementValue = 10m, TransactionStatusId = NayaxTransactionStatusIds.Completed, MachineAuthorizationTime = new DateTime(2025, 8, 1) });
        await db.SaveChangesAsync();

        var report = await Reporting(db).GetMachineProfitabilityAsync(new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1)));

        Assert.Equal(6m, Assert.Single(report.Rows, x => x.MachineId == 10).GrossProfit);
        var incomplete = Assert.Single(report.Rows, x => x.MachineId == 11);
        Assert.False(incomplete.IsCogsComplete);
        Assert.Null(incomplete.GrossProfit);
        Assert.Null(incomplete.DirectProfit);
        Assert.Null(incomplete.DirectMarginPercent);
        Assert.Contains(report.DataQuality.Notes!, x => x.Contains("machines have completed sales with no persisted COGS"));
    }

    [Fact]
    public async Task Machine_profitability_keeps_valid_machine_direct_profit_when_another_machine_has_overlapping_commissions()
    {
        using var db = CreateDbContext();
        var date = new DateTime(2025, 8, 1);
        db.NayaxSales.AddRange(
            new NayaxSales { TransactionID = 301, MachineID = 10, MachineName = "Valid", SettlementValue = 100m, PaymentMethod = "Credit Card", CostOfGoodsSold = 40m, CostingStatus = SaleCostingStatus.Costed, TransactionStatusId = NayaxTransactionStatusIds.Completed, MachineAuthorizationTime = date },
            new NayaxSales { TransactionID = 302, MachineID = 11, MachineName = "Invalid", SettlementValue = 100m, PaymentMethod = "Credit Card", CostOfGoodsSold = 40m, CostingStatus = SaleCostingStatus.Costed, TransactionStatusId = NayaxTransactionStatusIds.Completed, MachineAuthorizationTime = date });
        db.NayaxProcessingFeeRates.Add(new NayaxProcessingFeeRate { EffectiveFrom = new DateTime(2025, 1, 1), FeeExGst = .20m });
        db.SiteCommissionAgreements.AddRange(
            new SiteCommissionAgreement { SiteId = 91, EffectiveFrom = new DateTime(2025, 1, 1), CommissionRate = .10m, Basis = CommissionBasis.GrossSales },
            new SiteCommissionAgreement { SiteId = 92, EffectiveFrom = new DateTime(2025, 1, 1), CommissionRate = .10m, Basis = CommissionBasis.GrossSales },
            new SiteCommissionAgreement { SiteId = 92, EffectiveFrom = new DateTime(2025, 7, 1), CommissionRate = .12m, Basis = CommissionBasis.GrossSales });
        await db.SaveChangesAsync();

        var nayax = new TransactionTestNayaxClient(
            new NayaxMachine { MachineID = 10, MachineName = "Valid", CustomerID = 91 },
            new NayaxMachine { MachineID = 11, MachineName = "Invalid", CustomerID = 92 });
        var report = await Reporting(db, nayax, new SiteCommissionService(db, nayax)).GetMachineProfitabilityAsync(
            new ReportingFilterDto(date, date));

        var valid = Assert.Single(report.Rows, x => x.MachineId == 10);
        Assert.Equal(49.78m, valid.DirectProfit);
        Assert.Equal(49.78m, valid.DirectMarginPercent);
        var invalid = Assert.Single(report.Rows, x => x.MachineId == 11);
        Assert.Null(invalid.DirectProfit);
        Assert.Null(invalid.DirectMarginPercent);
        Assert.Contains(report.DataQuality.Notes!, x => x.Contains("Commission configuration is incomplete"));
    }

    [Fact]
    public async Task Machine_profitability_keeps_valid_zero_commission_machine_when_another_machine_has_no_site_mapping()
    {
        using var db = CreateDbContext();
        var date = new DateTime(2025, 8, 1);
        db.NayaxSales.AddRange(
            new NayaxSales { TransactionID = 303, MachineID = 10, MachineName = "Mapped", SettlementValue = 100m, PaymentMethod = "Credit Card", CostOfGoodsSold = 40m, CostingStatus = SaleCostingStatus.Costed, TransactionStatusId = NayaxTransactionStatusIds.Completed, MachineAuthorizationTime = date },
            new NayaxSales { TransactionID = 304, MachineID = 11, MachineName = "Unmapped", SettlementValue = 100m, PaymentMethod = "Credit Card", CostOfGoodsSold = 40m, CostingStatus = SaleCostingStatus.Costed, TransactionStatusId = NayaxTransactionStatusIds.Completed, MachineAuthorizationTime = date });
        db.NayaxProcessingFeeRates.Add(new NayaxProcessingFeeRate { EffectiveFrom = new DateTime(2025, 1, 1), FeeExGst = .20m });
        await db.SaveChangesAsync();

        var nayax = new TransactionTestNayaxClient(new NayaxMachine { MachineID = 10, MachineName = "Mapped", CustomerID = 91 });
        var report = await Reporting(db, nayax, new SiteCommissionService(db, nayax)).GetMachineProfitabilityAsync(
            new ReportingFilterDto(date, date));

        var mapped = Assert.Single(report.Rows, x => x.MachineId == 10);
        Assert.Equal(0m, mapped.SiteCommission);
        Assert.Equal(59.78m, mapped.DirectProfit);
        Assert.Equal(59.78m, mapped.DirectMarginPercent);
        var unmapped = Assert.Single(report.Rows, x => x.MachineId == 11);
        Assert.Null(unmapped.DirectProfit);
        Assert.Null(unmapped.DirectMarginPercent);
        Assert.Contains(report.DataQuality.Notes!, x => x.Contains("site mapping is unavailable"));
    }

    [Fact]
    public async Task Machine_filtered_reports_scope_commission_completeness_to_the_selected_machine()
    {
        using var db = CreateDbContext();
        var validDate = new DateTime(2025, 6, 15);
        var invalidDate = new DateTime(2025, 8, 1);
        db.NayaxSales.AddRange(
            new NayaxSales { TransactionID = 305, MachineID = 10, MachineName = "Valid", SettlementValue = 100m, PaymentMethod = "Credit Card", CostOfGoodsSold = 40m, CostingStatus = SaleCostingStatus.Costed, TransactionStatusId = NayaxTransactionStatusIds.Completed, MachineAuthorizationTime = validDate },
            new NayaxSales { TransactionID = 306, MachineID = 11, MachineName = "Invalid", SettlementValue = 100m, PaymentMethod = "Credit Card", CostOfGoodsSold = 40m, CostingStatus = SaleCostingStatus.Costed, TransactionStatusId = NayaxTransactionStatusIds.Completed, MachineAuthorizationTime = invalidDate });
        db.NayaxProcessingFeeRates.Add(new NayaxProcessingFeeRate { EffectiveFrom = new DateTime(2025, 1, 1), FeeExGst = .20m });
        db.SiteCommissionAgreements.Add(new SiteCommissionAgreement
        {
            SiteId = 91, EffectiveFrom = new DateTime(2025, 1, 1), EffectiveTo = new DateTime(2025, 6, 30),
            CommissionRate = .10m, Basis = CommissionBasis.GrossSales
        });
        await db.SaveChangesAsync();

        var nayax = new TransactionTestNayaxClient(
            new NayaxMachine { MachineID = 10, MachineName = "Valid", CustomerID = 91 },
            new NayaxMachine { MachineID = 11, MachineName = "Invalid", CustomerID = 91 });
        var service = Reporting(db, nayax, new SiteCommissionService(db, nayax));
        var from = new DateTime(2025, 6, 1);

        var validFilter = new ReportingFilterDto(from, invalidDate, MachineId: 10);
        var validDashboard = await service.GetDashboardAsync(validFilter);
        var validBookkeeping = await service.GetBookkeepingAsync(validFilter);
        Assert.Equal(49.78m, validDashboard.DirectProfit);
        Assert.Equal(49.78m, validDashboard.DirectMarginPercent);
        Assert.Null(validDashboard.NetProfit);
        Assert.Equal(49.78m, validBookkeeping.DirectProfit);
        Assert.Equal(49.78m, validBookkeeping.DirectMarginPercent);
        Assert.Null(validBookkeeping.NetProfit);
        Assert.DoesNotContain(validDashboard.DataQuality.Notes!, x => x.Contains("Commission configuration is incomplete"));
        Assert.DoesNotContain(validBookkeeping.DataQuality.Notes!, x => x.Contains("Commission configuration is incomplete"));

        var invalidFilter = new ReportingFilterDto(from, invalidDate, MachineId: 11);
        var invalidDashboard = await service.GetDashboardAsync(invalidFilter);
        var invalidBookkeeping = await service.GetBookkeepingAsync(invalidFilter);
        Assert.Null(invalidDashboard.DirectProfit);
        Assert.Null(invalidDashboard.DirectMarginPercent);
        Assert.Null(invalidBookkeeping.DirectProfit);
        Assert.Null(invalidBookkeeping.DirectMarginPercent);
        Assert.Contains(invalidDashboard.DataQuality.Notes!, x => x.Contains("Commission configuration is incomplete"));
        Assert.Contains(invalidBookkeeping.DataQuality.Notes!, x => x.Contains("Commission configuration is incomplete"));
    }

    [Fact]
    public async Task Machine_direct_profit_excludes_unallocated_overhead_and_filtered_reports_do_not_return_net_profit()
    {
        using var db = CreateDbContext();
        var date = new DateTime(2025, 8, 1);
        db.NayaxSales.Add(new NayaxSales
        {
            TransactionID = 300, MachineID = 10, MachineName = "Alpha One", SettlementValue = 100m,
            PaymentMethod = "Credit Card", TransactionStatusId = NayaxTransactionStatusIds.Completed,
            CostOfGoodsSold = 40m, CostingStatus = SaleCostingStatus.Costed, MachineAuthorizationTime = date
        });
        db.NayaxProcessingFeeRates.Add(new NayaxProcessingFeeRate { EffectiveFrom = new DateTime(2025, 1, 1), FeeExGst = 5m / 1.1m });
        db.SiteCommissionAgreements.Add(new SiteCommissionAgreement
        {
            SiteId = 91, EffectiveFrom = new DateTime(2025, 1, 1), CommissionRate = .10m,
            Basis = CommissionBasis.GrossSales
        });
        db.OperatingExpenses.AddRange(
            new OperatingExpense { ExpenseDate = date, MachineId = 10, TotalAmount = 5m },
            new OperatingExpense { ExpenseDate = date, TotalAmount = 20m });
        db.Receipts.Add(new Receipt { Title = "Delivery", PurchaseDate = date, DeliveryCost = 5m });
        await db.SaveChangesAsync();

        var nayax = new TransactionTestNayaxClient();
        var service = Reporting(db, nayax, new SiteCommissionService(db, nayax));
        var filter = new ReportingFilterDto(date, date, MachineId: 10);

        var machine = Assert.Single((await service.GetMachineProfitabilityAsync(filter)).Rows);
        Assert.Equal(60m, machine.GrossProfit);
        Assert.Equal(40m, machine.DirectProfit);
        Assert.Equal(40m, machine.DirectMarginPercent);

        var dashboard = await service.GetDashboardAsync(filter);
        Assert.Equal(40m, dashboard.DirectProfit);
        Assert.Equal(40m, dashboard.DirectMarginPercent);
        Assert.Null(dashboard.NetProfit);
        Assert.Contains(dashboard.DataQuality.Notes!, x => x.Contains("shared business overhead"));

        var bookkeeping = await service.GetBookkeepingAsync(filter);
        Assert.Equal(40m, bookkeeping.DirectProfit);
        Assert.Equal(40m, bookkeeping.DirectMarginPercent);
        Assert.Null(bookkeeping.NetProfit);
        Assert.Contains(bookkeeping.DataQuality.Notes!, x => x.Contains("shared business overhead"));
    }

    [Fact]
    public async Task Product_profitability_classifies_revenue_with_centralized_payment_classifier()
    {
        // SQLite-backed so the EF query really executes in SQL (translation safety);
        // expected values are derived from PaymentMethodClassifier rather than a re-stated mapping.
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        db.Products.Add(new Product { Id = 1, Name = "Water", UnitPrice = 3m });
        db.NayaxSales.AddRange(
            new NayaxSales { TransactionID = 1, MachineID = 10, NayaxProductId = 1, ProductName = "Water", PaymentMethod = "Credit Card", SettlementValue = 10m, CostOfGoodsSold = 1m, CostingStatus = SaleCostingStatus.Costed, TransactionStatusId = NayaxTransactionStatusIds.Completed, MachineAuthorizationTime = new DateTime(2025, 8, 1) },
            new NayaxSales { TransactionID = 2, MachineID = 10, NayaxProductId = 1, ProductName = "Water", PaymentMethod = "Prepaid Credit", SettlementValue = 20m, CostOfGoodsSold = 1m, CostingStatus = SaleCostingStatus.Costed, TransactionStatusId = NayaxTransactionStatusIds.Completed, MachineAuthorizationTime = new DateTime(2025, 8, 1) },
            new NayaxSales { TransactionID = 3, MachineID = 10, NayaxProductId = 1, ProductName = "Water", PaymentMethod = "Cash", SettlementValue = 15m, CostOfGoodsSold = 1m, CostingStatus = SaleCostingStatus.Costed, TransactionStatusId = NayaxTransactionStatusIds.Completed, MachineAuthorizationTime = new DateTime(2025, 8, 1) },
            new NayaxSales { TransactionID = 4, MachineID = 10, NayaxProductId = 1, ProductName = "Water", PaymentMethod = "Staff Voucher", SettlementValue = 7m, CostOfGoodsSold = 1m, CostingStatus = SaleCostingStatus.Costed, TransactionStatusId = NayaxTransactionStatusIds.Completed, MachineAuthorizationTime = new DateTime(2025, 8, 1) });
        await db.SaveChangesAsync();

        var sales = db.NayaxSales.ToList();
        var expectedCard = sales.Where(x => PaymentMethodClassifier.Classify(x.PaymentMethod) == NayaxPaymentType.Card).Sum(x => x.SettlementValue);
        var expectedCash = sales.Where(x => PaymentMethodClassifier.Classify(x.PaymentMethod) == NayaxPaymentType.Cash).Sum(x => x.SettlementValue);
        Assert.Equal(30m, expectedCard);
        Assert.Equal(15m, expectedCash);
        Assert.Equal(NayaxPaymentType.Unknown, PaymentMethodClassifier.Classify("Staff Voucher"));

        var report = await Reporting(db).GetProductProfitabilityAsync(
            new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1)));

        var row = Assert.Single(report.Rows);
        Assert.Equal(expectedCard, row.CardRevenue);
        Assert.Equal(expectedCash, row.CashRevenue);
        Assert.Equal(52m, row.Sales); // gross sales unchanged: includes the unknown payment method
    }

    [Fact]
    public async Task Product_profitability_does_not_treat_missing_cogs_as_zero()
    {
        using var db = CreateDbContext();
        db.Products.AddRange(new Product { Id = 1, Name = "A" }, new Product { Id = 2, Name = "B" });
        db.NayaxSales.AddRange(
            new NayaxSales { TransactionID = 205, MachineID = 10, NayaxProductId = 1, SettlementValue = 10m, CostOfGoodsSold = 4m, TransactionStatusId = NayaxTransactionStatusIds.Completed, MachineAuthorizationTime = new DateTime(2025, 8, 1) },
            new NayaxSales { TransactionID = 206, MachineID = 10, NayaxProductId = 1, SettlementValue = 10m, CostOfGoodsSold = 4m, TransactionStatusId = NayaxTransactionStatusIds.Completed, MachineAuthorizationTime = new DateTime(2025, 8, 1) },
            new NayaxSales { TransactionID = 207, MachineID = 10, NayaxProductId = 2, SettlementValue = 10m, CostOfGoodsSold = 4m, TransactionStatusId = NayaxTransactionStatusIds.Completed, MachineAuthorizationTime = new DateTime(2025, 8, 1) },
            new NayaxSales { TransactionID = 208, MachineID = 10, NayaxProductId = 2, SettlementValue = 10m, TransactionStatusId = NayaxTransactionStatusIds.Completed, MachineAuthorizationTime = new DateTime(2025, 8, 1) });
        await db.SaveChangesAsync();

        var report = await Reporting(db).GetProductProfitabilityAsync(new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1)));

        Assert.Equal(12m, Assert.Single(report.Rows, x => x.ProductId == 1).GrossProfit);
        var incomplete = Assert.Single(report.Rows, x => x.ProductId == 2);
        Assert.Equal(4m, incomplete.PartialCostOfGoods);
        Assert.False(incomplete.IsCogsComplete);
        Assert.Null(incomplete.GrossProfit);
        Assert.Null(incomplete.MarginPercent);
    }

    [Fact]
    public async Task Dashboard_profit_is_unavailable_when_completed_cogs_is_incomplete()
    {
        using var db = CreateDbContext();
        db.NayaxSales.AddRange(
            new NayaxSales { TransactionID = 209, MachineID = 10, SettlementValue = 10m, CostOfGoodsSold = 4m, TransactionStatusId = NayaxTransactionStatusIds.Completed, MachineAuthorizationTime = new DateTime(2025, 8, 1) },
            new NayaxSales { TransactionID = 210, MachineID = 10, SettlementValue = 5m, TransactionStatusId = NayaxTransactionStatusIds.Completed, MachineAuthorizationTime = new DateTime(2025, 8, 1) });
        await db.SaveChangesAsync();

        var report = await Reporting(db).GetDashboardAsync(new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1)));

        Assert.Equal(15m, report.Sales);
        Assert.False(report.IsCogsComplete);
        Assert.Null(report.CostOfGoodsSold);
        Assert.Equal(4m, report.PartialCostOfGoods);
        Assert.Null(report.GrossProfit);
        Assert.Null(report.GrossMarginPercent);
        Assert.Null(report.NetProfit);
        Assert.Null(report.NetMarginPercent);
        Assert.Contains(report.DataQuality.Notes!, x => x.Contains("profitability is unavailable"));
    }

    [Fact]
    public async Task Dashboard_gross_margin_uses_cogs_not_gross_profit_as_its_cost_basis()
    {
        using var db = CreateDbContext();
        db.NayaxSales.Add(new NayaxSales
        {
            TransactionID = 215, MachineID = 10, SettlementValue = 100m, CostOfGoodsSold = 40m,
            CostingStatus = SaleCostingStatus.Costed, TransactionStatusId = NayaxTransactionStatusIds.Completed,
            MachineAuthorizationTime = new DateTime(2025, 8, 1)
        });
        await db.SaveChangesAsync();

        var report = await Reporting(db).GetDashboardAsync(new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1)));

        Assert.True(report.IsCogsComplete);
        Assert.Equal(40m, report.CostOfGoodsSold);
        Assert.Equal(60m, report.GrossProfit);
        Assert.Equal(60m, report.GrossMarginPercent);
    }

    [Fact]
    public async Task Zero_persisted_cost_is_complete_and_non_completed_missing_cost_is_ignored()
    {
        using var db = CreateDbContext();
        db.NayaxSales.AddRange(
            new NayaxSales { TransactionID = 211, MachineID = 10, SettlementValue = 10m, CostOfGoodsSold = 0m, TransactionStatusId = NayaxTransactionStatusIds.Completed, MachineAuthorizationTime = new DateTime(2025, 8, 1) },
            new NayaxSales { TransactionID = 212, MachineID = 10, SettlementValue = 10m, TransactionStatusId = NayaxTransactionStatusIds.PendingSettlementNotFinal, MachineAuthorizationTime = new DateTime(2025, 8, 1) });
        await db.SaveChangesAsync();

        var report = await Reporting(db).GetBookkeepingAsync(new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1)));

        Assert.True(report.IsCogsComplete);
        Assert.Equal(0m, report.CostOfGoods);
        Assert.Equal(10m, report.GrossProfit);
    }

    [Fact]
    public async Task Reconciliation_matches_period_and_applies_tolerance()
    {
        using var db = CreateDbContext();
        db.NayaxSales.Add(new NayaxSales
        {
            TransactionID = 1, MachineID = 10, SettlementValue = 100m,
            PaymentMethod = "Credit Card",
            TransactionStatusId = NayaxTransactionStatusIds.Completed, MachineAuthorizationTime = new DateTime(2025, 8, 10)
        });
        var file = new ImportedFile { FileName = "aug.xml", FileHash = "aug", ImportedAt = DateTime.UtcNow };
        file.Reimbursements.Add(new ImportedReimbursement
        {
            ReimbursementStartDate = new DateTime(2025, 8, 1),
            ReimbursementEndDate = new DateTime(2025, 8, 31),
            Total = 100.005m
        });
        db.ImportedFiles.Add(file);
        await db.SaveChangesAsync();

        var report = await Reporting(db).GetReconciliationAsync(
            new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 31)), 0.01m);

        Assert.Equal(-0.005m, report.Difference);
        Assert.True(report.IsMatch);
    }

    [Fact]
    public async Task Reconciliation_flags_period_mismatch()
    {
        using var db = CreateDbContext();
        db.NayaxSales.Add(new NayaxSales
        {
            TransactionID = 2, MachineID = 10, SettlementValue = 50m,
            TransactionStatusId = NayaxTransactionStatusIds.Completed, MachineAuthorizationTime = new DateTime(2025, 8, 10)
        });
        db.ImportedReimbursements.Add(new ImportedReimbursement
        {
            ReimbursementStartDate = new DateTime(2025, 7, 1),
            ReimbursementEndDate = new DateTime(2025, 7, 31),
            Total = 50m
        });
        await db.SaveChangesAsync();

        var report = await Reporting(db).GetReconciliationAsync(
            new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 31)));

        Assert.Equal(0m, report.ImportedReimbursement);
        Assert.False(report.IsMatch);
        Assert.Contains(report.DataQuality.Notes!, x => x.Contains("No imported reimbursement"));
    }

    [Fact]
    public async Task Reconciliation_matches_reimbursement_device_to_selected_machine()
    {
        using var db = CreateDbContext();
        db.NayaxSales.AddRange(
            new NayaxSales
            {
                TransactionID = 10, MachineID = 1216029552, SettlementValue = 90.30m,
                PaymentMethod = "Credit Card",
                TransactionStatusId = NayaxTransactionStatusIds.Completed, MachineAuthorizationTime = new DateTime(2025, 8, 12)
            },
            new NayaxSales
            {
                TransactionID = 11, MachineID = 1216029562, SettlementValue = 42.90m,
                PaymentMethod = "Credit Card",
                TransactionStatusId = NayaxTransactionStatusIds.Completed, MachineAuthorizationTime = new DateTime(2025, 8, 12)
            });

        var file = new ImportedFile { FileName = "machine.xml", FileHash = "machine", ImportedAt = DateTime.UtcNow };
        file.Reimbursements.Add(new ImportedReimbursement
        {
            ReimbursementStartDate = new DateTime(2025, 8, 12),
            ReimbursementEndDate = new DateTime(2025, 8, 12),
            Total = 133.20m,
            Devices =
            {
                new ImportedReimbursementDevice
                {
                    MachineNumber = "1216029552",
                    TotalBillableTransactionAmount = 90.30m,
                    NetAmount = 86.22m
                },
                new ImportedReimbursementDevice
                {
                    MachineNumber = "1216029562",
                    TotalBillableTransactionAmount = 42.90m,
                    NetAmount = 41.03m
                }
            }
        });
        db.ImportedFiles.Add(file);
        await db.SaveChangesAsync();

        var report = await Reporting(db).GetReconciliationAsync(
            new ReportingFilterDto(new DateTime(2025, 8, 12), new DateTime(2025, 8, 12), 1216029552));

        Assert.Equal(90.30m, report.NayaxSales);
        Assert.Equal(90.30m, report.ImportedReimbursement);
        Assert.True(report.IsMatch);
    }

    [Fact]
    public async Task Reconciliation_exposes_sales_fee_and_settlement_breakdown()
    {
        using var db = CreateDbContext();
        db.NayaxSales.AddRange(
            new NayaxSales
            {
                TransactionID = 40, MachineID = 10, SettlementValue = 100m,
                PaymentMethod = "Credit Card", TransactionStatusId = NayaxTransactionStatusIds.Completed, MachineAuthorizationTime = new DateTime(2025, 8, 12)
            },
            new NayaxSales
            {
                TransactionID = 41, MachineID = 10, SettlementValue = 25m,
                PaymentMethod = "Cash", TransactionStatusId = NayaxTransactionStatusIds.Completed, MachineAuthorizationTime = new DateTime(2025, 8, 12)
            });
        db.ImportedReimbursements.Add(new ImportedReimbursement
        {
            ReimbursementStartDate = new DateTime(2025, 8, 12),
            ReimbursementEndDate = new DateTime(2025, 8, 12),
            ReimbursementPayoutDate = new DateTime(2025, 8, 14),
            Total = 88m,
            Devices =
            {
                new ImportedReimbursementDevice
                {
                    EntityId = "device-1", MachineNumber = "10", TotalBillableTransactionAmount = 100m,
                    TotalBillableTransactionCount = 1
                }
            },
            DevicePayments =
            {
                new ImportedDevicePayment
                {
                    EntityId = "device-1", PaymentMethodDescription = "Credit Card",
                    SalesCount = 1, TotalSum = 100m
                }
            },
            Fees =
            {
                new ImportedFee { FeeTypeDescription = "Processing fee", TotalSum = 2m, TotalSumWithVat = 2.2m, VatPercentage = 10m },
                new ImportedFee { FeeTypeDescription = "Service fee", TotalSum = 3m, TotalSumWithVat = 3.3m, VatPercentage = 10m }
            }
        });
        await db.SaveChangesAsync();

        var report = await Reporting(db).GetReconciliationAsync(
            new ReportingFilterDto(new DateTime(2025, 8, 12), new DateTime(2025, 8, 12)));

        Assert.Equal(125m, report.TotalVendingSales);
        Assert.Equal(100m, report.CardTransactionSales);
        Assert.Equal(25m, report.CashSales);
        Assert.Equal(100m, report.NayaxReportedGrossCardSales);
        Assert.Equal(2m, report.ProcessingFeesExGst);
        Assert.Equal(0.5m, report.FeeGst);
        Assert.Equal(3m, report.OtherFees);
        Assert.Equal(94.5m, report.ExpectedNetReimbursement);
        Assert.Equal(88m, report.ActualNetReimbursement);
        Assert.Equal("Reconciled", report.GrossStatus);
        Assert.Equal("Mismatch", report.SettlementStatus);
        Assert.Single(report.PeriodRows);
    }
}
