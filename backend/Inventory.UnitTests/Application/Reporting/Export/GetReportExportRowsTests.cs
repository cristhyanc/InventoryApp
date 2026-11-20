using System.Globalization;
using Inventory.Application.Reporting.Bookkeeping;
using Inventory.Application.Reporting.Export;
using Inventory.Application.Reporting.Shared;
using Inventory.Application.Reporting.Transactions;
using Inventory.Domain.Gst;
using Inventory.Domain.Reporting.Gst;
using InventoryApi.Tests.Application.Reporting.Bookkeeping;
using InventoryApi.Tests.Application.Reporting.Shared;
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
        Inventory.Application.Reporting.Gst.GetGstAccountingAid? gstAccountingAid = null,
        Inventory.Application.Reporting.Reconciliation.GetReconciliationReport? reconciliationReport = null,
        bool? gstRegistered = true)
    {
        bookkeeping ??= new GetBookkeepingReport(new FakeBookkeepingReportFactsProvider(FakeBookkeepingReportFactsProvider.Complete()));
        transactions ??= new GetTransactionSalesReport(new FakeTransactionSalesReportFactsProvider(FakeTransactionSalesReportFactsProvider.Empty()));
        daily ??= new Inventory.Application.Reporting.Daily.GetDailyReport(
            new InventoryApi.Tests.Application.Reporting.Daily.FakeDailyReportFactsProvider(
                InventoryApi.Tests.Application.Reporting.Daily.FakeDailyReportFactsProvider.SingleDay()));
        var reconciliation = reconciliationReport ?? new Inventory.Application.Reporting.Reconciliation.GetReconciliationReport(
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
            productProfitability, gst,
            InventoryApi.Tests.Application.Reporting.Gst.FakeGstRegistrationStore.BasSummary(gst, gstRegistered),
            dashboard, transactions);
    }

    /// <summary>The bookkeeping export's one data row, by column name (issue #1318).</summary>
    private static async Task<Dictionary<string, string>> BookkeepingRowAsync(GetReportExportRows sut)
    {
        var table = await sut.Handle("bookkeeping", new ReportingFilterDto(), CancellationToken.None);
        return table.Rows[0].Zip(table.Rows[1]).ToDictionary(pair => pair.First, pair => pair.Second);
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
    public async Task Bookkeeping_export_states_the_actual_gst_on_sales_rule_even_for_a_clean_period()
    {
        var bookkeeping = new GetBookkeepingReport(new FakeBookkeepingReportFactsProvider(
            FakeBookkeepingReportFactsProvider.Complete()));
        var filter = new ReportingFilterDto(new DateTime(2025, 7, 1), new DateTime(2025, 7, 31));

        var table = await Sut(bookkeeping: bookkeeping).Handle("bookkeeping", filter, CancellationToken.None);

        Assert.Equal(string.Empty, Cell(table, "DataQualityNotes"));
        // Issue #1255 replaced the "assumes all included sales are taxable" note with the rule the
        // figure actually follows: one eleventh of taxable sales, GST-free products excluded, no
        // rule still taxable, and each product's rule as it stands today.
        var basis = Cell(table, "GstOnSalesBasis");
        Assert.DoesNotContain("assumes all included sales are taxable", basis);
        Assert.Contains("one eleventh of taxable sales", basis);
        Assert.Contains("GST rule is GST-free are excluded", basis);
        Assert.Contains("products with no GST rule", basis);
        Assert.Contains("as it stands today", basis);
    }

    /// <summary>
    /// Issue #1255: both GST-bearing exports carry the sales split the screens show, so a
    /// downloaded file says which sales the GST figure was taken from. The bookkeeping columns are
    /// GST-inclusive like every other sales figure in that report; the aid's taxable sales stays the
    /// ex-GST figure it has always been, with the GST-free and no-rule sales beside it.
    /// </summary>
    [Fact]
    public async Task Gst_bearing_exports_carry_the_taxable_gst_free_and_no_rule_sales_split()
    {
        // Issue #1255's fixture: $11 of a taxable product, $3 of a GST-free product and $22 of a
        // product with no rule.
        var facts = FakeBookkeepingReportFactsProvider.Complete(sales: 36m, salesByProductGstRule:
        [
            new SalesByGstRule(GstClassification.Taxable, 11m),
            new SalesByGstRule(GstClassification.GstFree, 3m),
            new SalesByGstRule(GstRules.None, 22m),
        ]);
        var bookkeeping = new GetBookkeepingReport(new FakeBookkeepingReportFactsProvider(facts));
        var gstAccountingAid = new Inventory.Application.Reporting.Gst.GetGstAccountingAid(bookkeeping,
            new InventoryApi.Tests.Application.Reporting.Gst.FakeGstReportFactsProvider(
                InventoryApi.Tests.Application.Reporting.Gst.FakeGstReportFactsProvider.Complete()));
        var filter = new ReportingFilterDto(new DateTime(2025, 7, 1), new DateTime(2025, 7, 31));
        var sut = Sut(bookkeeping: bookkeeping, gstAccountingAid: gstAccountingAid);

        var bookkeepingTable = await sut.Handle("bookkeeping", filter, CancellationToken.None);
        var gstTable = await sut.Handle("gst", filter, CancellationToken.None);

        Assert.Equal(33m, Money(bookkeepingTable, "TaxableSales"));
        Assert.Equal(3m, Money(bookkeepingTable, "GstFreeSales"));
        Assert.Equal(22m, Money(bookkeepingTable, "SalesWithNoGstRule"));
        Assert.Equal(3m, Money(bookkeepingTable, "GstOnSales"));
        // The aid's taxable sales is the ex-GST figure it has always been, over the taxable sales
        // alone: $33 inclusive less its $3 of GST.
        Assert.Equal(30m, Money(gstTable, "TaxableSales"));
        Assert.Equal(3m, Money(gstTable, "GstFreeSales"));
        Assert.Equal(22m, Money(gstTable, "SalesWithNoGstRule"));
        Assert.Equal(3m, Money(gstTable, "GstOnSales"));
    }

    private static decimal Money(ReportExportTable table, string column) =>
        decimal.Parse(Cell(table, column), CultureInfo.InvariantCulture);

    /// <summary>
    /// Issue #477: the reconciliation export carries the same scoped diagnostics the API report
    /// returns - the per-period status exclusion counts, the period's own data-quality notes, and
    /// the adjustments assumption - so a downloaded file never implies a verified zero adjustment or
    /// repeats boilerplate the report no longer shows.
    /// </summary>
    [Fact]
    public async Task Reconciliation_export_carries_each_periods_own_status_counts_notes_and_the_adjustments_assumption()
    {
        var period = InventoryApi.Tests.Application.Reporting.Reconciliation.FakeReconciliationReportFactsProvider.Period(
            declinedOrCancelledTransactionCount: 17, missingStatusTransactionCount: 2);
        var facts = InventoryApi.Tests.Application.Reporting.Reconciliation.FakeReconciliationReportFactsProvider.SinglePeriod(
            period, declinedOrCancelledTransactionCount: 17, nullStatusTransactionCount: 2);
        var reconciliation = new Inventory.Application.Reporting.Reconciliation.GetReconciliationReport(
            new InventoryApi.Tests.Application.Reporting.Reconciliation.FakeReconciliationReportFactsProvider(facts));
        var filter = new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1));
        var apiResult = await reconciliation.Handle(filter, 0.01m, CancellationToken.None);

        var table = await Sut(reconciliationReport: reconciliation).Handle("reconciliation", filter, CancellationToken.None);

        var header = table.Rows[0].ToList();
        Assert.All(table.Rows, row => Assert.Equal(header.Count, row.Count));
        var periodRow = table.Rows[1];
        var totalsRow = table.Rows[^1];
        Assert.Equal("TOTAL", totalsRow[0]);
        Assert.Equal("17", periodRow[header.IndexOf("DeclinedOrCancelledTransactionCount")]);
        Assert.Equal("2", periodRow[header.IndexOf("MissingStatusTransactionCount")]);
        Assert.Equal("17", totalsRow[header.IndexOf("DeclinedOrCancelledTransactionCount")]);
        Assert.Equal("2", totalsRow[header.IndexOf("MissingStatusTransactionCount")]);
        Assert.Equal("False", periodRow[header.IndexOf("AdjustmentsSupported")]);
        Assert.Equal(Inventory.Application.Reporting.Reconciliation.GetReconciliationReport.AdjustmentsAssumption,
            periodRow[header.IndexOf("AdjustmentsBasis")]);
        Assert.Equal(Inventory.Application.Reporting.Reconciliation.GetReconciliationReport.AdjustmentsAssumption,
            totalsRow[header.IndexOf("AdjustmentsBasis")]);
        Assert.Equal(string.Join(" ", apiResult.PeriodRows[0].DataQuality.Notes!),
            periodRow[header.IndexOf("DataQualityNotes")]);
        Assert.Equal(string.Join(" ", apiResult.DataQuality.Notes!), totalsRow[header.IndexOf("DataQualityNotes")]);
        Assert.Contains("have no status ID", totalsRow[header.IndexOf("DataQualityNotes")]);
        Assert.DoesNotContain("cancelled or declined", totalsRow[header.IndexOf("DataQualityNotes")]);
    }

    [Fact]
    public async Task Reconciliation_export_of_a_clean_period_exports_no_data_quality_boilerplate()
    {
        var table = await Sut().Handle("reconciliation",
            new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1)), CancellationToken.None);

        var header = table.Rows[0].ToList();
        Assert.Equal(string.Empty, table.Rows[1][header.IndexOf("DataQualityNotes")]);
        Assert.Equal(string.Empty, table.Rows[^1][header.IndexOf("DataQualityNotes")]);
        Assert.Equal(Inventory.Application.Reporting.Reconciliation.GetReconciliationReport.AdjustmentsAssumption,
            table.Rows[1][header.IndexOf("AdjustmentsBasis")]);
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

    /// <summary>
    /// An amount the API reports as absent is an <em>empty</em> cell, never a zero (issue #1253 for
    /// the actual reimbursement, issue #730 for the rule).
    /// </summary>
    private static string Invariant(decimal? value) =>
        value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>
    /// A period no provider source accounted for and that holds no provider data, with every
    /// provider-sourced amount genuinely at zero, so the export's cells are the only thing that can
    /// tell "nobody measured this" from "it was zero". The business's own delivery, package and
    /// operating-expense figures stay as they are, because they must stay visible and exact.
    /// </summary>
    private static Inventory.Application.Reporting.Bookkeeping.BookkeepingReportFacts NoProviderData() =>
        FakeBookkeepingReportFactsProvider.Complete(
            sales: 0m, cost: 0m, cardSales: 0m, cardTransactions: 0, cashSales: 0m, cashTransactions: 0) with
        {
            Coverage = TestReportCoverage.Missing(),
            ImportedContainsRows = false,
            ImportedHasNetSettlement = false,
            ImportedNetSettlement = 0m,
            SiteCommission = 0m,
            OperatingExpensesGst = 0m,
            ProcessingFees = new NayaxProcessingFeeResult(0m, 0m, 0m, 0m, 0m, 0m, 0, null, null),
        };

    [Fact]
    public async Task Gst_export_reports_a_missing_provider_figure_as_its_state_and_never_as_a_zero()
    {
        // The export is the surface where an unlabelled 0 does the most damage: it is read away from
        // the application and pasted into a BAS working paper (issue #730, AGENTS.md § Nayax
        // processing fees and GST - never render a missing figure as 0 in an API, a UI or an export).
        var bookkeeping = new GetBookkeepingReport(new FakeBookkeepingReportFactsProvider(NoProviderData()));
        var filter = new ReportingFilterDto(new DateTime(2025, 7, 1), new DateTime(2025, 7, 31));

        var table = await Sut(bookkeeping: bookkeeping, gstAccountingAid: GstWith(bookkeeping))
            .Handle("gst", filter, CancellationToken.None);

        var header = table.Rows[0].ToList();
        var values = table.Rows[1];
        string Column(string name) => values[header.IndexOf(name)];
        Assert.Equal(header.Count, values.Count);

        foreach (var column in new[] { "TaxableSales", "GstOnSales", "TaxableFees", "GstOnFees", "NetGst" })
        {
            Assert.Equal("Missing", Column(column));
            Assert.NotEqual("0", Column(column));
        }

        Assert.Equal("Missing", Column("SalesDataAvailability"));
        Assert.Equal("Missing", Column("FeeDataAvailability"));
        Assert.Equal("Missing", Column("NetGstAvailability"));
        Assert.Equal(string.Empty, Column("SalesDataAsOfUtc"));
        Assert.Contains("not a measured zero", Column("DataQualityNotes"));

        // The business's own purchase input GST is a measured zero and stays one.
        Assert.Equal("0", Column("PurchaseInputGst"));
        Assert.Equal("0", Column("PurchaseLineGst"));
    }

    [Fact]
    public async Task Gst_export_of_a_completely_covered_period_is_unchanged_and_says_so()
    {
        var bookkeeping = new GetBookkeepingReport(new FakeBookkeepingReportFactsProvider(FakeBookkeepingReportFactsProvider.Complete()));
        var gst = GstWith(bookkeeping);
        var filter = new ReportingFilterDto(new DateTime(2025, 7, 1), new DateTime(2025, 7, 31));
        var apiResult = await gst.Handle(filter, CancellationToken.None);

        var table = await Sut(bookkeeping: bookkeeping, gstAccountingAid: gst).Handle("gst", filter, CancellationToken.None);

        var header = table.Rows[0].ToList();
        var values = table.Rows[1];
        string Column(string name) => values[header.IndexOf(name)];
        Assert.Equal(Invariant(apiResult.TaxableSales), Column("TaxableSales"));
        Assert.Equal(Invariant(apiResult.GstOnSales), Column("GstOnSales"));
        Assert.Equal(Invariant(apiResult.TaxableFees), Column("TaxableFees"));
        Assert.Equal(Invariant(apiResult.GstOnFees), Column("GstOnFees"));
        Assert.Equal(Invariant(apiResult.NetGst), Column("NetGst"));
        Assert.Equal("Complete", Column("SalesDataAvailability"));
        Assert.Equal("Complete", Column("FeeDataAvailability"));
        Assert.Equal("Complete", Column("NetGstAvailability"));
    }

    [Fact]
    public async Task Bookkeeping_export_reports_missing_provider_figures_as_states_and_keeps_the_local_ones_exact()
    {
        var bookkeeping = new GetBookkeepingReport(new FakeBookkeepingReportFactsProvider(NoProviderData()));
        var filter = new ReportingFilterDto(new DateTime(2025, 7, 1), new DateTime(2025, 7, 31));

        var table = await Sut(bookkeeping: bookkeeping).Handle("bookkeeping", filter, CancellationToken.None);

        var header = table.Rows[0].ToList();
        var values = table.Rows[1];
        string Column(string name) => values[header.IndexOf(name)];
        Assert.Equal(header.Count, values.Count);

        foreach (var column in new[]
        {
            "GrossSales", "CardSales", "CashSales", "CardTransactions", "CashTransactions", "COGS",
            "NayaxFeeExGst", "NayaxFeeGST", "NayaxFeeIncGST", "SiteCommission",
            // Issue #804: the completed-transaction count and the average sale are provider-sourced
            // too, so an uncovered period must not export either of them as a measured zero.
            "Transactions", "AverageSale"
        })
        {
            Assert.Equal("Missing", Column(column));
            Assert.NotEqual("0", Column(column));
        }

        Assert.Equal("Missing", Column("SalesDataAvailability"));
        Assert.Equal("Missing", Column("FeeDataAvailability"));
        Assert.Equal("Missing", Column("CombinedDataAvailability"));

        // Delivery, package and operating-expense figures come from the business's own records and
        // are never labelled with a provider state (AGENTS.md § Nayax processing fees and GST).
        Assert.Equal("2", Column("DeliveryCosts"));
        Assert.Equal("1", Column("PackageCosts"));
        Assert.Equal("1", Column("OtherOperatingExpenses"));
    }

    [Fact]
    public async Task Bookkeeping_export_of_a_completely_covered_period_keeps_every_figure_as_the_api_reported_it()
    {
        var bookkeeping = new GetBookkeepingReport(new FakeBookkeepingReportFactsProvider(
            FakeBookkeepingReportFactsProvider.Complete(sales: 250m, cost: 90m)));
        var filter = new ReportingFilterDto(new DateTime(2025, 7, 1), new DateTime(2025, 7, 31));
        var apiResult = await bookkeeping.Handle(filter, CancellationToken.None);

        var table = await Sut(bookkeeping: bookkeeping).Handle("bookkeeping", filter, CancellationToken.None);

        var header = table.Rows[0].ToList();
        var values = table.Rows[1];
        string Column(string name) => values[header.IndexOf(name)];
        Assert.Equal(Invariant(apiResult.Sales), Column("GrossSales"));
        Assert.Equal(Invariant(apiResult.CardSales), Column("CardSales"));
        Assert.Equal(Invariant(apiResult.CashSales), Column("CashSales"));
        Assert.Equal(Invariant(apiResult.CostOfGoods!.Value), Column("COGS"));
        Assert.Equal(Invariant(apiResult.NayaxFeesIncludingGst), Column("NayaxFeeIncGST"));
        Assert.Equal(Invariant(apiResult.NetSettlement), Column("NetSettlement"));
        Assert.Equal(Invariant(apiResult.SiteCommission), Column("SiteCommission"));
        Assert.Equal("Complete", Column("SalesDataAvailability"));
        Assert.Equal("Complete", Column("FeeDataAvailability"));
        Assert.Equal("Complete", Column("CombinedDataAvailability"));
    }

    /// <summary>
    /// Issue #804: the downloaded bookkeeping row carries the same completed-transaction count and
    /// average sale the API returned, so a file read away from the application cannot disagree with
    /// the page it came from.
    /// </summary>
    [Fact]
    public async Task Bookkeeping_export_carries_the_same_transaction_count_and_average_sale_as_the_api_report()
    {
        var bookkeeping = new GetBookkeepingReport(new FakeBookkeepingReportFactsProvider(
            FakeBookkeepingReportFactsProvider.Complete(sales: 250m, transactionCount: 10)));
        var filter = new ReportingFilterDto(new DateTime(2025, 7, 1), new DateTime(2025, 7, 31));
        var apiResult = await bookkeeping.Handle(filter, CancellationToken.None);

        var table = await Sut(bookkeeping: bookkeeping).Handle("bookkeeping", filter, CancellationToken.None);

        Assert.Equal(10, apiResult.TransactionCount);
        Assert.Equal(25m, apiResult.AverageSale);
        Assert.Equal(Invariant(apiResult.TransactionCount), Cell(table, "Transactions"));
        Assert.Equal(Invariant(apiResult.AverageSale), Cell(table, "AverageSale"));
    }

    /// <summary>
    /// A covered period that genuinely sold nothing exports a real zero for both, because only a
    /// <c>Complete</c> state may print a zero as a zero.
    /// </summary>
    [Fact]
    public async Task Bookkeeping_export_of_a_covered_period_with_no_sales_exports_a_real_zero_transaction_count_and_average()
    {
        var bookkeeping = new GetBookkeepingReport(new FakeBookkeepingReportFactsProvider(
            FakeBookkeepingReportFactsProvider.Complete(
                sales: 0m, cost: 0m, cardSales: 0m, cardTransactions: 0, cashSales: 0m, cashTransactions: 0)));
        var filter = new ReportingFilterDto(new DateTime(2025, 7, 1), new DateTime(2025, 7, 31));

        var table = await Sut(bookkeeping: bookkeeping).Handle("bookkeeping", filter, CancellationToken.None);

        Assert.Equal("Complete", Cell(table, "SalesDataAvailability"));
        Assert.Equal("0", Cell(table, "Transactions"));
        Assert.Equal("0", Cell(table, "AverageSale"));
    }

    /// <summary>
    /// Issue #805: the downloaded bookkeeping row carries the same expected-versus-actual
    /// reimbursement comparison the API report returned, so a file read away from the application
    /// cannot disagree with the page it came from - and the calculated expectation stays a separate
    /// column from the imported actual and from <c>NetSettlement</c>.
    /// </summary>
    [Fact]
    public async Task Bookkeeping_export_carries_the_same_reimbursement_comparison_as_the_api_report()
    {
        var bookkeeping = new GetBookkeepingReport(new FakeBookkeepingReportFactsProvider(
            FakeBookkeepingReportFactsProvider.Complete()));
        var filter = new ReportingFilterDto(new DateTime(2025, 7, 1), new DateTime(2025, 7, 31));
        var apiResult = await bookkeeping.Handle(filter, CancellationToken.None);

        var table = await Sut(bookkeeping: bookkeeping).Handle("bookkeeping", filter, CancellationToken.None);

        Assert.Equal(Invariant(apiResult.ExpectedReimbursement), Cell(table, "ExpectedReimbursement"));
        Assert.Equal(Invariant(apiResult.ActualReimbursement), Cell(table, "ActualReimbursement"));
        Assert.Equal(Invariant(apiResult.ReimbursementDifference), Cell(table, "ReimbursementDifference"));
        Assert.Equal(apiResult.ReconciliationStatus, Cell(table, "ReconciliationStatus"));
        Assert.Equal(apiResult.IsReconciled.ToString(), Cell(table, "IsReconciled"));
        Assert.Equal(Invariant(apiResult.ReconciliationTolerance), Cell(table, "ReconciliationTolerance"));
        Assert.Equal(Invariant(apiResult.NetSettlement), Cell(table, "NetSettlement"));
        Assert.NotEqual(Cell(table, "ExpectedReimbursement"), Cell(table, "ActualReimbursement"));
    }

    /// <summary>
    /// The same rule as every other provider figure in this file: an actual reimbursement nothing
    /// accounted for is exported as its state, never as a measured zero, while the status column
    /// still says the import is <c>Pending</c>.
    /// </summary>
    [Fact]
    public async Task Bookkeeping_export_reports_an_unavailable_reimbursement_comparison_as_its_state_not_a_zero()
    {
        var bookkeeping = new GetBookkeepingReport(new FakeBookkeepingReportFactsProvider(NoProviderData()));
        var filter = new ReportingFilterDto(new DateTime(2025, 7, 1), new DateTime(2025, 7, 31));

        var table = await Sut(bookkeeping: bookkeeping).Handle("bookkeeping", filter, CancellationToken.None);

        foreach (var column in new[] { "ExpectedReimbursement", "ActualReimbursement", "ReimbursementDifference" })
        {
            Assert.Equal("Missing", Cell(table, column));
            Assert.NotEqual("0", Cell(table, column));
        }

        Assert.Equal("Pending", Cell(table, "ReconciliationStatus"));
        Assert.Equal("False", Cell(table, "IsReconciled"));
        Assert.Equal("0.01", Cell(table, "ReconciliationTolerance"));
    }

    /// <summary>
    /// A covered period whose settlement import really did settle nothing exports a real zero, and
    /// its status says the two sides were compared - the distinction a reader cannot otherwise make
    /// from a file.
    /// </summary>
    [Fact]
    public async Task Bookkeeping_export_of_a_covered_period_exports_an_imported_zero_as_a_real_zero()
    {
        var bookkeeping = new GetBookkeepingReport(new FakeBookkeepingReportFactsProvider(
            FakeBookkeepingReportFactsProvider.Complete(
                sales: 0m, cost: 0m, cardSales: 0m, cardTransactions: 0, cashSales: 0m, cashTransactions: 0) with
            {
                ProcessingFees = new NayaxProcessingFeeResult(0m, 0m, 0m, 0m, 0m, 0m, 0, DateTime.UtcNow, null),
                ImportedNetSettlement = 0m,
            }));
        var filter = new ReportingFilterDto(new DateTime(2025, 7, 1), new DateTime(2025, 7, 31));

        var table = await Sut(bookkeeping: bookkeeping).Handle("bookkeeping", filter, CancellationToken.None);

        Assert.Equal("Complete", Cell(table, "FeeDataAvailability"));
        Assert.Equal("0", Cell(table, "ActualReimbursement"));
        Assert.Equal("0", Cell(table, "ReimbursementDifference"));
        Assert.Equal("Reconciled", Cell(table, "ReconciliationStatus"));
        Assert.Equal("True", Cell(table, "IsReconciled"));
    }

    /// <summary>
    /// Issue #1253: a range with no payout wholly inside it has no actual reimbursement, and in a
    /// fully accounted-for period that cell is <strong>blank</strong> - never <c>0</c>, which a
    /// reader pasting the file into a working paper would take for a settlement of nothing. The net
    /// settlement beside it is this report's own estimate and carries the <c>Estimated</c> marker,
    /// and the payout the range cut is listed with its period dates and total in a column of its own.
    /// </summary>
    [Fact]
    public async Task Bookkeeping_export_leaves_an_unavailable_actual_blank_and_marks_the_estimate()
    {
        var bookkeeping = new GetBookkeepingReport(new FakeBookkeepingReportFactsProvider(
            FakeBookkeepingReportFactsProvider.Complete(sales: 100m, cardSales: 100m, cashSales: 0m, cashTransactions: 0) with
            {
                ProcessingFees = new NayaxProcessingFeeResult(2.5m, 0.25m, 2.75m, 2.75m, 0m, 0m, 0, DateTime.UtcNow, null),
                ImportedHasNetSettlement = false,
                ImportedNetSettlement = 0m,
                ImportedPartlyCoveredPeriods =
                    [new ImportedReimbursementPeriodDto(new DateTime(2026, 9, 1), new DateTime(2026, 9, 7), 680.75m)],
            }));
        var filter = new ReportingFilterDto(new DateTime(2026, 9, 7), new DateTime(2026, 9, 7));

        var table = await Sut(bookkeeping: bookkeeping).Handle("bookkeeping", filter, CancellationToken.None);

        Assert.Equal("Complete", Cell(table, "FeeDataAvailability"));
        Assert.Equal(string.Empty, Cell(table, "ActualReimbursement"));
        Assert.Equal(string.Empty, Cell(table, "ReimbursementDifference"));
        Assert.Equal("Range cuts a payout period", Cell(table, "ReconciliationStatus"));
        Assert.Equal("False", Cell(table, "IsReconciled"));
        // The estimate itself is still published - card sales 100.00 less fees 2.75 - and is labelled.
        Assert.Equal("97.25", Cell(table, "NetSettlement"));
        Assert.Equal("Estimated", Cell(table, "NetSettlementSource"));
        Assert.Equal("2026-09-01 to 2026-09-07: 680.75", Cell(table, "PartlyCoveredPayouts"));
        Assert.Equal(string.Empty, Cell(table, "ReimbursementPeriods"));
        // The expected reimbursement is unchanged and stays on its own line.
        Assert.Equal("97.25", Cell(table, "ExpectedReimbursement"));
    }

    /// <summary>
    /// And a range whose payout lies wholly inside it exports the imported amount, the periods it is
    /// made of, and an <c>Imported</c> net settlement - no estimate marker (issue #1253).
    /// </summary>
    [Fact]
    public async Task Bookkeeping_export_marks_an_imported_net_settlement_and_lists_its_periods()
    {
        var bookkeeping = new GetBookkeepingReport(new FakeBookkeepingReportFactsProvider(
            FakeBookkeepingReportFactsProvider.Complete(sales: 700m, cardSales: 700m, cashSales: 0m, cashTransactions: 0) with
            {
                ProcessingFees = new NayaxProcessingFeeResult(17.5m, 1.75m, 19.25m, 19.25m, 0m, 0m, 0, DateTime.UtcNow, null),
                ImportedHasNetSettlement = true,
                ImportedNetSettlement = 680.75m,
                ImportedFullyCoveredPeriods =
                    [new ImportedReimbursementPeriodDto(new DateTime(2026, 9, 1), new DateTime(2026, 9, 7), 680.75m)],
            }));
        var filter = new ReportingFilterDto(new DateTime(2026, 9, 1), new DateTime(2026, 9, 7));

        var table = await Sut(bookkeeping: bookkeeping).Handle("bookkeeping", filter, CancellationToken.None);

        Assert.Equal("680.75", Cell(table, "ActualReimbursement"));
        Assert.Equal("680.75", Cell(table, "NetSettlement"));
        Assert.Equal("Imported", Cell(table, "NetSettlementSource"));
        Assert.Equal("2026-09-01 to 2026-09-07: 680.75", Cell(table, "ReimbursementPeriods"));
        Assert.Equal(string.Empty, Cell(table, "PartlyCoveredPayouts"));
        Assert.Equal("Reconciled", Cell(table, "ReconciliationStatus"));
    }

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
        // 20 report columns plus issue #731's five availability-state columns.
        Assert.Equal(25, headerLength);
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

    /// <summary>
    /// Issue #1318: the bookkeeping download carries the BAS summary its own screen shows, from the
    /// same shared calculation, so the file and the page cannot say different things about a period.
    /// </summary>
    [Fact]
    public async Task Bookkeeping_export_carries_the_bas_summary_for_a_gst_registered_business()
    {
        var row = await BookkeepingRowAsync(Sut());

        Assert.Equal("Available", row["BasSummaryStatus"]);
        Assert.Equal("100", row["BasG1TotalSales"]);
        Assert.Equal("9.09", row["Bas1AGstOnSales"]);
        Assert.Equal("0.5", row["Bas1BGstCredits"]);
        Assert.Equal("0", row["BasGstCreditsFromPurchases"]);
        Assert.Equal("0.1", row["BasGstCreditsFromOperatingExpenses"]);
        Assert.Equal("0.4", row["BasGstCreditsFromNayaxFees"]);
        Assert.Equal("8.59", row["BasEstimatedNetGst"]);
        Assert.Equal("Payable", row["BasNetGstOutcome"]);
        Assert.Equal("False", row["BasEstimateIncomplete"]);
    }

    [Fact]
    public async Task Bookkeeping_export_states_the_bas_figures_agree_with_the_gst_accounting_aid_export()
    {
        var sut = Sut();
        var bookkeepingRow = await BookkeepingRowAsync(sut);
        var gstTable = await sut.Handle("gst", new ReportingFilterDto(), CancellationToken.None);
        var gstRow = gstTable.Rows[0].Zip(gstTable.Rows[1]).ToDictionary(pair => pair.First, pair => pair.Second);

        Assert.Equal(gstRow["GstOnSales"], bookkeepingRow["Bas1AGstOnSales"]);
        Assert.Equal(gstRow["GstOnFees"], bookkeepingRow["BasGstCreditsFromNayaxFees"]);
        Assert.Equal(gstRow["PurchaseInputGst"], bookkeepingRow["BasGstCreditsFromPurchases"]);
        Assert.Equal(gstRow["NetGst"], bookkeepingRow["BasEstimatedNetGst"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task Bookkeeping_export_writes_no_bas_figure_at_all_for_a_business_that_is_not_registered(bool? registered)
    {
        // Empty cells, never zeros: a downloaded zero in a BAS column would be read as a nil BAS by
        // exactly the reader this file is dangerous for.
        var row = await BookkeepingRowAsync(Sut(gstRegistered: registered));

        Assert.Equal("NotGstRegistered", row["BasSummaryStatus"]);
        foreach (var column in new[]
        {
            "BasG1TotalSales", "Bas1AGstOnSales", "Bas1BGstCredits", "BasGstCreditsFromPurchases",
            "BasGstCreditsFromOperatingExpenses", "BasGstCreditsFromNayaxFees", "BasEstimatedNetGst",
            "BasNetGstOutcome", "BasEstimateIncomplete"
        })
            Assert.Equal(string.Empty, row[column]);
    }

    [Fact]
    public async Task Bookkeeping_export_states_no_bas_outcome_for_a_net_amount_nothing_measured()
    {
        // The amount cell already substitutes the coverage state for an unmeasured zero. An outcome
        // cell reading "Nil" beside it would put a nil BAS in the file anyway - the one thing these
        // columns must never say about a period nobody measured.
        var bookkeeping = new GetBookkeepingReport(new FakeBookkeepingReportFactsProvider(NoProviderData()));

        var row = await BookkeepingRowAsync(Sut(bookkeeping: bookkeeping));

        Assert.Equal("Missing", row["BasEstimatedNetGst"]);
        Assert.Equal(string.Empty, row["BasNetGstOutcome"]);
        Assert.Equal("True", row["BasEstimateIncomplete"]);
        // The business's own credits are a measured zero and stay exact beside it.
        Assert.Equal("0", row["BasGstCreditsFromPurchases"]);
        Assert.Equal("0", row["BasGstCreditsFromOperatingExpenses"]);
    }

    [Fact]
    public async Task Bookkeeping_export_keeps_the_bas_outcome_of_a_real_amount_in_an_incomplete_period()
    {
        // Partial provider data is real data: the amount is printed, so its outcome is too.
        var bookkeeping = new GetBookkeepingReport(new FakeBookkeepingReportFactsProvider(
            FakeBookkeepingReportFactsProvider.Complete() with { Coverage = TestReportCoverage.Unverified() }));

        var row = await BookkeepingRowAsync(Sut(bookkeeping: bookkeeping));

        Assert.Equal("8.59", row["BasEstimatedNetGst"]);
        Assert.Equal("Payable", row["BasNetGstOutcome"]);
        Assert.Equal("True", row["BasEstimateIncomplete"]);
    }

    [Fact]
    public async Task Bookkeeping_export_reports_the_machine_filter_instead_of_a_machine_scoped_bas_estimate()
    {
        var table = await Sut().Handle("bookkeeping", new ReportingFilterDto(MachineId: 10), CancellationToken.None);
        var row = table.Rows[0].Zip(table.Rows[1]).ToDictionary(pair => pair.First, pair => pair.Second);

        Assert.Equal("MachineFiltered", row["BasSummaryStatus"]);
        Assert.Equal(string.Empty, row["BasEstimatedNetGst"]);
    }
}
