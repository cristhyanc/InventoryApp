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

    /// <summary>
    /// With no payout lying wholly inside the range there is nothing to compare, so the actual
    /// reimbursement is not available at all - never a zero (issue #1253) - and the expected figure
    /// still stands on its own line.
    /// </summary>
    [Fact]
    public async Task Absent_imported_rows_report_pending_and_are_never_reconciled()
    {
        var report = await Report(WithImported(0m, containsRows: false));

        Assert.Equal("Pending", report.ReconciliationStatus);
        Assert.False(report.IsReconciled);
        Assert.Equal(100m, report.ExpectedReimbursement);
        Assert.Null(report.ActualReimbursement);
        Assert.Null(report.ReimbursementDifference);
    }

    /// <summary>
    /// The issue's own fixture, at the use-case boundary: card sales of 100.00 for one day, fees of
    /// 2.75, and the only imported payout settles 1-7 September while the report was asked about
    /// 7 September. Nothing lies wholly inside the day, so the actual reimbursement is not available,
    /// the net settlement stays the 97.25 estimate and says it is one, the status names what
    /// happened instead of "Needs Review", and the payout is reported with its own dates and total.
    /// </summary>
    [Fact]
    public async Task A_day_inside_a_weekly_payout_reports_no_actual_an_estimate_and_the_payout_it_cut()
    {
        var partlyCovered = new ImportedReimbursementPeriodDto(
            new DateTime(2026, 9, 1), new DateTime(2026, 9, 7), 680.75m);
        var facts = FakeBookkeepingReportFactsProvider.Complete(
            sales: 100m, cardSales: 100m, cashSales: 0m, cashTransactions: 0) with
        {
            ProcessingFees = new NayaxProcessingFeeResult(2.5m, 0.25m, 2.75m, 2.75m, 0m, 0m, 0, DateTime.UtcNow, null),
            ImportedHasNetSettlement = false,
            ImportedNetSettlement = 0m,
            ImportedPartlyCoveredPeriods = [partlyCovered],
        };

        var report = await Report(facts);

        Assert.Null(report.ActualReimbursement);
        Assert.Null(report.ReimbursementDifference);
        Assert.False(report.IsReconciled);
        Assert.Equal("Range cuts a payout period", report.ReconciliationStatus);
        Assert.NotEqual("Needs Review", report.ReconciliationStatus);
        Assert.Equal(97.25m, report.ExpectedReimbursement);
        Assert.Equal(97.25m, report.NetSettlement);
        Assert.True(report.IsNetSettlementEstimated);
        Assert.Equal([partlyCovered], report.PartlyCoveredReimbursements);
        Assert.Empty(report.ReimbursementPeriods);
    }

    /// <summary>
    /// The same payout asked about the week it settles is wholly inside the range: it is the actual
    /// reimbursement, it is published with the period it covers, and the net settlement is the
    /// imported amount rather than an estimate.
    /// </summary>
    [Fact]
    public async Task A_payout_wholly_inside_the_range_is_the_actual_and_the_net_settlement_is_not_estimated()
    {
        var covered = new ImportedReimbursementPeriodDto(
            new DateTime(2026, 9, 1), new DateTime(2026, 9, 7), 680.75m);
        var facts = FakeBookkeepingReportFactsProvider.Complete(
            sales: 700m, cardSales: 700m, cashSales: 0m, cashTransactions: 0) with
        {
            ProcessingFees = new NayaxProcessingFeeResult(17.5m, 1.75m, 19.25m, 19.25m, 0m, 0m, 0, DateTime.UtcNow, null),
            ImportedHasNetSettlement = true,
            ImportedNetSettlement = 680.75m,
            ImportedFullyCoveredPeriods = [covered],
        };

        var report = await Report(facts);

        Assert.Equal(680.75m, report.ActualReimbursement);
        Assert.Equal(680.75m, report.NetSettlement);
        Assert.False(report.IsNetSettlementEstimated);
        Assert.Equal([covered], report.ReimbursementPeriods);
        Assert.Empty(report.PartlyCoveredReimbursements);
        Assert.Equal(680.75m, report.ExpectedReimbursement);
        Assert.Equal("Reconciled", report.ReconciliationStatus);
    }

    /// <summary>
    /// A payout the range cuts explains an absent actual; it can never change one that exists. A
    /// range holding both compares only what it covers in full and lists the rest beside it.
    /// </summary>
    [Fact]
    public async Task A_partly_covered_payout_is_listed_beside_a_fully_covered_one_and_changes_no_total()
    {
        var partlyCovered = new ImportedReimbursementPeriodDto(
            new DateTime(2026, 7, 28), new DateTime(2026, 8, 3), 500m);
        var facts = WithImported(100m) with { ImportedPartlyCoveredPeriods = [partlyCovered] };

        var report = await Report(facts);

        Assert.Equal(100m, report.ActualReimbursement);
        Assert.Equal(0m, report.ReimbursementDifference);
        Assert.Equal("Reconciled", report.ReconciliationStatus);
        Assert.Equal([partlyCovered], report.PartlyCoveredReimbursements);
    }

    /// <summary>
    /// A settlement import that really did settle nothing is a measured zero and reconciles against
    /// a zero expectation; a period with no payout wholly inside it has no actual reimbursement at
    /// all and is still <c>Pending</c>. Since issue #1253 the two differ in the figure itself - a
    /// zero against nothing - as well as in the status, so neither can be read as the other.
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

        Assert.Null(absent.ActualReimbursement);
        Assert.Null(absent.ReimbursementDifference);
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
