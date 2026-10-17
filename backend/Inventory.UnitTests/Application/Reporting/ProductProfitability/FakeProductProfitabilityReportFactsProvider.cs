using Inventory.Application.Reporting.ProductProfitability;
using Inventory.Application.Reporting.Shared;
using InventoryApi.Tests.Application.Reporting.Shared;

namespace InventoryApi.Tests.Application.Reporting.ProductProfitability;

/// <summary>
/// In-memory fake of the report facts port, so the product profitability use case's matching,
/// merging, and quality-note assembly can be tested without EF Core or SQLite.
/// </summary>
public sealed class FakeProductProfitabilityReportFactsProvider : IProductProfitabilityReportFactsProvider
{
    private readonly ProductProfitabilityReportFacts _facts;

    public FakeProductProfitabilityReportFactsProvider(ProductProfitabilityReportFacts facts) => _facts = facts;

    public (DateTime From, DateTime To, long? MachineId)? LastRequest { get; private set; }

    public Task<ProductProfitabilityReportFacts> GetFactsAsync(DateTime from, DateTime to, long? machineId, CancellationToken cancellationToken)
    {
        LastRequest = (from, to, machineId);
        return Task.FromResult(_facts);
    }

    public static ProductProfitabilityReportFacts Empty(ReportCoverageFacts? coverage = null) =>
        new(Array.Empty<ProductProfitabilitySaleGroupFacts>(), Array.Empty<ProductProfitabilityCatalogueEntry>())
        {
            Coverage = coverage ?? TestReportCoverage.Complete()
        };

    public static ProductProfitabilityReportFacts SingleMappedProduct(
        long productId = 1, string productName = "Water", decimal sales = 30m, decimal cost = 10m,
        bool isCogsComplete = true, ReportCoverageFacts? coverage = null) => new(
        [
            new ProductProfitabilitySaleGroupFacts(productId, productName, sales, 3m, cost, isCogsComplete,
                isCogsComplete ? 0 : 1, isCogsComplete ? 0m : sales, 3, sales, 0m)
        ],
        [new ProductProfitabilityCatalogueEntry(productId, productName, "Drinks")])
        {
            // Fully accounted for unless a test replaces it, so a test about something else is
            // never incidentally about provider coverage (issue #731).
            Coverage = coverage ?? TestReportCoverage.Complete()
        };
}
