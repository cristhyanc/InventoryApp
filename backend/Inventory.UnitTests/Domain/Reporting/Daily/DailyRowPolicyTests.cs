using Inventory.Domain.Reporting.Daily;
using Xunit;

namespace InventoryApi.Tests.Domain.Reporting.Daily;

public class DailyRowPolicyTests
{
    private static DailyRowInputs Complete(
        decimal grossSales = 100m,
        int transactionCount = 10,
        decimal partialCostOfGoods = 40m,
        bool isCogsComplete = true,
        decimal cardSales = 80m,
        decimal importedReimbursement = 80m,
        bool hasImportedReimbursement = true,
        bool hasPeriodOnlyImportedData = false,
        bool hasDataQualityWarning = false,
        decimal tolerance = 0.01m) => new(
        GrossSales: grossSales,
        TransactionCount: transactionCount,
        PartialCostOfGoods: partialCostOfGoods,
        IsCogsComplete: isCogsComplete,
        CardSales: cardSales,
        ImportedReimbursement: importedReimbursement,
        HasImportedReimbursement: hasImportedReimbursement,
        HasPeriodOnlyImportedData: hasPeriodOnlyImportedData,
        HasDataQualityWarning: hasDataQualityWarning,
        Tolerance: tolerance);

    [Fact]
    public void Complete_cogs_returns_gross_profit_margin_and_reconciled_status()
    {
        var result = DailyRowPolicy.Calculate(Complete());

        Assert.Equal(40m, result.CostOfGoods);
        Assert.Equal(60m, result.GrossProfit);
        Assert.Equal(60m, result.GrossMarginPercent);
        Assert.Equal(10m, result.AverageSale);
        Assert.True(result.IsReconciled);
        Assert.Equal("Reconciled", result.ReconciliationStatus);
    }

    [Fact]
    public void Incomplete_cogs_makes_cost_profit_and_margin_null_but_still_computes_average()
    {
        var result = DailyRowPolicy.Calculate(Complete(isCogsComplete: false));

        Assert.Null(result.CostOfGoods);
        Assert.Null(result.GrossProfit);
        Assert.Null(result.GrossMarginPercent);
        Assert.Equal(10m, result.AverageSale);
    }

    [Fact]
    public void Zero_transactions_does_not_throw_and_reports_zero_average()
    {
        var result = DailyRowPolicy.Calculate(Complete(grossSales: 0m, transactionCount: 0, partialCostOfGoods: 0m));

        Assert.Equal(0m, result.AverageSale);
        Assert.Equal(0m, result.GrossMarginPercent);
    }

    [Fact]
    public void No_imported_reimbursement_and_no_period_only_data_is_pending_and_not_reconciled()
    {
        var result = DailyRowPolicy.Calculate(Complete(hasImportedReimbursement: false, importedReimbursement: 0m));

        Assert.False(result.IsReconciled);
        Assert.Equal("Pending", result.ReconciliationStatus);
    }

    [Fact]
    public void Period_only_imported_data_is_not_pending_but_carries_a_warning()
    {
        var result = DailyRowPolicy.Calculate(Complete(
            cardSales: 0m, importedReimbursement: 0m, hasImportedReimbursement: false, hasPeriodOnlyImportedData: true));

        Assert.False(result.IsReconciled);
        Assert.Equal("Warning", result.ReconciliationStatus);
    }

    [Fact]
    public void Difference_beyond_tolerance_is_a_mismatch()
    {
        var result = DailyRowPolicy.Calculate(Complete(cardSales: 80m, importedReimbursement: 70m));

        Assert.False(result.IsReconciled);
        Assert.Equal("Mismatch", result.ReconciliationStatus);
    }

    [Fact]
    public void Difference_exactly_at_tolerance_boundary_is_reconciled()
    {
        var result = DailyRowPolicy.Calculate(Complete(cardSales: 80.01m, importedReimbursement: 80m, tolerance: 0.01m));

        Assert.True(result.IsReconciled);
        Assert.Equal("Reconciled", result.ReconciliationStatus);
    }

    [Fact]
    public void Data_quality_warning_surfaces_as_warning_status_even_when_reconciled()
    {
        var result = DailyRowPolicy.Calculate(Complete(hasDataQualityWarning: true));

        Assert.True(result.IsReconciled);
        Assert.Equal("Warning", result.ReconciliationStatus);
    }
}
