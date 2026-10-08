using Inventory.Application.Reporting.Bookkeeping;
using Inventory.Application.Reporting.Export;
using Inventory.Application.Reporting.Shared;
using Inventory.Application.Reporting.Transactions;
using InventoryApi.Tests.Application.Reporting.Bookkeeping;
using InventoryApi.Tests.Application.Reporting.Transactions;
using Xunit;

namespace InventoryApi.Tests.Application.Reporting.Export;

/// <summary>
/// Focused tests for the export use case that replaced the removed legacy reporting service's
/// export row-building: every row must come from the same authoritative report use case the API
/// response uses, never a re-derived value.
/// </summary>
public class GetReportExportRowsTests
{
    private static GetReportExportRows Sut(GetBookkeepingReport? bookkeeping = null, GetTransactionSalesReport? transactions = null,
        Inventory.Application.Reporting.Daily.GetDailyReport? daily = null,
        Inventory.Application.Reporting.Gst.GetGstAccountingAid? gstAccountingAid = null)
    {
        bookkeeping ??= new GetBookkeepingReport(new FakeBookkeepingReportFactsProvider(FakeBookkeepingReportFactsProvider.Complete()));
        transactions ??= new GetTransactionSalesReport(new FakeTransactionSalesReportFactsProvider(FakeTransactionSalesReportFactsProvider.Empty()));
        daily ??= new Inventory.Application.Reporting.Daily.GetDailyReport(
            new InventoryApi.Tests.Application.Reporting.Daily.FakeDailyReportFactsProvider(
                InventoryApi.Tests.Application.Reporting.Daily.FakeDailyReportFactsProvider.SingleDay()));
        var reconciliation = new Inventory.Application.Reporting.Reconciliation.GetReconciliationReport(
            new InventoryApi.Tests.Application.Reporting.Reconciliation.FakeReconciliationReportFactsProvider(
                InventoryApi.Tests.Application.Reporting.Reconciliation.FakeReconciliationReportFactsProvider.SinglePeriod()));
        var machineProfitability = new Inventory.Application.Reporting.MachineProfitability.GetMachineProfitabilityReport(
            new InventoryApi.Tests.Application.Reporting.MachineProfitability.FakeMachineProfitabilityReportFactsProvider(
                InventoryApi.Tests.Application.Reporting.MachineProfitability.FakeMachineProfitabilityReportFactsProvider.Empty()));
        var productProfitability = new Inventory.Application.Reporting.ProductProfitability.GetProductProfitabilityReport(
            new InventoryApi.Tests.Application.Reporting.ProductProfitability.FakeProductProfitabilityReportFactsProvider(
                InventoryApi.Tests.Application.Reporting.ProductProfitability.FakeProductProfitabilityReportFactsProvider.Empty()),
            InventoryApi.Tests.Application.Reporting.ProductProfitability.FakeProductPurchaseCostFactsProvider.Empty());
        var gst = gstAccountingAid ?? new Inventory.Application.Reporting.Gst.GetGstAccountingAid(bookkeeping,
            new InventoryApi.Tests.Application.Reporting.Gst.FakeGstReportFactsProvider(
                InventoryApi.Tests.Application.Reporting.Gst.FakeGstReportFactsProvider.Complete()));
        var dashboard = new Inventory.Application.Reporting.Dashboard.GetDashboardReport(bookkeeping,
            new InventoryApi.Tests.Application.Reporting.ProductProfitability.FakeGetProductProfitabilityReport(
                new Inventory.Application.Reporting.ProductProfitability.ProductProfitabilityReportDto(default, default, [], new ReportingDataQualityDto())),
            new InventoryApi.Tests.Application.Reporting.Dashboard.FakeDashboardReportFactsProvider(
                InventoryApi.Tests.Application.Reporting.Dashboard.FakeDashboardReportFactsProvider.Complete()));

        return new GetReportExportRows(bookkeeping, daily, reconciliation, machineProfitability,
            productProfitability, gst, dashboard, transactions);
    }

    [Fact]
    public async Task Bookkeeping_export_reuses_the_same_authoritative_result_as_the_api_report()
    {
        var bookkeeping = new GetBookkeepingReport(new FakeBookkeepingReportFactsProvider(
            FakeBookkeepingReportFactsProvider.Complete(sales: 250m, cost: 90m)));
        var apiResult = await bookkeeping.Handle(new ReportingFilterDto(new DateTime(2025, 7, 1), new DateTime(2025, 7, 31)), CancellationToken.None);

        var table = await Sut(bookkeeping: bookkeeping).Handle("bookkeeping",
            new ReportingFilterDto(new DateTime(2025, 7, 1), new DateTime(2025, 7, 31)), CancellationToken.None);

        Assert.Equal("Report", table.SheetName);
        var header = table.Rows[0];
        var values = table.Rows[1];
        var grossSales = values[Array.IndexOf(header.ToArray(), "GrossSales")];
        Assert.Equal(apiResult.Sales.ToString(System.Globalization.CultureInfo.InvariantCulture), grossSales);
    }

    private static string Cell(ReportExportTable table, string column)
    {
        var index = table.Rows[0].ToList().IndexOf(column);
        Assert.True(index >= 0, $"The export has no '{column}' column.");
        return table.Rows[1][index];
    }

    [Fact]
    public async Task Bookkeeping_export_carries_the_same_conditional_limitations_as_the_api_report()
    {
        var bookkeeping = new GetBookkeepingReport(new FakeBookkeepingReportFactsProvider(
            FakeBookkeepingReportFactsProvider.Complete() with
            {
                IsCogsComplete = false,
                UncostedTransactionCount = 2,
                UncostedSalesAmount = 12.5m,
                MissingStatusTransactionCount = 1,
                UnknownStatusTransactionCount = 3,
                DeclinedOrCancelledTransactionCount = 4
            }));
        var filter = new ReportingFilterDto(new DateTime(2025, 7, 1), new DateTime(2025, 7, 31));
        var apiResult = await bookkeeping.Handle(filter, CancellationToken.None);

        var table = await Sut(bookkeeping: bookkeeping).Handle("bookkeeping", filter, CancellationToken.None);

        Assert.Equal("False", Cell(table, "IsCogsComplete"));
        Assert.Equal("2", Cell(table, "UncostedTransactionCount"));
        Assert.Equal("12.5", Cell(table, "UncostedSalesAmount"));
        Assert.Equal("1", Cell(table, "MissingStatusTransactionCount"));
        Assert.Equal("3", Cell(table, "UnknownStatusTransactionCount"));
        Assert.Equal("4", Cell(table, "DeclinedOrCancelledTransactionCount"));
        Assert.Equal(string.Join(" ", apiResult.DataQuality.Notes!), Cell(table, "DataQualityNotes"));
        Assert.Contains("no persisted COGS", Cell(table, "DataQualityNotes"));
        Assert.Contains("have no status ID", Cell(table, "DataQualityNotes"));
    }

    [Fact]
    public async Task Bookkeeping_export_states_the_gst_on_sales_estimate_basis_even_for_a_clean_period()
    {
        var bookkeeping = new GetBookkeepingReport(new FakeBookkeepingReportFactsProvider(
            FakeBookkeepingReportFactsProvider.Complete()));
        var filter = new ReportingFilterDto(new DateTime(2025, 7, 1), new DateTime(2025, 7, 31));

        var table = await Sut(bookkeeping: bookkeeping).Handle("bookkeeping", filter, CancellationToken.None);

        Assert.Equal(string.Empty, Cell(table, "DataQualityNotes"));
        Assert.Equal(
            "Estimated GST on sales - assumes all included sales are taxable at 10% (GST-inclusive).",
            Cell(table, "GstOnSalesBasis"));
    }

    [Fact]
    public async Task Transactions_export_uses_the_transactions_sheet_name_and_the_unpaginated_result()
    {
        var row = FakeTransactionSalesReportFactsProvider.CompletedCardRow(transactionId: 7, sale: 42m);
        var facts = FakeTransactionSalesReportFactsProvider.Facts([row]);
        var transactions = new GetTransactionSalesReport(new FakeTransactionSalesReportFactsProvider(facts));

        var table = await Sut(transactions: transactions).Handle("transactions", new TransactionSalesFilterDto(), CancellationToken.None);

        Assert.Equal("Transactions", table.SheetName);
        Assert.Equal(2, table.Rows.Count);
        Assert.Contains("7", table.Rows[1]);
    }

    private static Inventory.Application.Reporting.Gst.GetGstAccountingAid GstWith(
        GetBookkeepingReport bookkeeping,
        params Inventory.Domain.Reporting.Gst.PurchaseGstComponents[] purchases) =>
        new(bookkeeping, new InventoryApi.Tests.Application.Reporting.Gst.FakeGstReportFactsProvider(
            InventoryApi.Tests.Application.Reporting.Gst.FakeGstReportFactsProvider.Complete(purchases: purchases)));

    [Fact]
    public async Task Gst_export_carries_the_purchase_input_gst_columns_from_the_same_authoritative_result()
    {
        var bookkeeping = new GetBookkeepingReport(new FakeBookkeepingReportFactsProvider(FakeBookkeepingReportFactsProvider.Complete()));
        var gst = GstWith(bookkeeping, new Inventory.Domain.Reporting.Gst.PurchaseGstComponents(
            [new Inventory.Domain.Purchases.PurchaseGstLine(2m, 5.50m, Inventory.Domain.Gst.GstClassification.Taxable)],
            new Inventory.Domain.Purchases.PurchaseGstCharge(11m, Inventory.Domain.Gst.GstClassification.Taxable),
            new Inventory.Domain.Purchases.PurchaseGstCharge(22m, Inventory.Domain.Gst.GstClassification.Unknown)));
        var filter = new ReportingFilterDto(new DateTime(2025, 7, 1), new DateTime(2025, 7, 31));
        var apiResult = await gst.Handle(filter, CancellationToken.None);

        var table = await Sut(bookkeeping: bookkeeping, gstAccountingAid: gst).Handle("gst", filter, CancellationToken.None);

        var header = table.Rows[0];
        var values = table.Rows[1];
        string Column(string name) => values[header.ToList().IndexOf(name)];
        Assert.Equal(header.Count, values.Count);
        Assert.Equal(Invariant(apiResult.PurchaseLineGst), Column("PurchaseLineGst"));
        Assert.Equal(Invariant(apiResult.PurchaseChargeGst), Column("PurchaseChargeGst"));
        Assert.Equal(Invariant(apiResult.InventoryPurchaseGst), Column("PurchaseInputGst"));
        Assert.Equal(apiResult.PurchaseUnresolvedComponentCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Column("PurchaseUnresolvedComponents"));
        Assert.Equal(Invariant(apiResult.PurchaseUnresolvedAmount), Column("PurchaseUnresolvedAmount"));
        Assert.Equal(apiResult.PurchaseGstIncomplete.ToString(), Column("PurchaseGstIncomplete"));
        Assert.Equal(Invariant(apiResult.NetGst), Column("NetGst"));
        Assert.Equal("True", Column("PurchaseGstIncomplete"));
    }

    [Fact]
    public async Task Gst_export_of_a_period_with_no_purchases_reports_zero_and_a_complete_purchase_status()
    {
        var bookkeeping = new GetBookkeepingReport(new FakeBookkeepingReportFactsProvider(FakeBookkeepingReportFactsProvider.Complete()));
        var filter = new ReportingFilterDto(new DateTime(2025, 7, 1), new DateTime(2025, 7, 31));

        var table = await Sut(bookkeeping: bookkeeping, gstAccountingAid: GstWith(bookkeeping)).Handle("gst", filter, CancellationToken.None);

        var header = table.Rows[0].ToList();
        var values = table.Rows[1];
        Assert.Equal("0", values[header.IndexOf("PurchaseInputGst")]);
        Assert.Equal("0", values[header.IndexOf("PurchaseUnresolvedComponents")]);
        Assert.Equal("False", values[header.IndexOf("PurchaseGstIncomplete")]);
    }

    private static string Invariant(decimal value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task Unsupported_transaction_report_name_throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Sut().Handle("not-a-report", new TransactionSalesFilterDto(), CancellationToken.None));
    }

    [Fact]
    public async Task Daily_export_header_detail_and_totals_rows_all_have_the_same_column_count()
    {
        var table = await Sut().Handle("daily", new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1)), CancellationToken.None);

        var headerLength = table.Rows[0].Count;
        Assert.Equal(20, headerLength);
        Assert.All(table.Rows, row => Assert.Equal(headerLength, row.Count));
    }

    [Fact]
    public async Task Daily_detail_row_fee_gst_is_read_from_the_authoritative_processing_fee_result_not_derived_by_subtraction()
    {
        var processingFees = new NayaxProcessingFeeResult(4m, 0.5m, 4.4m, 0m, 0m, 0m, 0, DateTime.UtcNow, null);
        var daily = new Inventory.Application.Reporting.Daily.GetDailyReport(
            new InventoryApi.Tests.Application.Reporting.Daily.FakeDailyReportFactsProvider(
                InventoryApi.Tests.Application.Reporting.Daily.FakeDailyReportFactsProvider.SingleDay(processingFees: processingFees)));

        var table = await Sut(daily: daily).Handle("daily", new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1)), CancellationToken.None);

        var header = table.Rows[0];
        var feeGstIndex = header.ToList().IndexOf("NayaxFeeGST");
        var detailRow = table.Rows[1];
        var totalsRow = table.Rows[2];

        Assert.Equal("0.5", detailRow[feeGstIndex]);
        Assert.Equal("0.5", totalsRow[feeGstIndex]);
        Assert.NotEqual((4.4m - 4m).ToString(System.Globalization.CultureInfo.InvariantCulture), detailRow[feeGstIndex]);
    }

    /// <summary>
    /// Issue #207's Last Cost/Lowest Cost/Saving-per-unit purchasing insights are a deliberately
    /// UI-only presentation (paired currency + supplier text in one report cell, per the issue's
    /// frontend presentation section) and are not added to the CSV/XLSX export contract.
    /// </summary>
    [Fact]
    public async Task Product_profitability_export_does_not_include_the_last_lowest_cost_or_saving_columns()
    {
        var table = await Sut().Handle("product-profitability", new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1)), CancellationToken.None);

        var header = table.Rows[0];
        Assert.DoesNotContain("LastCost", header);
        Assert.DoesNotContain("LowestCost", header);
        Assert.DoesNotContain("SavingPerUnit", header);
    }

    [Fact]
    public async Task Daily_totals_row_is_header_aligned_with_empty_fee_source_and_reconciliation_status()
    {
        var table = await Sut().Handle("daily", new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1)), CancellationToken.None);

        var header = table.Rows[0].ToList();
        var totalsRow = table.Rows[^1];
        Assert.Equal("TOTAL", totalsRow[0]);
        Assert.Equal(string.Empty, totalsRow[header.IndexOf("FeeSource")]);
        Assert.Equal(string.Empty, totalsRow[header.IndexOf("ReconciliationStatus")]);
        Assert.Equal("80", totalsRow[header.IndexOf("ImportedReimbursement")]);
        Assert.Equal("78", totalsRow[header.IndexOf("NetReimbursement")]);
        Assert.Equal("4.4", totalsRow[header.IndexOf("NayaxFeeIncGST")]);
        Assert.Equal("4", totalsRow[header.IndexOf("NayaxFeeExGst")]);
    }
}
