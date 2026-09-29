using Inventory.Application.Reporting.ProductProfitability;
using Inventory.Application.Reporting.Shared;
using Inventory.Domain.Purchases;
using Xunit;

namespace InventoryApi.Tests.Application.Reporting.ProductProfitability;

public class GetProductProfitabilityReportTests
{
    [Fact]
    public async Task Resolves_the_requested_range_and_machine_filter_and_forwards_them_to_the_port()
    {
        var provider = new FakeProductProfitabilityReportFactsProvider(FakeProductProfitabilityReportFactsProvider.Empty());
        var useCase = new GetProductProfitabilityReport(provider, FakeProductPurchaseCostFactsProvider.Empty());

        await useCase.Handle(new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 31), MachineId: 10), CancellationToken.None);

        Assert.Equal((new DateTime(2025, 8, 1), new DateTime(2025, 8, 31), (long?)10), provider.LastRequest);
    }

    [Fact]
    public async Task Mapped_product_reports_cost_gross_profit_and_margin()
    {
        var facts = FakeProductProfitabilityReportFactsProvider.SingleMappedProduct(sales: 30m, cost: 10m);
        var useCase = new GetProductProfitabilityReport(new FakeProductProfitabilityReportFactsProvider(facts), FakeProductPurchaseCostFactsProvider.Empty());

        var report = await useCase.Handle(new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1)), CancellationToken.None);

        var row = Assert.Single(report.Rows);
        Assert.False(row.IsUnmapped);
        Assert.Equal(20m, row.GrossProfit);
        Assert.Equal("Drinks", row.CategoryName);
        Assert.False(report.DataQuality.ContainsUnmappedProducts);
        Assert.True(report.DataQuality.MissingStatus);
        Assert.False(report.DataQuality.HistoricalCostUnavailable);
        Assert.True(report.DataQuality.GstClassificationMissing);
        Assert.True(report.DataQuality.CommissionNotPersisted);
    }

    [Fact]
    public async Task Two_raw_sale_groups_that_match_the_same_product_are_merged_into_one_row()
    {
        var facts = new ProductProfitabilityReportFacts(
            [
                new ProductProfitabilitySaleGroupFacts(1, "Water", 3m, 1m, 1m, true, 0, 0m, 1, 3m, 0m),
                new ProductProfitabilitySaleGroupFacts(999, "Water (999)", 3m, 1m, 1m, true, 0, 0m, 1, 3m, 0m)
            ],
            [new ProductProfitabilityCatalogueEntry(1, "Water", null)]);
        var useCase = new GetProductProfitabilityReport(new FakeProductProfitabilityReportFactsProvider(facts), FakeProductPurchaseCostFactsProvider.Empty());

        var report = await useCase.Handle(new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1)), CancellationToken.None);

        var row = Assert.Single(report.Rows);
        Assert.Equal(1, row.ProductId);
        Assert.Equal(6m, row.Sales);
        Assert.Equal(2m, row.CostOfGoods);
        Assert.Equal(2, row.TransactionCount);
    }

    [Fact]
    public async Task Unmapped_sale_group_is_reported_as_unmapped_with_a_quality_note()
    {
        var facts = new ProductProfitabilityReportFacts(
            [new ProductProfitabilitySaleGroupFacts(999, "Unknown Item", 5m, 1m, 0m, false, 1, 5m, 1, 0m, 0m)],
            []);
        var useCase = new GetProductProfitabilityReport(new FakeProductProfitabilityReportFactsProvider(facts), FakeProductPurchaseCostFactsProvider.Empty());

        var report = await useCase.Handle(new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1)), CancellationToken.None);

        var row = Assert.Single(report.Rows);
        Assert.True(row.IsUnmapped);
        Assert.False(row.HistoricalCostAvailable);
        Assert.Null(row.GrossProfit);
        Assert.Null(row.MarginPercent);
        Assert.True(report.DataQuality.ContainsUnmappedProducts);
        Assert.Contains(report.DataQuality.Notes!, x => x.Contains("could not be mapped to a Product"));
    }

    [Fact]
    public async Task Blank_raw_name_with_no_id_match_is_unmapped_product_not_a_normalized_empty_name()
    {
        var facts = new ProductProfitabilityReportFacts(
            [new ProductProfitabilitySaleGroupFacts(null, "   ", 5m, 1m, 2m, true, 0, 0m, 1, 5m, 0m)],
            []);
        var useCase = new GetProductProfitabilityReport(new FakeProductProfitabilityReportFactsProvider(facts), FakeProductPurchaseCostFactsProvider.Empty());

        var report = await useCase.Handle(new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1)), CancellationToken.None);

        var row = Assert.Single(report.Rows);
        Assert.Equal("Unmapped product", row.ProductName);
    }

    [Fact]
    public async Task Incomplete_cogs_group_reports_null_profit_and_a_quality_note()
    {
        var facts = FakeProductProfitabilityReportFactsProvider.SingleMappedProduct(isCogsComplete: false);
        var useCase = new GetProductProfitabilityReport(new FakeProductProfitabilityReportFactsProvider(facts), FakeProductPurchaseCostFactsProvider.Empty());

        var report = await useCase.Handle(new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1)), CancellationToken.None);

        var row = Assert.Single(report.Rows);
        Assert.False(row.IsCogsComplete);
        Assert.Null(row.GrossProfit);
        Assert.True(report.DataQuality.HistoricalCostUnavailable);
        Assert.Contains(report.DataQuality.Notes!, x => x.Contains("no persisted COGS"));
    }

    [Fact]
    public async Task Rows_are_ordered_by_sales_descending()
    {
        var facts = new ProductProfitabilityReportFacts(
            [
                new ProductProfitabilitySaleGroupFacts(1, "Low", 10m, 1m, 1m, true, 0, 0m, 1, 10m, 0m),
                new ProductProfitabilitySaleGroupFacts(2, "High", 50m, 1m, 1m, true, 0, 0m, 1, 50m, 0m)
            ],
            [new ProductProfitabilityCatalogueEntry(1, "Low", null), new ProductProfitabilityCatalogueEntry(2, "High", null)]);
        var useCase = new GetProductProfitabilityReport(new FakeProductProfitabilityReportFactsProvider(facts), FakeProductPurchaseCostFactsProvider.Empty());

        var report = await useCase.Handle(new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1)), CancellationToken.None);

        Assert.Equal(["High", "Low"], report.Rows.Select(x => x.ProductName).ToArray());
    }

    private static SupplierPriceHistoryEntry Entry(
        int purchaseItemId, DateTime purchaseDate, string? supplierName, decimal unitCost, int purchaseId = 1) =>
        new(purchaseItemId, purchaseId, "Order", purchaseDate, supplierName is null ? null : purchaseItemId, supplierName, unitCost);

    [Fact]
    public async Task Mapped_product_reports_last_and_lowest_purchase_cost_with_their_suppliers_and_the_saving()
    {
        var facts = FakeProductProfitabilityReportFactsProvider.SingleMappedProduct(productId: 1);
        var purchaseCostFacts = new FakeProductPurchaseCostFactsProvider(new Dictionary<long, IReadOnlyList<SupplierPriceHistoryEntry>>
        {
            [1] = new[]
            {
                Entry(1, new DateTime(2026, 1, 5), "Woolworths", 2.05m),
                Entry(2, new DateTime(2026, 3, 5), "Acme Supplies", 2.40m)
            }
        });
        var useCase = new GetProductProfitabilityReport(new FakeProductProfitabilityReportFactsProvider(facts), purchaseCostFacts);

        var report = await useCase.Handle(new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1)), CancellationToken.None);

        var row = Assert.Single(report.Rows);
        Assert.Equal(2.40m, row.LastCost);
        Assert.Equal("Acme Supplies", row.LastCostSupplierName);
        Assert.Equal(2.05m, row.LowestCost);
        Assert.Equal("Woolworths", row.LowestCostSupplierName);
        Assert.Equal(0.35m, row.SavingPerUnit);
    }

    [Fact]
    public async Task Equal_last_and_lowest_purchase_cost_reports_a_zero_saving_not_an_unavailable_one()
    {
        var facts = FakeProductProfitabilityReportFactsProvider.SingleMappedProduct(productId: 1);
        var purchaseCostFacts = new FakeProductPurchaseCostFactsProvider(new Dictionary<long, IReadOnlyList<SupplierPriceHistoryEntry>>
        {
            [1] = new[] { Entry(1, new DateTime(2026, 1, 5), "Acme Supplies", 2.00m) }
        });
        var useCase = new GetProductProfitabilityReport(new FakeProductProfitabilityReportFactsProvider(facts), purchaseCostFacts);

        var report = await useCase.Handle(new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1)), CancellationToken.None);

        var row = Assert.Single(report.Rows);
        Assert.Equal(2.00m, row.LastCost);
        Assert.Equal(2.00m, row.LowestCost);
        Assert.Equal(0m, row.SavingPerUnit);
    }

    [Fact]
    public async Task No_recorded_purchase_history_reports_null_cost_supplier_and_saving_not_zero()
    {
        var facts = FakeProductProfitabilityReportFactsProvider.SingleMappedProduct(productId: 1);
        var useCase = new GetProductProfitabilityReport(new FakeProductProfitabilityReportFactsProvider(facts), FakeProductPurchaseCostFactsProvider.Empty());

        var report = await useCase.Handle(new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1)), CancellationToken.None);

        var row = Assert.Single(report.Rows);
        Assert.Null(row.LastCost);
        Assert.Null(row.LastCostSupplierName);
        Assert.Null(row.LowestCost);
        Assert.Null(row.LowestCostSupplierName);
        Assert.Null(row.SavingPerUnit);
    }

    [Fact]
    public async Task A_purchase_with_no_recorded_supplier_reports_a_null_supplier_name_per_issue_63_semantics()
    {
        var facts = FakeProductProfitabilityReportFactsProvider.SingleMappedProduct(productId: 1);
        var purchaseCostFacts = new FakeProductPurchaseCostFactsProvider(new Dictionary<long, IReadOnlyList<SupplierPriceHistoryEntry>>
        {
            [1] = new[] { Entry(1, new DateTime(2026, 1, 5), null, 2.00m) }
        });
        var useCase = new GetProductProfitabilityReport(new FakeProductProfitabilityReportFactsProvider(facts), purchaseCostFacts);

        var report = await useCase.Handle(new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1)), CancellationToken.None);

        var row = Assert.Single(report.Rows);
        Assert.Equal(2.00m, row.LastCost);
        Assert.Null(row.LastCostSupplierName);
    }

    [Fact]
    public async Task Equal_lowest_cost_ties_break_to_the_earliest_occurrence_matching_issue_63_policy()
    {
        var facts = FakeProductProfitabilityReportFactsProvider.SingleMappedProduct(productId: 1);
        var purchaseCostFacts = new FakeProductPurchaseCostFactsProvider(new Dictionary<long, IReadOnlyList<SupplierPriceHistoryEntry>>
        {
            [1] = new[]
            {
                Entry(1, new DateTime(2026, 1, 5), "First Supplier", 1.50m),
                Entry(2, new DateTime(2026, 3, 5), "Second Supplier", 1.50m)
            }
        });
        var useCase = new GetProductProfitabilityReport(new FakeProductProfitabilityReportFactsProvider(facts), purchaseCostFacts);

        var report = await useCase.Handle(new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1)), CancellationToken.None);

        var row = Assert.Single(report.Rows);
        Assert.Equal("First Supplier", row.LowestCostSupplierName);
        Assert.Equal(0m, row.SavingPerUnit);
    }

    [Fact]
    public async Task An_unmapped_sale_group_never_gets_a_purchase_cost_insight_from_its_raw_nayax_identifier()
    {
        var facts = new ProductProfitabilityReportFacts(
            [new ProductProfitabilitySaleGroupFacts(1, "Unknown Item", 5m, 1m, 0m, false, 1, 5m, 1, 0m, 0m)],
            []);
        var purchaseCostFacts = new FakeProductPurchaseCostFactsProvider(new Dictionary<long, IReadOnlyList<SupplierPriceHistoryEntry>>
        {
            [1] = new[] { Entry(1, new DateTime(2026, 1, 5), "Acme Supplies", 2.00m) }
        });
        var useCase = new GetProductProfitabilityReport(new FakeProductProfitabilityReportFactsProvider(facts), purchaseCostFacts);

        var report = await useCase.Handle(new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1)), CancellationToken.None);

        var row = Assert.Single(report.Rows);
        Assert.True(row.IsUnmapped);
        Assert.Null(row.LastCost);
        Assert.Null(row.LowestCost);
    }

    [Fact]
    public async Task Multiple_mapped_products_purchase_cost_facts_are_fetched_in_one_bulk_call_not_per_product()
    {
        var facts = new ProductProfitabilityReportFacts(
            [
                new ProductProfitabilitySaleGroupFacts(1, "Low", 10m, 1m, 1m, true, 0, 0m, 1, 10m, 0m),
                new ProductProfitabilitySaleGroupFacts(2, "High", 50m, 1m, 1m, true, 0, 0m, 1, 50m, 0m)
            ],
            [new ProductProfitabilityCatalogueEntry(1, "Low", null), new ProductProfitabilityCatalogueEntry(2, "High", null)]);
        var purchaseCostFacts = FakeProductPurchaseCostFactsProvider.Empty();
        var useCase = new GetProductProfitabilityReport(new FakeProductProfitabilityReportFactsProvider(facts), purchaseCostFacts);

        await useCase.Handle(new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1)), CancellationToken.None);

        Assert.Equal(new long[] { 1, 2 }, purchaseCostFacts.LastRequestedProductIds!.OrderBy(x => x).ToArray());
    }

    [Fact]
    public async Task Revenue_cost_of_goods_gross_profit_and_margin_are_unaffected_by_purchase_cost_insights()
    {
        var facts = FakeProductProfitabilityReportFactsProvider.SingleMappedProduct(productId: 1, sales: 30m, cost: 10m);
        var purchaseCostFacts = new FakeProductPurchaseCostFactsProvider(new Dictionary<long, IReadOnlyList<SupplierPriceHistoryEntry>>
        {
            [1] = new[] { Entry(1, new DateTime(2026, 1, 5), "Acme Supplies", 1_000_000m) }
        });
        var useCase = new GetProductProfitabilityReport(new FakeProductProfitabilityReportFactsProvider(facts), purchaseCostFacts);

        var report = await useCase.Handle(new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1)), CancellationToken.None);

        var row = Assert.Single(report.Rows);
        Assert.Equal(30m, row.Sales);
        Assert.Equal(10m, row.CostOfGoods);
        Assert.Equal(20m, row.GrossProfit);
    }
}
