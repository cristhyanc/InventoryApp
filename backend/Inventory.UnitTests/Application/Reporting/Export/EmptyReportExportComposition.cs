using Inventory.Application.Reporting.Bookkeeping;
using Inventory.Application.Reporting.Dashboard;
using Inventory.Application.Reporting.Daily;
using Inventory.Application.Reporting.Export;
using Inventory.Application.Reporting.Gst;
using Inventory.Application.Reporting.MachineProfitability;
using Inventory.Application.Reporting.ProductProfitability;
using Inventory.Application.Reporting.Reconciliation;
using Inventory.Application.Reporting.Shared;
using Inventory.Application.Reporting.Transactions;
using InventoryApi.Tests.Application.Reporting.Bookkeeping;
using InventoryApi.Tests.Application.Reporting.Dashboard;
using InventoryApi.Tests.Application.Reporting.Daily;
using InventoryApi.Tests.Application.Reporting.Gst;
using InventoryApi.Tests.Application.Reporting.MachineProfitability;
using InventoryApi.Tests.Application.Reporting.ProductProfitability;
using InventoryApi.Tests.Application.Reporting.Reconciliation;
using InventoryApi.Tests.Application.Reporting.Transactions;

namespace InventoryApi.Tests.Application.Reporting.Export;

/// <summary>
/// The export composition the empty-result tests share (issue #731). <see cref="Empty"/> is the one
/// they are about: the three per-record reports - transaction sales, product profitability and
/// machine profitability - produce no row at all on a given coverage record, which is the shape a
/// period with no sales leaves behind whatever the coverage behind it says, and the case in which an
/// export has no data row to carry the report's availability state.
///
/// <para>It lives in the unit test project and is compiled into the integration project as a linked
/// source file (AGENTS.md § Commands), because both halves of the rule are tested: the export-table
/// representation here, and the CSV/XLSX bytes the Infrastructure writer encodes from it there.</para>
/// </summary>
internal static class EmptyReportExportComposition
{
    /// <summary>A period the availability helpers' decided window comfortably contains.</summary>
    internal static readonly DateTime From = new(2026, 7, 1);

    /// <summary>That period's inclusive end.</summary>
    internal static readonly DateTime To = new(2026, 7, 31);

    /// <summary>
    /// The same composition with one machine row, so a test can show that a report which *has* rows
    /// keeps exactly its previous shape and gains no metadata row.
    /// </summary>
    internal static GetReportExportRows WithOneMachine(ReportCoverageFacts coverage) =>
        Build(coverage, FakeMachineProfitabilityReportFactsProvider.SingleMachine(coverage: coverage));

    internal static GetReportExportRows Empty(ReportCoverageFacts coverage) =>
        Build(coverage, FakeMachineProfitabilityReportFactsProvider.Empty(coverage));

    private static GetReportExportRows Build(
        ReportCoverageFacts coverage, MachineProfitabilityReportFacts machineFacts)
    {
        var bookkeeping = new GetBookkeepingReport(
            new FakeBookkeepingReportFactsProvider(FakeBookkeepingReportFactsProvider.Complete()));
        var daily = new GetDailyReport(new FakeDailyReportFactsProvider(
            FakeDailyReportFactsProvider.SingleDay()));
        var reconciliation = new GetReconciliationReport(new FakeReconciliationReportFactsProvider(
            FakeReconciliationReportFactsProvider.SinglePeriod()));
        var machines = new GetMachineProfitabilityReport(
            new FakeMachineProfitabilityReportFactsProvider(machineFacts));
        var products = new GetProductProfitabilityReport(
            new FakeProductProfitabilityReportFactsProvider(
                FakeProductProfitabilityReportFactsProvider.Empty(coverage)),
            FakeProductPurchaseCostFactsProvider.Empty());
        var gst = new GetGstAccountingAid(bookkeeping,
            new FakeGstReportFactsProvider(FakeGstReportFactsProvider.Complete()));
        var dashboard = new GetDashboardReport(bookkeeping,
            new FakeGetProductProfitabilityReport(
                new ProductProfitabilityReportDto(From, To, [], new ReportingDataQualityDto())),
            new FakeDashboardReportFactsProvider(FakeDashboardReportFactsProvider.Complete()));
        var transactions = new GetTransactionSalesReport(new FakeTransactionSalesReportFactsProvider(
            FakeTransactionSalesReportFactsProvider.Empty(coverage)));

        return new GetReportExportRows(bookkeeping, daily, reconciliation, machines, products, gst, dashboard, transactions);
    }
}
