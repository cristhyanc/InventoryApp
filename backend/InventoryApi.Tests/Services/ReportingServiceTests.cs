using System;
using System.Threading.Tasks;
using InventoryApi.Data;
using InventoryApi.DTOs;
using InventoryApi.Models;
using InventoryApi.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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

        var report = await new ReportingService(db).GetMachineProfitabilityAsync(
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

        var report = await new ReportingService(db).GetProductProfitabilityAsync(
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

        var report = await new ReportingService(db).GetProductProfitabilityAsync(
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

        var report = await new ReportingService(db).GetProductProfitabilityAsync(
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

        var report = await new ReportingService(db).GetDailyAsync(
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

        var report = await new ReportingService(db).GetDailyAsync(
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

        var report = await new ReportingService(db).GetDailyAsync(
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

        var service = new ReportingService(db);
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

        var report = await new ReportingService(db).GetBookkeepingAsync(
            new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 31)));

        Assert.Equal(5m, report.OtherOperatingExpenses);
        Assert.Equal(95m, report.NetProfit);
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

        var report = await new ReportingService(db).GetReconciliationAsync(
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

        var report = await new ReportingService(db).GetReconciliationAsync(
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

        var report = await new ReportingService(db).GetReconciliationAsync(
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

        var report = await new ReportingService(db).GetReconciliationAsync(
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
