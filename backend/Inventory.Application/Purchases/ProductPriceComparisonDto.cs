namespace Inventory.Application.Purchases;

/// <summary>
/// One entry of a product's actual Purchase-item price history, as returned to the API/frontend.
/// <see cref="SupplierName"/> is null exactly when the source Purchase has no supplier recorded;
/// callers must present that explicitly (for example "None"/"Unknown"), never omit the row.
/// </summary>
public sealed record ProductPriceHistoryEntryDto(
    int PurchaseItemId,
    int PurchaseId,
    string PurchaseTitle,
    DateTime PurchaseDate,
    int? SupplierId,
    string? SupplierName,
    decimal UnitCost);

/// <summary>
/// The supplier-product price comparison for one product (issue #63): its lowest and most recent
/// recorded actual Purchase unit cost, the absolute/percentage difference between them, and every
/// recorded entry newest-first for the drill-down view. Null <see cref="Lowest"/>/<see cref="Latest"/>
/// means the product has no Purchase-item history at all.
/// </summary>
public sealed record ProductPriceComparisonDto(
    ProductPriceHistoryEntryDto? Lowest,
    ProductPriceHistoryEntryDto? Latest,
    decimal? AbsoluteDifference,
    decimal? PercentageDifference,
    bool PercentageIsMeaningful,
    IReadOnlyList<ProductPriceHistoryEntryDto> HistoryNewestFirst);
