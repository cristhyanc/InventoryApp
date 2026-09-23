using Inventory.Application.Reporting.ProductProfitability;
using Inventory.Application.Reporting.Shared;
using Xunit;

namespace InventoryApi.Tests.Application.Reporting.ProductProfitability;

public class GetProductProfitabilityReportTests
{
    [Fact]
    public async Task Resolves_the_requested_range_and_machine_filter_and_forwards_them_to_the_port()
    {
        var provider = new FakeProductProfitabilityReportFactsProvider(FakeProductProfitabilityReportFactsProvider.Empty());
        var useCase = new GetProductProfitabilityReport(provider);

        await useCase.Handle(new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 31), MachineId: 10), CancellationToken.None);

        Assert.Equal((new DateTime(2025, 8, 1), new DateTime(2025, 8, 31), (long?)10), provider.LastRequest);
    }

    [Fact]
    public async Task Mapped_product_reports_cost_gross_profit_and_margin()
    {
        var facts = FakeProductProfitabilityReportFactsProvider.SingleMappedProduct(sales: 30m, cost: 10m);
        var useCase = new GetProductProfitabilityReport(new FakeProductProfitabilityReportFactsProvider(facts));

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
        var useCase = new GetProductProfitabilityReport(new FakeProductProfitabilityReportFactsProvider(facts));

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
        var useCase = new GetProductProfitabilityReport(new FakeProductProfitabilityReportFactsProvider(facts));

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
        var useCase = new GetProductProfitabilityReport(new FakeProductProfitabilityReportFactsProvider(facts));

        var report = await useCase.Handle(new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1)), CancellationToken.None);

        var row = Assert.Single(report.Rows);
        Assert.Equal("Unmapped product", row.ProductName);
    }

    [Fact]
    public async Task Incomplete_cogs_group_reports_null_profit_and_a_quality_note()
    {
        var facts = FakeProductProfitabilityReportFactsProvider.SingleMappedProduct(isCogsComplete: false);
        var useCase = new GetProductProfitabilityReport(new FakeProductProfitabilityReportFactsProvider(facts));

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
        var useCase = new GetProductProfitabilityReport(new FakeProductProfitabilityReportFactsProvider(facts));

        var report = await useCase.Handle(new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1)), CancellationToken.None);

        Assert.Equal(["High", "Low"], report.Rows.Select(x => x.ProductName).ToArray());
    }
}
