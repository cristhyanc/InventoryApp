using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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
        public Task<List<NayaxDevice>> GetDevicesAsync(CancellationToken ct = default) => Task.FromResult(new List<NayaxDevice>());
        public Task<List<NayaxMachine>> GetMachinesAsync(CancellationToken ct = default) => Task.FromResult(new List<NayaxMachine> { new() { MachineID = 10, MachineName = "Alpha One", CustomerID = 91 } });
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
        Assert.Null(Assert.Single(transactions.Rows).DirectProfit);
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
            MachineAuthorizationTime = new DateTime(2025, 8, 1)
        });
        await db.SaveChangesAsync();

        var report = await Reporting(db).GetProductProfitabilityAsync(
            new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1)));

        var known = Assert.Single(report.Rows, x => !x.IsUnmapped);
        Assert.Equal(6m, known.CostOfGoods);
        Assert.Equal(4m, known.GrossProfit);
        Assert.Equal(40m, known.MarginPercent);
        var unknown = Assert.Single(report.Rows, x => x.IsUnmapped);
        Assert.Equal(0m, unknown.MarginPercent);
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
            new NayaxSales { TransactionID = 1, MachineID = 10, SettlementValue = 5m, MachineAuthorizationTime = new DateTime(2025, 7, 1, 23, 59, 59) },
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
                PaymentMethod = "Cash", MachineAuthorizationTime = new DateTime(2025, 8, 1)
            });
        db.ImportedReimbursements.Add(new ImportedReimbursement
        {
            ReimbursementStartDate = new DateTime(2025, 8, 1),
            ReimbursementEndDate = new DateTime(2025, 8, 1),
            ReimbursementPayoutDate = new DateTime(2025, 8, 3),
            Total = 10m,
            Fees = { new ImportedFee { TotalSum = 1m, TotalSumWithVat = 1.1m, VatPercentage = 10m } }
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
            SettlementValue = 110m, MachineAuthorizationTime = new DateTime(2025, 8, 1)
        });
        db.ImportedReimbursements.Add(new ImportedReimbursement
        {
            ReimbursementStartDate = new DateTime(2025, 8, 1),
            ReimbursementEndDate = new DateTime(2025, 8, 31),
            Total = 110m,
            Fees = { new ImportedFee { TotalSumWithVat = 11m, VatPercentage = 10m } }
        });
        await db.SaveChangesAsync();

        var service = Reporting(db);
        var bookkeeping = await service.GetBookkeepingAsync(new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 31)));
        var gst = await service.GetGstAsync(new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 31)));

        Assert.Equal(110m, bookkeeping.Sales);
        Assert.Equal(10m, bookkeeping.GstOnSales);
        Assert.Equal(1m, bookkeeping.GstOnFees);
        Assert.Equal(100m, gst.TaxableSales);
        Assert.Equal(9m, gst.NetGst);
        Assert.Equal(0m, ReportingCalculations.MarginPercent(0m, 4m));
    }

    [Fact]
    public async Task Bookkeeping_net_profit_deducts_receipt_operating_costs()
    {
        using var db = CreateDbContext();
        db.NayaxSales.Add(new NayaxSales
        {
            TransactionID = 20, MachineID = 10, SettlementValue = 100m,
            MachineAuthorizationTime = new DateTime(2025, 8, 1)
        });
        db.Receipts.Add(new Receipt
        {
            Title = "Supplier receipt",
            PurchaseDate = new DateTime(2025, 8, 15),
            DeliveryCost = 2m,
            PackageCost = 3m
        });
        await db.SaveChangesAsync();

        var report = await Reporting(db).GetBookkeepingAsync(
            new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 31)));

        Assert.Equal(5m, report.OtherOperatingExpenses);
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
        Assert.Null(incomplete.NetProfit);
        Assert.Contains(report.DataQuality.Notes!, x => x.Contains("machines have completed sales with no persisted COGS"));
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
        Assert.Null(report.GrossProfit);
        Assert.Null(report.NetProfit);
        Assert.Null(report.NetMarginPercent);
        Assert.Contains(report.DataQuality.Notes!, x => x.Contains("profitability is unavailable"));
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
            MachineAuthorizationTime = new DateTime(2025, 8, 10)
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
            MachineAuthorizationTime = new DateTime(2025, 8, 10)
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
                MachineAuthorizationTime = new DateTime(2025, 8, 12)
            },
            new NayaxSales
            {
                TransactionID = 11, MachineID = 1216029562, SettlementValue = 42.90m,
                PaymentMethod = "Credit Card",
                MachineAuthorizationTime = new DateTime(2025, 8, 12)
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
                PaymentMethod = "Credit Card", MachineAuthorizationTime = new DateTime(2025, 8, 12)
            },
            new NayaxSales
            {
                TransactionID = 41, MachineID = 10, SettlementValue = 25m,
                PaymentMethod = "Cash", MachineAuthorizationTime = new DateTime(2025, 8, 12)
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
