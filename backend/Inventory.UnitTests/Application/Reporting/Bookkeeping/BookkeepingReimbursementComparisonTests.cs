using Inventory.Application.Reporting.Bookkeeping;
using Inventory.Application.Reporting.Shared;
using Inventory.Domain.Reporting.Dashboard;
using Xunit;

namespace InventoryApi.Tests.Application.Reporting.Bookkeeping;

/// <summary>
/// Issue #805: the bookkeeping report carries the expected-versus-actual Nayax reimbursement
/// comparison the reporting dashboard already showed, decided by the same Domain
/// <see cref="DashboardReimbursementPolicy"/> rather than by a second status or tolerance rule.
/// Expected reimbursement is calculated from this period's card sales and fees; actual
/// reimbursement is the imported settlement for the period, and the two must never be conflated -
/// with each other or with <see cref="BookkeepingReportDto.NetSettlement"/>, which keeps its own
/// established meaning.
/// </summary>
public class BookkeepingReimbursementComparisonTests
{
    private static readonly DateTime From = new(2026, 7, 1);
    private static readonly DateTime To = new(2026, 7, 31);

    private static Task<BookkeepingReportDto> Report(BookkeepingReportFacts facts, long? machineId = null) =>
        new GetBookkeepingReport(new FakeBookkeepingReportFactsProvider(facts))
            .Handle(new ReportingFilterDto(From, To, MachineId: machineId), CancellationToken.None);

    /// <summary>
    /// Facts whose card sales and fees make the expected reimbursement exactly
    /// <paramref name="imported"/> less <c>difference</c>, so each test states the one number it is
    /// about. Card sales are 100.00 and there are no fees at all, which keeps the arithmetic of the
    /// tolerance boundary tests visible.
    /// </summary>
    private static BookkeepingReportFacts WithImported(decimal imported, bool containsRows = true) =>
        FakeBookkeepingReportFactsProvider.Complete(sales: 100m, cardSales: 100m, cashSales: 0m, cashTransactions: 0) with
        {
            ProcessingFees = new NayaxProcessingFeeResult(0m, 0m, 0m, 0m, 0m, 0m, 0, DateTime.UtcNow, null),
            ImportedContainsRows = containsRows,
            ImportedHasNetSettlement = containsRows,
            ImportedNetSettlement = imported,
        };

    [Fact]
    public async Task Expected_is_card_sales_less_fees_including_gst_and_actual_is_the_imported_settlement()
    {
        var report = await Report(FakeBookkeepingReportFactsProvider.Complete());

        // Complete(): card sales 80.00, fees including GST 4.40, imported settlement 74.00.
        Assert.Equal(75.6m, report.ExpectedReimbursement);
        Assert.Equal(74m, report.ActualReimbursement);
        Assert.Equal(-1.6m, report.ReimbursementDifference);
        Assert.False(report.IsReconciled);
        Assert.Equal("Needs Review", report.ReconciliationStatus);
    }

    [Fact]
    public async Task Absent_imported_rows_report_pending_and_are_never_reconciled()
    {
        var report = await Report(WithImported(0m, containsRows: false));

        Assert.Equal("Pending", report.ReconciliationStatus);
        Assert.False(report.IsReconciled);
        Assert.Equal(100m, report.ExpectedReimbursement);
        Assert.Equal(0m, report.ActualReimbursement);
    }

    /// <summary>
    /// A settlement import that really did settle nothing is a measured zero and reconciles against
    /// a zero expectation; a period with no imported row at all reports the same two amounts and is
    /// still <c>Pending</c>. The status is what separates them, so neither can be read as the other.
    /// </summary>
    [Fact]
    public async Task An_imported_zero_stays_distinguishable_from_absent_imported_rows()
    {
        var noSales = FakeBookkeepingReportFactsProvider.Complete(
            sales: 0m, cost: 0m, cardSales: 0m, cardTransactions: 0, cashSales: 0m, cashTransactions: 0) with
        {
            ProcessingFees = new NayaxProcessingFeeResult(0m, 0m, 0m, 0m, 0m, 0m, 0, DateTime.UtcNow, null),
            ImportedNetSettlement = 0m,
        };

        var imported = await Report(noSales with { ImportedContainsRows = true, ImportedHasNetSettlement = true });
        var absent = await Report(noSales with { ImportedContainsRows = false, ImportedHasNetSettlement = false });

        Assert.Equal(0m, imported.ActualReimbursement);
        Assert.Equal(0m, imported.ReimbursementDifference);
        Assert.True(imported.IsReconciled);
        Assert.Equal("Reconciled", imported.ReconciliationStatus);

        Assert.Equal(0m, absent.ActualReimbursement);
        Assert.False(absent.IsReconciled);
        Assert.Equal("Pending", absent.ReconciliationStatus);
    }

    [Theory]
    [InlineData(100.01)]
    [InlineData(99.99)]
    public async Task A_difference_at_the_tolerance_in_either_direction_is_reconciled(double imported)
    {
        var report = await Report(WithImported((decimal)imported));

        Assert.True(report.IsReconciled);
        Assert.Equal("Reconciled", report.ReconciliationStatus);
        Assert.Equal((decimal)imported - 100m, report.ReimbursementDifference);
    }

    [Theory]
    [InlineData(100.02, 0.02)]
    [InlineData(99.98, -0.02)]
    public async Task A_difference_beyond_the_tolerance_needs_review_and_keeps_its_sign(double imported, double difference)
    {
        var report = await Report(WithImported((decimal)imported));

        Assert.False(report.IsReconciled);
        Assert.Equal("Needs Review", report.ReconciliationStatus);
        Assert.Equal((decimal)difference, report.ReimbursementDifference);
    }

    [Fact]
    public async Task The_tolerance_is_reported_so_no_presentation_restates_it()
    {
        var report = await Report(FakeBookkeepingReportFactsProvider.Complete());

        Assert.Equal(DashboardReimbursementPolicy.Tolerance, report.ReconciliationTolerance);
        Assert.Equal(0.01m, report.ReconciliationTolerance);
    }

    /// <summary>
    /// <c>NetSettlement</c> prefers the imported amount for the period and falls back to the
    /// calculated one; the comparison's expected reimbursement is always the calculated figure. The
    /// two are therefore different numbers for a period whose import disagrees, and this report must
    /// keep publishing both.
    /// </summary>
    [Fact]
    public async Task Net_settlement_keeps_its_established_meaning_beside_the_new_expected_figure()
    {
        var withImport = await Report(FakeBookkeepingReportFactsProvider.Complete());
        var withoutImport = await Report(FakeBookkeepingReportFactsProvider.Complete() with
        {
            ImportedHasNetSettlement = false,
        });

        Assert.Equal(74m, withImport.NetSettlement);
        Assert.Equal(75.6m, withImport.ExpectedReimbursement);
        Assert.Equal(75.6m, withoutImport.NetSettlement);
        Assert.Equal(75.6m, withoutImport.ExpectedReimbursement);
    }

    /// <summary>
    /// The comparison is built from the facts the port returned for the requested scope, which the
    /// adapter narrows by date range and machine. The use case must not widen or re-derive that
    /// scope: a machine-filtered report compares the machine-scoped figures it was given.
    /// </summary>
    [Fact]
    public async Task A_machine_filtered_report_compares_the_machine_scoped_facts_it_was_given()
    {
        var facts = WithImported(40m) with { CardSales = 40m };
        var provider = new FakeBookkeepingReportFactsProvider(facts);

        var report = await new GetBookkeepingReport(provider)
            .Handle(new ReportingFilterDto(From, To, MachineId: 10), CancellationToken.None);

        Assert.Equal((From, To, (long?)10), provider.LastRequest);
        Assert.Equal(40m, report.ExpectedReimbursement);
        Assert.Equal(40m, report.ActualReimbursement);
        Assert.Equal("Reconciled", report.ReconciliationStatus);
    }
}
