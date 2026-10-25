using Inventory.Application.Reporting.Bookkeeping;
using Inventory.Application.Reporting.Shared;
using Inventory.Domain.Reporting.Availability;
using Xunit;

namespace InventoryApi.Tests.Application.Reporting.Bookkeeping;

/// <summary>
/// Issue #730 (parent #329 decision P9): the bookkeeping report reports how well recorded provider
/// coverage accounts for its provider-sourced figures, so missing provider data is never presented
/// as a real zero, and the business's own delivery/package and operating-expense figures stay
/// visible and exact whatever the provider data looks like.
/// </summary>
public class BookkeepingReportAvailabilityTests
{
    private static readonly DateTime From = new(2026, 7, 1);
    private static readonly DateTime To = new(2026, 7, 31);
    private static readonly DateTime EndExclusive = new(2026, 8, 1);

    private static Task<BookkeepingReportDto> Report(BookkeepingReportFacts facts) =>
        new GetBookkeepingReport(new FakeBookkeepingReportFactsProvider(facts))
            .Handle(new ReportingFilterDto(From, To), CancellationToken.None);

    private static string Notes(BookkeepingReportDto report) => string.Join(" ", report.DataQuality.Notes!);

    private static BookkeepingReportFacts NoProviderData(BookkeepingReportFacts facts) => facts with
    {
        GrossSales = 0m,
        CardSales = 0m,
        CashSales = 0m,
        CardTransactions = 0,
        CashTransactions = 0,
        TransactionCount = 0,
        PartialCostOfGoods = 0m,
        ImportedHasNetSettlement = false,
        ImportedNetSettlement = 0m,
        ImportedContainsRows = false,
        ProcessingFees = new NayaxProcessingFeeResult(0m, 0m, 0m, 0m, 0m, 0m, 0, null, null),
        SalesCoverage = [],
        SettlementCoverage = [],
        ContainsSalesRows = false,
    };

    [Fact]
    public async Task A_fully_covered_period_reports_every_provider_figure_complete()
    {
        var report = await Report(FakeBookkeepingReportFactsProvider.Complete());

        Assert.Equal(nameof(ReportAvailabilityState.Complete), report.ProviderAvailability.Sales.State);
        Assert.Equal(nameof(ReportAvailabilityState.Complete), report.ProviderAvailability.Fees.State);
        Assert.True(report.ProviderAvailability.Sales.IsComplete);
        Assert.DoesNotContain("coverage", Notes(report), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_fully_covered_period_with_no_sales_stays_complete_so_its_zero_is_a_real_zero()
    {
        var facts = NoProviderData(FakeBookkeepingReportFactsProvider.Complete()) with
        {
            SalesCoverage = [new ReportCoverageRange(From.AddDays(-10), EndExclusive.AddDays(2))],
            SettlementCoverage = [new ReportCoverageRange(From.AddDays(-10), EndExclusive.AddDays(2))],
        };

        var report = await Report(facts);

        Assert.Equal(nameof(ReportAvailabilityState.Complete), report.ProviderAvailability.Sales.State);
        Assert.Equal(nameof(ReportAvailabilityState.Complete), report.ProviderAvailability.Fees.State);
        Assert.Equal(0m, report.Sales);
    }

    [Fact]
    public async Task No_sales_data_and_no_coverage_is_missing_rather_than_a_zero_period()
    {
        var report = await Report(NoProviderData(FakeBookkeepingReportFactsProvider.Complete()));

        Assert.Equal(nameof(ReportAvailabilityState.Missing), report.ProviderAvailability.Sales.State);
        Assert.Equal(nameof(ReportAvailabilityState.Missing), report.ProviderAvailability.Fees.State);
        Assert.Contains("no sales data", Notes(report), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Sales_covered_only_up_to_a_point_are_stale_as_of_that_instant()
    {
        // Previously imported sales after a provider is disconnected: still reported, labelled.
        var lastCovered = new DateTime(2026, 7, 18, 6, 0, 0, DateTimeKind.Utc);
        var facts = FakeBookkeepingReportFactsProvider.Complete() with
        {
            SalesCoverage = [new ReportCoverageRange(From.AddDays(-30), lastCovered)],
        };

        var report = await Report(facts);

        Assert.Equal(nameof(ReportAvailabilityState.Stale), report.ProviderAvailability.Sales.State);
        Assert.Equal(lastCovered, report.ProviderAvailability.Sales.AsOfUtc);
        Assert.Equal(100m, report.Sales);
        Assert.Contains("as of", Notes(report), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Sales_stored_before_coverage_recording_began_are_unverified_not_complete_and_not_zero()
    {
        var facts = FakeBookkeepingReportFactsProvider.Complete() with { SalesCoverage = [] };

        var report = await Report(facts);

        Assert.Equal(nameof(ReportAvailabilityState.Unverified), report.ProviderAvailability.Sales.State);
        Assert.Equal(100m, report.Sales);
        Assert.Contains("not accounted for", Notes(report), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_gap_left_by_a_failed_run_makes_the_period_unverified()
    {
        var facts = FakeBookkeepingReportFactsProvider.Complete() with
        {
            SalesCoverage =
            [
                new ReportCoverageRange(From.AddDays(-1), new DateTime(2026, 7, 10, 0, 0, 0, DateTimeKind.Utc)),
                new ReportCoverageRange(new DateTime(2026, 7, 16, 0, 0, 0, DateTimeKind.Utc), EndExclusive),
            ],
        };

        var report = await Report(facts);

        Assert.Equal(nameof(ReportAvailabilityState.Unverified), report.ProviderAvailability.Sales.State);
    }

    [Fact]
    public async Task Settlement_coverage_is_decided_separately_from_sales_coverage()
    {
        var facts = FakeBookkeepingReportFactsProvider.Complete() with
        {
            SettlementCoverage = [],
            ImportedContainsRows = false,
        };

        var report = await Report(facts);

        Assert.Equal(nameof(ReportAvailabilityState.Complete), report.ProviderAvailability.Sales.State);
        Assert.Equal(nameof(ReportAvailabilityState.Missing), report.ProviderAvailability.Fees.State);
    }

    /// <summary>
    /// Issue #804: the transaction count and the average sale are provider-sourced, so the sales
    /// family's note has to name them. A period nothing covered reports both as zero in the
    /// contract, and that zero is exactly the one the state exists to stop a reader believing.
    /// </summary>
    [Fact]
    public async Task The_sales_note_names_the_transaction_count_and_average_sale_when_nothing_covered_the_period()
    {
        var report = await Report(NoProviderData(FakeBookkeepingReportFactsProvider.Complete()));

        Assert.Equal(nameof(ReportAvailabilityState.Missing), report.ProviderAvailability.Sales.State);
        Assert.Equal(0, report.TransactionCount);
        Assert.Equal(0m, report.AverageSale);
        Assert.Contains("transaction count", Notes(report), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("average sale", Notes(report), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Partially covered sales are real partial data: the count and the average stay exactly as
    /// counted, beside the stale state that says they may not be all of the period's.
    /// </summary>
    [Fact]
    public async Task Partially_covered_sales_keep_the_counted_transaction_count_and_average_sale()
    {
        var facts = FakeBookkeepingReportFactsProvider.Complete(sales: 100m, transactionCount: 10) with
        {
            SalesCoverage = [new ReportCoverageRange(From.AddDays(-30), new DateTime(2026, 7, 18, 6, 0, 0, DateTimeKind.Utc))],
        };

        var report = await Report(facts);

        Assert.Equal(nameof(ReportAvailabilityState.Stale), report.ProviderAvailability.Sales.State);
        Assert.Equal(10, report.TransactionCount);
        Assert.Equal(10m, report.AverageSale);
    }

    /// <summary>
    /// Issue #805: the imported actual reimbursement is settlement-sourced and the comparison built
    /// on it depends on both families, so the fee note has to name them and the combined state has
    /// to govern the difference. A period with no settlement import reports an actual of zero in the
    /// contract, and that zero is exactly the one the state exists to stop a reader believing.
    /// </summary>
    [Fact]
    public async Task The_fee_note_names_the_actual_reimbursement_and_the_reconciliation_when_no_settlement_covered_the_period()
    {
        var facts = FakeBookkeepingReportFactsProvider.Complete() with
        {
            SettlementCoverage = [],
            ImportedContainsRows = false,
            ImportedHasNetSettlement = false,
            ImportedNetSettlement = 0m,
        };

        var report = await Report(facts);

        Assert.Equal(nameof(ReportAvailabilityState.Missing), report.ProviderAvailability.Fees.State);
        Assert.Equal(nameof(ReportAvailabilityState.Missing), report.ProviderAvailability.Combined.State);
        Assert.Equal(0m, report.ActualReimbursement);
        Assert.Equal("Pending", report.ReconciliationStatus);
        Assert.Contains("actual reimbursement", Notes(report), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reconciliation", Notes(report), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Incomplete provider data never makes the comparison itself disappear: the amounts stay as
    /// calculated beside the state that says they may not be all of the period's, which is what the
    /// presentations substitute for a zero. Stale settlement coverage is still settlement coverage.
    /// </summary>
    [Fact]
    public async Task Partially_covered_settlement_keeps_the_imported_actual_reimbursement_it_read()
    {
        var lastCovered = new DateTime(2026, 7, 18, 6, 0, 0, DateTimeKind.Utc);
        var facts = FakeBookkeepingReportFactsProvider.Complete() with
        {
            SettlementCoverage = [new ReportCoverageRange(From.AddDays(-30), lastCovered)],
        };

        var report = await Report(facts);

        Assert.Equal(nameof(ReportAvailabilityState.Stale), report.ProviderAvailability.Fees.State);
        Assert.Equal(lastCovered, report.ProviderAvailability.Fees.AsOfUtc);
        Assert.Equal(74m, report.ActualReimbursement);
        Assert.Equal(75.6m, report.ExpectedReimbursement);
        Assert.Equal("Needs Review", report.ReconciliationStatus);
    }

    [Fact]
    public async Task Local_delivery_package_and_operating_expense_figures_stay_visible_with_no_provider_data()
    {
        var report = await Report(NoProviderData(FakeBookkeepingReportFactsProvider.Complete()));

        Assert.Equal(2m, report.DeliveryCosts);
        Assert.Equal(1m, report.PackageCosts);
        Assert.Equal(1m, report.StructuredOperatingExpenses);
        Assert.Equal(0.1m, report.OperatingExpenseGst);
    }
}
