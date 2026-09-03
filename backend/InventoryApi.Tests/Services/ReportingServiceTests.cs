using System;
using System.Threading.Tasks;
using InventoryApi.Data;
using InventoryApi.DTOs;
using InventoryApi.Models;
using InventoryApi.Services;
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
    public async Task Product_profitability_uses_decimal_cost_and_zero_safe_margin()
    {
        using var db = CreateDbContext();
        db.Products.Add(new Product { Id = 1, Name = "Known", UnitPrice = 3m });
        db.NayaxSales.Add(new NayaxSales
        {
            TransactionID = 1, MachineID = 10, NayaxProductId = 1,
            SettlementValue = 10m, Quantity = 2, MachineAuthorizationTime = new DateTime(2025, 8, 1)
        });
        db.NayaxSales.Add(new NayaxSales
        {
            TransactionID = 2, MachineID = 10, NayaxProductId = 99,
            SettlementValue = 0m, Quantity = 1, ProductName = "Unknown",
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
    public async Task Date_and_machine_filters_are_inclusive_and_machine_specific()
    {
        using var db = CreateDbContext();
        db.NayaxSales.AddRange(
            new NayaxSales { TransactionID = 1, MachineID = 10, SettlementValue = 5m, Quantity = 1, MachineAuthorizationTime = new DateTime(2025, 7, 1, 23, 59, 59) },
            new NayaxSales { TransactionID = 2, MachineID = 11, SettlementValue = 8m, Quantity = 1, MachineAuthorizationTime = new DateTime(2025, 7, 1, 12, 0, 0) },
            new NayaxSales { TransactionID = 3, MachineID = 10, SettlementValue = 9m, Quantity = 1, MachineAuthorizationTime = new DateTime(2025, 7, 2) });
        await db.SaveChangesAsync();

        var report = await new ReportingService(db).GetDailyAsync(
            new ReportingFilterDto(new DateTime(2025, 7, 1), new DateTime(2025, 7, 1), 10));

        var row = Assert.Single(report.Rows);
        Assert.Equal(5m, row.Sales);
        Assert.Equal(1, row.TransactionCount);
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
            SettlementValue = 110m, Quantity = 1, MachineAuthorizationTime = new DateTime(2025, 8, 1)
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
            TransactionID = 20, MachineID = 10, SettlementValue = 100m, Quantity = 1,
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
            TransactionID = 1, MachineID = 10, SettlementValue = 100m, Quantity = 1,
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
            TransactionID = 2, MachineID = 10, SettlementValue = 50m, Quantity = 1,
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
                TransactionID = 10, MachineID = 1216029552, SettlementValue = 90.30m, Quantity = 1,
                MachineAuthorizationTime = new DateTime(2025, 8, 12)
            },
            new NayaxSales
            {
                TransactionID = 11, MachineID = 1216029562, SettlementValue = 42.90m, Quantity = 1,
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
}
