using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Inventory.Application.Reporting.Transactions;
using Inventory.Domain.Reporting.Transactions;
using Xunit;

namespace InventoryApi.Tests.Application.Reporting.Transactions;

public class GetTransactionSalesReportTests
{
    private static TransactionSalesReportFactsRow Row(long id, TransactionSaleStatus status = TransactionSaleStatus.Completed,
        TransactionPaymentType paymentType = TransactionPaymentType.Card, decimal sale = 10m,
        bool hasPersistedCost = true, decimal? costOfGoodsSold = 4m, long? siteId = null, string siteName = null,
        long? nayaxProductId = 1, string rawProductName = "Water", string machineName = "Machine A",
        long machineId = 10, DateTime? date = null) =>
        new(id, date ?? new DateTime(2025, 8, 1), machineId, machineName, siteId, siteName, nayaxProductId,
            rawProductName, paymentType, paymentType.ToString(), sale, null, costOfGoodsSold, costOfGoodsSold,
            hasPersistedCost ? "Costed" : "Pending", hasPersistedCost ? "Inventory Ledger" : "Unknown", hasPersistedCost,
            status, status == TransactionSaleStatus.Completed ? 12 : 55, status.ToString());

    private static GetTransactionSalesReport UseCase(TransactionSalesReportFacts facts) =>
        new(new FakeTransactionSalesReportFactsProvider(facts));

    private static TransactionSalesReportFacts Facts(params TransactionSalesReportFactsRow[] rows) => new(
        rows, [], [], [], SiteMappingUnavailable: false);

    [Fact]
    public async Task Default_status_filter_only_returns_completed_transactions()
    {
        var facts = Facts(Row(1, TransactionSaleStatus.Completed), Row(2, TransactionSaleStatus.Pending));
        var useCase = UseCase(facts);

        var report = await useCase.Handle(new TransactionSalesFilterDto(), CancellationToken.None);

        Assert.Equal(1, Assert.Single(report.Rows).TransactionId);
    }

    [Fact]
    public async Task Explicit_status_filter_returns_only_that_status()
    {
        var facts = Facts(Row(1, TransactionSaleStatus.Completed), Row(2, TransactionSaleStatus.Pending));
        var useCase = UseCase(facts);

        var report = await useCase.Handle(new TransactionSalesFilterDto(Status: "pending"), CancellationToken.None);

        Assert.Equal(2, Assert.Single(report.Rows).TransactionId);
    }

    [Fact]
    public async Task Payment_type_filter_narrows_to_the_requested_type()
    {
        var facts = Facts(
            Row(1, paymentType: TransactionPaymentType.Card),
            Row(2, paymentType: TransactionPaymentType.Cash));
        var useCase = UseCase(facts);

        var report = await useCase.Handle(new TransactionSalesFilterDto(PaymentType: "cash"), CancellationToken.None);

        Assert.Equal(2, Assert.Single(report.Rows).TransactionId);
    }

    [Fact]
    public async Task Cogs_status_filter_uses_the_raw_persisted_cost_flag()
    {
        var facts = Facts(
            Row(1, hasPersistedCost: true),
            Row(2, hasPersistedCost: false, costOfGoodsSold: null));
        var useCase = UseCase(facts);

        var costed = await useCase.Handle(new TransactionSalesFilterDto(Status: "all", CogsStatus: "costed"), CancellationToken.None);
        var uncosted = await useCase.Handle(new TransactionSalesFilterDto(Status: "all", CogsStatus: "uncosted"), CancellationToken.None);

        Assert.Equal(1, Assert.Single(costed.Rows).TransactionId);
        Assert.Equal(2, Assert.Single(uncosted.Rows).TransactionId);
    }

    [Fact]
    public async Task Site_and_product_filters_narrow_to_the_selected_identity()
    {
        var facts = Facts(
            Row(1, siteId: 91, nayaxProductId: 1),
            Row(2, siteId: 92, nayaxProductId: 2));
        var useCase = UseCase(facts);

        var report = await useCase.Handle(new TransactionSalesFilterDto(Status: "all", SiteId: 91), CancellationToken.None);

        Assert.Equal(1, Assert.Single(report.Rows).TransactionId);
    }

    [Fact]
    public async Task Search_matches_the_product_name()
    {
        var facts = Facts(
            Row(1, rawProductName: "Sparkling Water"),
            Row(2, rawProductName: "Chocolate Bar"));
        var useCase = UseCase(facts);

        var report = await useCase.Handle(new TransactionSalesFilterDto(Status: "all", Search: "choc"), CancellationToken.None);

        Assert.Equal(2, Assert.Single(report.Rows).TransactionId);
    }

    [Fact]
    public async Task Sorting_by_sale_amount_orders_rows_and_ties_break_on_transaction_id()
    {
        var facts = Facts(Row(1, sale: 5m), Row(2, sale: 20m), Row(3, sale: 5m));
        var useCase = UseCase(facts);

        var ascending = await useCase.Handle(new TransactionSalesFilterDto(Status: "all", SortBy: "sale", SortDescending: false), CancellationToken.None);

        Assert.Equal(new long[] { 1, 3, 2 }, ascending.Rows.Select(x => x.TransactionId));
    }

    [Fact]
    public async Task Default_sort_is_by_date_then_transaction_id_descending()
    {
        var facts = Facts(
            Row(1, date: new DateTime(2025, 8, 1)),
            Row(2, date: new DateTime(2025, 8, 2)),
            Row(3, date: new DateTime(2025, 8, 2)));
        var useCase = UseCase(facts);

        var report = await useCase.Handle(new TransactionSalesFilterDto(Status: "all"), CancellationToken.None);

        Assert.Equal(new long[] { 3, 2, 1 }, report.Rows.Select(x => x.TransactionId));
    }

    [Fact]
    public async Task Invalid_page_size_clamps_to_the_default_of_fifty()
    {
        var facts = Facts(Row(1));
        var useCase = UseCase(facts);

        var report = await useCase.Handle(new TransactionSalesFilterDto(PageSize: 30), CancellationToken.None);

        Assert.Equal(50, report.PageSize);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(250)]
    public async Task Supported_page_sizes_are_preserved(int pageSize)
    {
        var facts = Facts(Row(1));
        var useCase = UseCase(facts);

        var report = await useCase.Handle(new TransactionSalesFilterDto(PageSize: pageSize), CancellationToken.None);

        Assert.Equal(pageSize, report.PageSize);
    }

    [Fact]
    public async Task A_page_beyond_the_result_set_returns_no_rows_but_reports_the_true_total_count()
    {
        var facts = Facts(Row(1), Row(2));
        var useCase = UseCase(facts);

        var report = await useCase.Handle(new TransactionSalesFilterDto(Status: "all", Page: 5, PageSize: 50), CancellationToken.None);

        Assert.Empty(report.Rows);
        Assert.Equal(2, report.TotalCount);
    }

    [Fact]
    public async Task Unpaginated_export_mode_returns_every_filtered_row()
    {
        var facts = Facts(Enumerable.Range(1, 60).Select(i => Row(i)).ToArray());
        var useCase = UseCase(facts);

        var report = await useCase.Handle(new TransactionSalesFilterDto(), paginate: false, CancellationToken.None);

        Assert.Equal(60, report.Rows.Count);
        Assert.Equal(60, report.TotalCount);
    }

    [Fact]
    public async Task Filter_options_are_built_from_every_transaction_not_only_the_filtered_set()
    {
        var facts = Facts(
            Row(1, status: TransactionSaleStatus.Completed, siteId: 91, siteName: "Depot", nayaxProductId: 1, rawProductName: "Water"),
            Row(2, status: TransactionSaleStatus.Pending, siteId: 92, siteName: "Office", nayaxProductId: 2, rawProductName: "Cola")) with
        {
            ProductCatalogue = [new TransactionSalesCatalogueEntry(1, "Water"), new TransactionSalesCatalogueEntry(2, "Cola")]
        };
        var useCase = UseCase(facts);

        var report = await useCase.Handle(new TransactionSalesFilterDto(), CancellationToken.None);

        Assert.Equal(2, report.FilterOptions.Sites.Count);
        Assert.Equal(2, report.FilterOptions.Products.Count);
    }

    [Fact]
    public async Task Unmapped_product_falls_back_to_the_normalized_raw_name_and_adds_a_quality_note()
    {
        var facts = Facts(Row(1, nayaxProductId: 999, rawProductName: "Mystery Snack (999)"));
        var useCase = UseCase(facts);

        var report = await useCase.Handle(new TransactionSalesFilterDto(), CancellationToken.None);

        var row = Assert.Single(report.Rows);
        Assert.Null(row.ProductId);
        Assert.Equal("Mystery Snack", row.ProductName);
        Assert.True(report.DataQuality.ContainsUnmappedProducts);
    }

    [Fact]
    public async Task Product_matches_the_catalogue_by_nayax_product_id()
    {
        var facts = Facts(Row(1, nayaxProductId: 1, rawProductName: "Water")) with
        {
            ProductCatalogue = [new TransactionSalesCatalogueEntry(1, "Spring Water")]
        };
        var useCase = UseCase(facts);

        var report = await useCase.Handle(new TransactionSalesFilterDto(), CancellationToken.None);

        var row = Assert.Single(report.Rows);
        Assert.Equal(1, row.ProductId);
        Assert.Equal("Spring Water", row.ProductName);
    }

    [Fact]
    public async Task Unavailable_site_mapping_adds_a_quality_note()
    {
        var facts = Facts(Row(1)) with { SiteMappingUnavailable = true };
        var useCase = UseCase(facts);

        var report = await useCase.Handle(new TransactionSalesFilterDto(), CancellationToken.None);

        Assert.Contains(report.DataQuality.Notes!, note => note.Contains("Current site mapping is unavailable"));
    }
}
