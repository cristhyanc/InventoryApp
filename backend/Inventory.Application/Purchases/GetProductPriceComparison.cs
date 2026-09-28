using Inventory.Domain.Purchases;

namespace Inventory.Application.Purchases;

/// <summary>
/// The supplier-product price comparison use case (issue #63): retrieves a product's actual
/// Purchase-item cost history through <see cref="IProductPurchasePriceHistoryProvider"/> and applies
/// the Domain <see cref="SupplierPriceComparisonPolicy"/> to derive the authoritative lowest/latest
/// comparison and full history. The backend is authoritative for this calculation; callers (the API
/// controller and ultimately Angular) only fetch and present the result.
/// </summary>
public sealed class GetProductPriceComparison
{
    private readonly IProductPurchasePriceHistoryProvider _facts;

    public GetProductPriceComparison(IProductPurchasePriceHistoryProvider facts) => _facts = facts;

    public async Task<ProductPriceComparisonDto> Handle(long productId, CancellationToken cancellationToken)
    {
        var facts = await _facts.GetForProductAsync(productId, cancellationToken);
        var entries = facts
            .Select(fact => new SupplierPriceHistoryEntry(
                fact.PurchaseItemId,
                fact.PurchaseId,
                fact.PurchaseTitle,
                fact.PurchaseDate,
                fact.SupplierId,
                fact.SupplierName,
                fact.UnitCost))
            .ToList();

        var result = SupplierPriceComparisonPolicy.Evaluate(entries);

        return new ProductPriceComparisonDto(
            Map(result.Lowest),
            Map(result.Latest),
            result.AbsoluteDifference,
            result.PercentageDifference,
            result.PercentageIsMeaningful,
            result.HistoryNewestFirst.Select(entry => Map(entry)!).ToList());
    }

    private static ProductPriceHistoryEntryDto? Map(SupplierPriceHistoryEntry? entry) =>
        entry is null
            ? null
            : new ProductPriceHistoryEntryDto(
                entry.Value.PurchaseItemId,
                entry.Value.PurchaseId,
                entry.Value.PurchaseTitle,
                entry.Value.PurchaseDate,
                entry.Value.SupplierId,
                entry.Value.SupplierName,
                entry.Value.UnitCost);
}
