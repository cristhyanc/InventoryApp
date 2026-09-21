using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Inventory.Application.Reporting.ProductProfitability;

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

    public static ProductProfitabilityReportFacts Empty() =>
        new(Array.Empty<ProductProfitabilitySaleGroupFacts>(), Array.Empty<ProductProfitabilityCatalogueEntry>());

    public static ProductProfitabilityReportFacts SingleMappedProduct(
        long productId = 1, string productName = "Water", decimal sales = 30m, decimal cost = 10m,
        bool isCogsComplete = true) => new(
        [
            new ProductProfitabilitySaleGroupFacts(productId, productName, sales, 3m, cost, isCogsComplete,
                isCogsComplete ? 0 : 1, isCogsComplete ? 0m : sales, 3, sales, 0m)
        ],
        [new ProductProfitabilityCatalogueEntry(productId, productName, "Drinks")]);
}
