using System.Text.Json;
using Inventory.Application.Reporting.Bookkeeping;
using Inventory.Application.Reporting.Daily;
using Inventory.Application.Reporting.Dashboard;
using Inventory.Application.Reporting.Gst;
using Inventory.Application.Reporting.MachineProfitability;
using Inventory.Application.Reporting.Reconciliation;
using Inventory.Application.Reporting.Shared;
using Inventory.Application.Reporting.Transactions;
using Xunit;

namespace InventoryApi.Tests.DTOs;

/// <summary>
/// Locks the wire contract of representative reporting endpoints (bookkeeping, dashboard, a
/// profitability report, transactions, and reconciliation) after moving their contracts from
/// <c>InventoryApi.DTOs.ReportingDtos</c> into <c>Inventory.Application.Reporting.&lt;Feature&gt;</c>
/// namespaces (issue #66). The API controller returns these application types directly, so their
/// serialized shape under <see cref="JsonSerializerDefaults.Web"/> (the defaults <c>AddControllers()</c>
/// configures) is the actual public response. Nothing here changes property names, nullability, or
/// financial meaning; it only proves the move preserved them.
/// </summary>
public class ReportingContractJsonSerializationTests
{
    private static readonly JsonSerializerOptions WebDefaults = new(JsonSerializerDefaults.Web);

    private static IEnumerable<string> PropertyNames(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
    }

    [Fact]
    public void Bookkeeping_report_serializes_expected_properties_and_preserves_null_cogs_and_profit()
    {
        var dataQuality = new ReportingDataQualityDto();
        var report = new BookkeepingReportDto(
            new DateTime(2026, 7, 1), new DateTime(2026, 7, 31), "2026-27",
            Sales: 1000m, CostOfGoods: null, GrossProfit: null,
            Fees: 20m, NetSettlement: 980m, GstOnSales: 90.91m, GstOnFees: 2m,
            DataQuality: dataQuality);

        var json = JsonSerializer.Serialize(report, WebDefaults);

        Assert.Equal(
            new[]
            {
                "from", "to", "financialYear", "sales", "costOfGoods", "grossProfit", "fees", "netSettlement",
                "gstOnSales", "gstOnFees", "dataQuality", "siteCommission", "netProfit", "netMarginPercent",
                "nayaxFeesExGst", "nayaxFeesIncludingGst", "deliveryCosts", "packageCosts", "otherOperatingExpenses",
                "cardSales", "cashSales", "cardTransactionCount", "cashTransactionCount", "nayaxProcessingRate",
                "pendingTransactionCount", "refundedTransactionCount", "declinedOrCancelledTransactionCount",
                "unknownStatusTransactionCount", "partialCostOfGoods", "isCogsComplete", "uncostedTransactionCount",
                "uncostedSalesAmount", "structuredOperatingExpenses", "operatingExpenseGst",
                "operatingExpensesByCategory", "directProfit", "directMarginPercent", "nayaxProcessingFees",
            },
            PropertyNames(json));

        using var document = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("costOfGoods").ValueKind);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("grossProfit").ValueKind);
        Assert.Equal(1000m, document.RootElement.GetProperty("sales").GetDecimal());
    }

    [Fact]
    public void Gst_report_serializes_expected_properties_and_preserves_the_wire_shape()
    {
        var report = new GstAccountingAidDto(
            new DateTime(2026, 7, 1), new DateTime(2026, 7, 31),
            TaxableSales: 909.09m, GstOnSales: 90.91m, TaxableFees: 20m, GstOnFees: 2m, NetGst: 88.91m,
            DataQuality: new ReportingDataQualityDto());

        var json = JsonSerializer.Serialize(report, WebDefaults);

        Assert.Equal(
            new[]
            {
                "from", "to", "taxableSales", "gstOnSales", "taxableFees", "gstOnFees", "netGst",
                "dataQuality", "gstFreeSales", "inventoryPurchaseGst", "operatingExpenseGst",
            },
            PropertyNames(json));

        using var document = JsonDocument.Parse(json);
        Assert.Equal(909.09m, document.RootElement.GetProperty("taxableSales").GetDecimal());
        Assert.Equal(88.91m, document.RootElement.GetProperty("netGst").GetDecimal());
    }

    [Fact]
    public void Dashboard_report_serializes_expected_properties_and_preserves_null_cogs_and_profit()
    {
        var report = new DashboardReportDto(
            new DateTime(2026, 7, 1), new DateTime(2026, 7, 31), Sales: 500m, GrossProfit: null,
            Transactions: 10, Quantity: 25m, MachineCount: 2, ProductCount: 5, UnmappedProductCount: 0,
            DataQuality: new ReportingDataQualityDto());

        var json = JsonSerializer.Serialize(report, WebDefaults);

        Assert.Equal(
            new[]
            {
                "from", "to", "sales", "grossProfit", "transactions", "quantity", "machineCount", "productCount",
                "unmappedProductCount", "dataQuality", "nayaxFees", "netReimbursement", "siteCommission",
                "netProfit", "netMarginPercent", "nayaxFeesExGst", "deliveryCosts", "packageCosts",
                "otherOperatingExpenses", "cardSales", "cashSales", "cardTransactionCount", "cashTransactionCount",
                "structuredOperatingExpenses", "operatingExpenseGst", "totalSales", "costOfGoodsSold",
                "partialCostOfGoods", "isCogsComplete", "uncostedTransactionCount", "uncostedSalesAmount",
                "averageSale", "grossMarginPercent", "nayaxFeesIncludingGst", "expectedReimbursement",
                "actualReimbursement", "reimbursementDifference", "isReconciled", "reconciliationStatus",
                "reconciliationTolerance", "adjustmentsSupported", "directProfit", "directMarginPercent",
                "nayaxProcessingFees",
            },
            PropertyNames(json));

        using var document = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("grossProfit").ValueKind);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("costOfGoodsSold").ValueKind);
        Assert.Equal(50m, document.RootElement.GetProperty("averageSale").GetDecimal());
    }

    [Fact]
    public void Machine_profitability_row_serializes_expected_properties_and_preserves_null_cogs_and_profit()
    {
        var row = new MachineProfitabilityRowDto(
            MachineId: 1, MachineName: "Machine A", Sales: 100m, Quantity: 3m,
            CostOfGoods: null, GrossProfit: null, MarginPercent: null, TransactionCount: 3);

        var json = JsonSerializer.Serialize(row, WebDefaults);

        Assert.Equal(
            new[]
            {
                "machineId", "machineName", "sales", "quantity", "costOfGoods", "grossProfit", "marginPercent",
                "transactionCount", "siteCommission", "directProfit", "directMarginPercent", "commissionPercent",
                "cardSales", "cashSales", "partialCostOfGoods", "isCogsComplete", "uncostedTransactionCount",
                "uncostedSalesAmount", "directOperatingExpenses", "nayaxProcessingFees",
            },
            PropertyNames(json));

        using var document = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("costOfGoods").ValueKind);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("grossProfit").ValueKind);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("marginPercent").ValueKind);
    }

    [Fact]
    public void Transaction_sales_report_serializes_filter_options_and_rows_without_flattening_cost_fields()
    {
        var row = new TransactionSalesRowDto(
            TransactionDate: new DateTime(2026, 7, 5), TransactionId: 42, MachineId: 1, MachineName: "Machine A",
            SiteId: null, SiteName: null, ProductId: null, ProductName: "Cola",
            PaymentType: "card", RawPaymentMethod: "Card", Sale: 3.5m,
            NayaxProductCostPrice: null, UnitCostAtSale: null, CostOfGoods: null,
            CostingStatus: "Uncosted", CostSource: "None",
            GrossProfit: null, GrossMarginPercent: null, DirectProfit: null, DirectMarginPercent: null,
            FeeExGst: null, FeeGst: null, FeeIncGst: null, FeeSource: "None",
            CommissionRate: null, CommissionBasis: null, CommissionAmount: null,
            TransactionStatusId: 12, TransactionStatus: "Completed", IsCompleted: true);
        var totals = new TransactionSalesTotalsDto(
            TransactionCount: 1, CompletedTransactionCount: 1, Sales: 3.5m, CardSales: 3.5m, CashSales: 0m,
            CostedCompletedTransactionCount: 0, UncostedCompletedTransactionCount: 1, IsCogsComplete: false,
            CostOfGoods: null, PartialCostOfGoods: 0m, GrossProfit: null, GrossMarginPercent: null,
            DirectProfit: null, DirectMarginPercent: null, PartialGrossProfit: null, PartialDirectProfit: null,
            EstimatedFeeExGst: 0m, EstimatedFeeGst: 0m, EstimatedFeeIncGst: 0m, CommissionAmount: 0m);
        var filterOptions = new TransactionSalesFilterOptionsDto(
            Sites: new[] { new TransactionSalesFilterOptionDto(1, "Site A") },
            Products: new[] { new TransactionSalesFilterOptionDto(2, "Cola") });
        var report = new TransactionSalesReportDto(
            new DateTime(2026, 7, 1), new DateTime(2026, 7, 31), new[] { row }, totals,
            new ReportingDataQualityDto(), Page: 1, PageSize: 50, TotalCount: 1, FilterOptions: filterOptions);

        var json = JsonSerializer.Serialize(report, WebDefaults);

        Assert.Equal(
            new[]
            {
                "from", "to", "rows", "totals", "dataQuality", "page", "pageSize", "totalCount", "filterOptions",
            },
            PropertyNames(json));

        using var document = JsonDocument.Parse(json);
        var jsonRow = document.RootElement.GetProperty("rows")[0];
        Assert.Equal(JsonValueKind.Null, jsonRow.GetProperty("costOfGoods").ValueKind);
        Assert.Equal(JsonValueKind.Null, jsonRow.GetProperty("grossProfit").ValueKind);
        Assert.Equal("Uncosted", jsonRow.GetProperty("costingStatus").GetString());
        var jsonFilterOptions = document.RootElement.GetProperty("filterOptions");
        Assert.Equal("Site A", jsonFilterOptions.GetProperty("sites")[0].GetProperty("name").GetString());
        Assert.Equal("Cola", jsonFilterOptions.GetProperty("products")[0].GetProperty("name").GetString());
    }

    [Fact]
    public void Daily_report_row_serializes_the_additive_row_level_fee_gst_field_alongside_the_existing_fee_fields()
    {
        var row = new DailyReportRowDto(
            new DateTime(2026, 7, 1), Sales: 100m, Quantity: 5m, CostOfGoods: 40m, GrossProfit: 60m, TransactionCount: 10,
            NayaxFeesExGst: 4m, NayaxFeesIncludingGst: 4.4m, NayaxFeesGst: 0.4m);

        var json = JsonSerializer.Serialize(row, WebDefaults);

        Assert.Equal(
            new[]
            {
                "date", "sales", "quantity", "costOfGoods", "grossProfit", "transactionCount", "grossSales",
                "cardSales", "cashSales", "averageSale", "isCogsComplete", "uncostedTransactionCount",
                "uncostedSalesAmount", "grossMarginPercent", "nayaxFeesExGst", "nayaxFeesIncludingGst",
                "nayaxFeesGst", "importedReimbursement", "netReimbursement", "isReconciled", "reconciliationStatus",
                "completedTransactionCount", "pendingTransactionCount", "declinedOrCancelledTransactionCount",
                "refundedTransactionCount", "unknownStatusTransactionCount", "nayaxFeeSource", "partialCostOfGoods",
            },
            PropertyNames(json));

        using var document = JsonDocument.Parse(json);
        Assert.Equal(4m, document.RootElement.GetProperty("nayaxFeesExGst").GetDecimal());
        Assert.Equal(4.4m, document.RootElement.GetProperty("nayaxFeesIncludingGst").GetDecimal());
        Assert.Equal(0.4m, document.RootElement.GetProperty("nayaxFeesGst").GetDecimal());
    }

    [Fact]
    public void Reconciliation_report_serializes_computed_defaults_and_nested_period_rows()
    {
        var report = new ReconciliationReportDto(
            new DateTime(2026, 7, 1), new DateTime(2026, 7, 31), NayaxSales: 1000m, ImportedReimbursement: 950m,
            Difference: 50m, Tolerance: 0.01m, IsMatch: false, DataQuality: new ReportingDataQualityDto());

        var json = JsonSerializer.Serialize(report, WebDefaults);

        using var document = JsonDocument.Parse(json);
        Assert.Equal("Mismatch", document.RootElement.GetProperty("grossStatus").GetString());
        Assert.Equal("Mismatch", document.RootElement.GetProperty("status").GetString());
        Assert.Equal("Pending", document.RootElement.GetProperty("settlementStatus").GetString());
        Assert.Equal(JsonValueKind.Array, document.RootElement.GetProperty("periodRows").ValueKind);
        Assert.Empty(document.RootElement.GetProperty("periodRows").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("totals").ValueKind);
        Assert.Equal(1000m, document.RootElement.GetProperty("totalVendingSales").GetDecimal());
        Assert.Equal(950m, document.RootElement.GetProperty("nayaxReportedGrossCardSales").GetDecimal());
    }
}
