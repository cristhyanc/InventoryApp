using Inventory.Application.Reporting.ProductProfitability;
using Inventory.Domain.Purchases;

namespace InventoryApi.Tests.Application.Reporting.ProductProfitability;

/// <summary>
/// In-memory fake of the bulk purchase-cost facts port, so the product profitability use case's
/// Last/Lowest Cost purchasing insight can be tested without EF Core or SQLite.
/// </summary>
public sealed class FakeProductPurchaseCostFactsProvider : IProductPurchaseCostFactsProvider
{
    private readonly IReadOnlyDictionary<long, IReadOnlyList<SupplierPriceHistoryEntry>> _factsByProduct;

    public FakeProductPurchaseCostFactsProvider(IReadOnlyDictionary<long, IReadOnlyList<SupplierPriceHistoryEntry>> factsByProduct) =>
        _factsByProduct = factsByProduct;

    public IReadOnlyCollection<long>? LastRequestedProductIds { get; private set; }

    public Task<IReadOnlyDictionary<long, IReadOnlyList<SupplierPriceHistoryEntry>>> GetForProductsAsync(
        IReadOnlyCollection<long> productIds, CancellationToken cancellationToken)
    {
        LastRequestedProductIds = productIds;
        return Task.FromResult(_factsByProduct);
    }

    public static FakeProductPurchaseCostFactsProvider Empty() =>
        new(new Dictionary<long, IReadOnlyList<SupplierPriceHistoryEntry>>());
}
